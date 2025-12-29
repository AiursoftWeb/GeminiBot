using Aiursoft.CSTools.Services;
using Aiursoft.GitRunner;
using Aiursoft.GitRunner.Models;
using Aiursoft.GeminiBot.Configuration;
using Aiursoft.GeminiBot.Models;
using Aiursoft.NugetNinja.GitServerBase.Models;
using Aiursoft.NugetNinja.GitServerBase.Models.Abstractions;
using Aiursoft.NugetNinja.GitServerBase.Services;
using Aiursoft.NugetNinja.GitServerBase.Services.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json.Serialization;

namespace Aiursoft.GeminiBot.Services;

/// <summary>
/// Handles checking and fixing failed merge requests.
/// Ensures bot's own PRs pass CI/CD before processing new issues.
/// </summary>
public class MergeRequestProcessor
{
    private readonly LocalizationService _localizationService;
    private readonly IVersionControlService _versionControl;
    private readonly WorkspaceManager _workspaceManager;
    private readonly HttpWrapper _httpWrapper;
    private readonly GeminiCliService _geminiCliService;
    private readonly CommandService _commandService;
    private readonly GeminiBotOptions _options;
    private readonly ILogger<MergeRequestProcessor> _logger;

    public MergeRequestProcessor(
        LocalizationService localizationService,
        IVersionControlService versionControl,
        WorkspaceManager workspaceManager,
        HttpWrapper httpWrapper,
        GeminiCliService geminiCliService,
        CommandService commandService,
        IOptions<GeminiBotOptions> options,
        ILogger<MergeRequestProcessor> logger)
    {
        _localizationService = localizationService;
        _versionControl = versionControl;
        _workspaceManager = workspaceManager;
        _httpWrapper = httpWrapper;
        _geminiCliService = geminiCliService;
        _commandService = commandService;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Main method to process all open merge requests for the bot user.
    /// Checks pipeline status, downloads failure logs, and invokes Gemini to fix.
    /// </summary>
    public async Task<ProcessResult> ProcessMergeRequestsAsync(Server server)
    {
        try
        {
            IReadOnlyCollection<MergeRequestSearchResult> mergeRequests;
            var targetBranches = new Dictionary<int, string>();
            if (server.Provider == "GitLab")
            {
                _logger.LogInformation("Checking merge requests assigned to {UserName}...", server.UserName);
                var url = $"{server.EndPoint.TrimEnd('/')}/api/v4/merge_requests?scope=assigned_to_me&state=opened&per_page=100";
                var gitLabMrs = await _httpWrapper.SendHttpAndGetJson<List<GitLabMergeRequestDto>>(url, HttpMethod.Get, server.Token);

                foreach (var m in gitLabMrs)
                {
                    targetBranches[m.Iid] = m.TargetBranch;
                }

                mergeRequests = gitLabMrs.Select(m => new MergeRequestSearchResult
                {
                    IID = m.Iid,
                    Title = m.Title,
                    ProjectId = m.ProjectId,
                    SourceProjectId = m.SourceProjectId,
                    SourceBranch = m.SourceBranch
                }).ToList();
            }
            else
            {
                _logger.LogInformation("Checking merge requests submitted by {UserName}...", server.UserName);
                mergeRequests = await _versionControl.GetOpenMergeRequests(
                    server.EndPoint,
                    server.UserName,
                    server.Token);
            }

            var mrsToProcess = new List<MRToProcess>();

            foreach (var mr in mergeRequests)
            {
                _logger.LogInformation("Checking MR #{IID}: {Title}...", mr.IID, mr.Title);

                var details = await _versionControl.GetMergeRequestDetails(
                    server.EndPoint,
                    server.UserName,
                    server.Token,
                    mr.ProjectId,
                    mr.IID);

                var hasConflicts = details.HasConflicts;
                var hasNewHumanReview = await ShouldProcessDueToReviewAsync(server, mr);
                var pipelineFailed = details.Pipeline?.Status == "failed";
                var targetBranch = targetBranches.GetValueOrDefault(mr.IID, "main");

                if (hasConflicts || hasNewHumanReview || pipelineFailed)
                {
                    _logger.LogWarning("MR #{IID} needs attention. Conflict: {HasConflicts}, New Human Review: {HasNewReview}, Pipeline Failed: {PipelineFailed}",
                        mr.IID, hasConflicts, hasNewHumanReview, pipelineFailed);
                    
                    mrsToProcess.Add(new MRToProcess
                    {
                        SearchResult = mr,
                        Details = details,
                        HasConflicts = hasConflicts,
                        HasNewHumanReview = hasNewHumanReview,
                        PipelineFailed = pipelineFailed,
                        TargetBranch = targetBranch
                    });
                }
                else
                {
                    _logger.LogInformation("MR #{IID} is healthy. Pipeline: {Status}, Conflicts: {HasConflicts}, No new human review.",
                        mr.IID, details.Pipeline?.Status ?? "null", hasConflicts);
                }
            }

            if (mrsToProcess.Count == 0)
            {
                _logger.LogInformation("No merge requests need attention. All clear!");
                return ProcessResult.Succeeded("No MRs to fix");
            }

            _logger.LogInformation("Found {Count} merge requests to fix", mrsToProcess.Count);

            // Process each MR
            foreach (var item in mrsToProcess)
            {
                await CheckAndFixMergeRequestAsync(item, server);
            }

            return ProcessResult.Succeeded($"Processed {mrsToProcess.Count} MRs");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing merge requests");
            return ProcessResult.Failed("Error processing merge requests", ex);
        }
    }

    private class MRToProcess
    {
        public required MergeRequestSearchResult SearchResult { get; init; }
        public required DetailedMergeRequest Details { get; init; }
        public bool HasConflicts { get; init; }
        public bool HasNewHumanReview { get; init; }
        public bool PipelineFailed { get; init; }
        public string TargetBranch { get; init; } = "main";
    }

    /// <summary>
    /// Check a single MR, download logs or discussions, and invoke Gemini to fix.
    /// </summary>
    private async Task CheckAndFixMergeRequestAsync(
        MRToProcess item,
        Server server)
    {
        var mr = item.SearchResult;
        var details = item.Details;
        try
        {
            _logger.LogInformation("Processing MR #{IID}: {Title}", mr.IID, mr.Title);

            // CRITICAL: Pipeline runs in the SOURCE project (fork), not the target project!
            var pipelineProjectId = mr.SourceProjectId > 0 ? mr.SourceProjectId : mr.ProjectId;
            _logger.LogInformation("MR #{IID}: Using project ID {ProjectId} for operations (source: {SourceProjectId}, target: {TargetProjectId})",
                mr.IID, pipelineProjectId, mr.SourceProjectId, mr.ProjectId);

            // Get repository details from SOURCE project (where the branch exists)
            // The MR branch is in the fork, not in the target project!
            _logger.LogInformation("Getting repository details from source project {ProjectId}...", pipelineProjectId);
            var repository = await _versionControl.GetRepository(
                server.EndPoint,
                pipelineProjectId.ToString(),
                string.Empty,
                server.Token);

            var failureLogs = string.Empty;
            var reviewNotes = string.Empty;
            string prompt;
            string commitMessage;

            if (item.HasConflicts)
            {
                prompt = BuildConflictPrompt(mr, item.TargetBranch);
                commitMessage = $"Resolve merge conflicts for MR #{mr.IID} by merging {item.TargetBranch}\n\nAutomatically generated fix by Gemini Bot.";
            }
            else if (item.HasNewHumanReview)
            {
                reviewNotes = await GetFormattedDiscussionsAsync(server, mr);
                prompt = BuildReviewPrompt(mr, reviewNotes);
                commitMessage = $"Address human review for MR #{mr.IID}\n\nAutomatically generated fix by Gemini Bot.";
            }
            else if (item.PipelineFailed)
            {
                if (details.Pipeline == null || details.Pipeline.Id <= 0)
                {
                    _logger.LogWarning("MR #{IID} is marked as pipeline failed but has no valid pipeline ID", mr.IID);
                    return;
                }
                failureLogs = await GetFailureLogsAsync(server, pipelineProjectId, details.Pipeline.Id);
                prompt = BuildFailurePrompt(mr, details, failureLogs);
                commitMessage = $"Fix pipeline failure for MR #{mr.IID}\n\nAutomatically generated fix by Gemini Bot.";
            }
            else
            {
                _logger.LogWarning("MR #{IID} was marked for processing but no action identified.", mr.IID);
                return;
            }

            if (string.IsNullOrWhiteSpace(failureLogs) && string.IsNullOrWhiteSpace(reviewNotes) && !item.HasConflicts)
            {
                _logger.LogWarning("No logs, no review notes and no conflicts found for MR #{IID}", mr.IID);
                return;
            }

            // Clone the repository and checkout the MR branch
            var workPath = GetWorkspacePath(mr, repository);
            _logger.LogInformation("Cloning repository for MR #{IID} to {WorkPath}...", mr.IID, workPath);

            // Get the source branch from the MR
            var branchName = mr.SourceBranch ?? throw new InvalidOperationException($"MR #{mr.IID} has no source branch");

            await _workspaceManager.ResetRepo(
                workPath,
                branchName, // Checkout the MR's source branch
                repository.CloneUrl ?? throw new InvalidOperationException($"Repository clone URL is null for MR {mr.IID}"),
                CloneMode.Full,
                $"{server.UserName}:{server.Token}");

            // Set user config right after reset so Gemini can commit if needed
            await _workspaceManager.SetUserConfig(workPath, server.DisplayName, server.UserEmail);

            _logger.LogInformation("Invoking Gemini CLI to fix MR #{IID}...", mr.IID);

            var geminiSuccess = await _geminiCliService.InvokeGeminiCliAsync(workPath, prompt, hideGitFolder: false);

            // Run localization if enabled
            _logger.LogInformation("Checking for localization requirements...");
            await _localizationService.LocalizeProjectAsync(workPath);
            if (!geminiSuccess)
            {
                _logger.LogWarning("Gemini CLI failed to process MR #{IID}. But continue to proceed possible localization updates.", mr.IID);
            }

            // Wait for Gemini to finish
            await Task.Delay(1000);

            // Check for both pending changes and unpushed commits
            var hasPendingChanges = await _workspaceManager.PendingCommit(workPath);
            var isAheadOfOrigin = await IsAheadOfOrigin(workPath, branchName);

            if (!hasPendingChanges && !isAheadOfOrigin)
            {
                _logger.LogInformation("MR #{IID} - No changes detected (no pending changes and not ahead of origin)", mr.IID);
                return;
            }

            // Commit pending changes if any exist
            if (hasPendingChanges)
            {
                _logger.LogInformation("MR #{IID} has pending changes. Committing...", mr.IID);

                var saved = await _workspaceManager.CommitToBranch(workPath, commitMessage, branchName);
                if (!saved)
                {
                    _logger.LogError("Failed to commit changes for MR #{IID}", mr.IID);
                    return;
                }
            }
            else
            {
                _logger.LogInformation("MR #{IID} - No pending changes, but HEAD is ahead of origin. Will push existing commits.", mr.IID);
            }

            // Push to the MR's source branch
            var pushPath = _versionControl.GetPushPath(server, repository);
            await _workspaceManager.Push(workPath, branchName, pushPath, force: true);

            _logger.LogInformation("Successfully fixed and pushed changes for MR #{IID}", mr.IID);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fixing MR #{IID}", mr.IID);
        }
    }

    /// <summary>
    /// Download failure logs from all failed jobs in the pipeline.
    /// </summary>
    private async Task<string> GetFailureLogsAsync(Server server, int projectId, int pipelineId)
    {
        try
        {
            _logger.LogInformation("Fetching jobs for pipeline {PipelineId} in project {ProjectId}...", pipelineId, projectId);

            var jobs = await _versionControl.GetPipelineJobs(
                server.EndPoint,
                server.Token,
                projectId,
                pipelineId);

            if (jobs.Count == 0)
            {
                _logger.LogWarning("Pipeline {PipelineId} has no jobs (may have been deleted or not started yet)", pipelineId);
                return string.Empty;
            }

            var failedJobs = jobs.Where(j => j.Status == "failed").ToList();

            if (failedJobs.Count == 0)
            {
                _logger.LogWarning("Pipeline {PipelineId} has no failed jobs", pipelineId);
                return string.Empty;
            }

            _logger.LogInformation("Found {Count} failed jobs in pipeline {PipelineId}", failedJobs.Count, pipelineId);

            var allLogs = new StringBuilder();

            foreach (var job in failedJobs)
            {
                _logger.LogInformation("Downloading log for failed job: {JobName} (ID: {JobId})", job.Name, job.Id);

                var log = await _versionControl.GetJobLog(
                    server.EndPoint,
                    server.Token,
                    projectId,
                    job.Id);

                if (!string.IsNullOrWhiteSpace(log))
                {
                    allLogs.AppendLine($"\n\n=== Job: {job.Name} (Stage: {job.Stage}) ===");
                    allLogs.AppendLine(log);
                    allLogs.AppendLine("=== End of Job Log ===\n");
                }
                else
                {
                    _logger.LogWarning("Job {JobId} has no log", job.Id);
                }
            }

            return allLogs.ToString();
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("404"))
        {
            _logger.LogWarning("Pipeline {PipelineId} not found (404) - it may have been deleted or never existed. Skipping...", pipelineId);
            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting failure logs for pipeline {PipelineId}", pipelineId);
            return string.Empty;
        }
    }

    private string BuildConflictPrompt(
        MergeRequestSearchResult mr,
        string targetBranch)
    {
        return $@"We are working on escorting the merge request #{mr.IID}: {mr.Title}

There are merge conflicts between the source branch '{mr.SourceBranch}' and the target branch '{targetBranch}'.

Your task is to:
1. Merge the target branch '{targetBranch}' into the current branch.
2. Resolve any merge conflicts that arise.
3. Ensure the code still builds and looks correct.

Please run 'git fetch origin {targetBranch}' and then 'git merge origin/{targetBranch}' to start the merge process. 

Please also bump the version of the updated nuget package projects if necessary.

After resolving conflicts, don't forget to run git commit.";
    }

    /// <summary>
    /// Build the prompt for Gemini with MR context and failure logs.
    /// </summary>
    private string BuildFailurePrompt(
        MergeRequestSearchResult mr,
        DetailedMergeRequest details,
        string failureLogs)
    {
        return $@"We are working on escroting the merge request #{mr.IID}: {mr.Title}

Pipeline Web URL: {details.Pipeline?.WebUrl}
Pipeline Status: {details.Pipeline?.Status}

The CI/CD pipeline for this merge request has FAILED. Your task is to analyze the failure logs below and fix the code to make the pipeline pass.

=== FAILURE LOGS ===
{failureLogs}
=== END OF FAILURE LOGS ===

Please read git log, analyze the failure logs, identify the root cause, and make the necessary code changes to fix the build/test failures.

Don't forget to run git commit after making changes.";
    }

    /// <summary>
    /// Build the prompt for Gemini with MR context and human review notes.
    /// </summary>
    private string BuildReviewPrompt(
        MergeRequestSearchResult mr,
        string reviewNotes)
    {
        return $@"We are working on escroting the merge request #{mr.IID}: {mr.Title}

Human reviewers have provided feedback on this merge request. Your task is to analyze the discussions below and make the necessary code changes to address the feedback.

=== FEEDBACK ===
{reviewNotes}
=== END OF FEEDBACK ===

Please read git log, analyze the feedback, identify the requested changes, and make the necessary code modifications to satisfy the reviewers.

Please also bump the version of the updated nuget package projects if necessary.

Don't forget to run git commit after making changes.";
    }



    private string GetWorkspacePath(MergeRequestSearchResult mr, Repository repository)
    {
        var repoName = repository.Name ?? "unknown";
        return Path.Combine(_options.WorkspaceFolder, $"{mr.ProjectId}-{repoName}-mr-{mr.IID}");
    }

    private async Task<bool> ShouldProcessDueToReviewAsync(Server server, MergeRequestSearchResult mr)
    {
        if (server.Provider != "GitLab")
        {
            return false;
        }

        try
        {
            var commitsUrl = $"{server.EndPoint.TrimEnd('/')}/api/v4/projects/{mr.ProjectId}/merge_requests/{mr.IID}/commits";
            var commits = await _httpWrapper.SendHttpAndGetJson<List<GitLabCommit>>(commitsUrl, HttpMethod.Get, server.Token);

            var botCommits = commits
                .Where(c => c.Message.Contains("Automatically generated fix by Gemini Bot") || c.Message.Contains("Automatically generated by Gemini Bot"))
                .ToList();

            DateTime lastBotCommitTime = DateTime.MinValue;
            if (botCommits.Any())
            {
                lastBotCommitTime = botCommits.Max(c => c.Created_at);
            }

            var discussionsUrl = $"{server.EndPoint.TrimEnd('/')}/api/v4/projects/{mr.ProjectId}/merge_requests/{mr.IID}/discussions";
            var discussions = await _httpWrapper.SendHttpAndGetJson<List<GitLabDiscussion>>(discussionsUrl, HttpMethod.Get, server.Token);

            var humanNotes = discussions
                .SelectMany(d => d.Notes)
                .Where(n => !n.System && !string.Equals(n.Author.Username, server.UserName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (!humanNotes.Any())
            {
                return false;
            }

            var latestHumanNoteTime = humanNotes.Max(n => n.Created_at);
            return latestHumanNoteTime > lastBotCommitTime;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking for human reviews for MR #{IID}", mr.IID);
            return false;
        }
    }

    private async Task<string> GetFormattedDiscussionsAsync(Server server, MergeRequestSearchResult mr)
    {
        if (server.Provider != "GitLab")
        {
            return string.Empty;
        }

        try
        {
            var discussionsUrl = $"{server.EndPoint.TrimEnd('/')}/api/v4/projects/{mr.ProjectId}/merge_requests/{mr.IID}/discussions";
            var discussions = await _httpWrapper.SendHttpAndGetJson<List<GitLabDiscussion>>(discussionsUrl, HttpMethod.Get, server.Token);

            var sb = new StringBuilder();
            var allNotes = discussions
                .SelectMany(d => d.Notes)
                .Where(n => !n.System)
                .OrderBy(n => n.Created_at);

            foreach (var note in allNotes)
            {
                sb.AppendLine($"{note.Author.Username} said: {note.Body} ({note.Created_at})");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching discussions for MR #{IID}", mr.IID);
            return string.Empty;
        }
    }

    private class GitLabCommit
    {
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        // ReSharper disable once UnusedAutoPropertyAccessor.Local
        public DateTime Created_at { get; set; }
    }

    private class GitLabDiscussion
    {
        [JsonPropertyName("notes")]
        public IEnumerable<GitLabNote> Notes { get; set; } = [];
    }

    private class GitLabNote
    {
        [JsonPropertyName("body")]
        public string Body { get; set; } = string.Empty;

        [JsonPropertyName("author")]
        public GitLabUser Author { get; set; } = new();

        [JsonPropertyName("created_at")]
        // ReSharper disable once UnusedAutoPropertyAccessor.Local
        public DateTime Created_at { get; set; }

        [JsonPropertyName("system")]
        // ReSharper disable once UnusedAutoPropertyAccessor.Local
        public bool System { get; set; }
    }

    private class GitLabUser
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;
    }

    private class GitLabMergeRequestDto
    {
        [JsonPropertyName("iid")]
        public int Iid { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("project_id")]
        public int ProjectId { get; set; }

        [JsonPropertyName("source_project_id")]
        public int SourceProjectId { get; set; }

        [JsonPropertyName("source_branch")]
        public string SourceBranch { get; set; } = string.Empty;

        [JsonPropertyName("target_branch")]
        public string TargetBranch { get; set; } = string.Empty;
    }

    /// <summary>
    /// Check if the current HEAD is ahead of the remote tracking branch.
    /// Returns true if there are commits that haven't been pushed to origin.
    /// </summary>
    private async Task<bool> IsAheadOfOrigin(string workPath, string branchName)
    {
        try
        {
            // Count commits that are in HEAD but not in origin/<branchName>
            var (exitCode, output, error) = await _commandService.RunCommandAsync(
                bin: "git",
                arg: $"rev-list --count HEAD ^origin/{branchName}",
                path: workPath,
                timeout: TimeSpan.FromSeconds(10));

            if (exitCode != 0)
            {
                _logger.LogWarning("git rev-list command failed with exit code {ExitCode}. Error: {Error}", exitCode, error);
                return false;
            }

            if (int.TryParse(output.Trim(), out var commitCount))
            {
                var isAhead = commitCount > 0;
                if (isAhead)
                {
                    _logger.LogInformation("Local HEAD is {Count} commit(s) ahead of origin/{Branch}", commitCount, branchName);
                }
                return isAhead;
            }

            _logger.LogWarning("Failed to parse commit count from git rev-list: {Result}", output);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking if HEAD is ahead of origin/{Branch}. Assuming not ahead.", branchName);
            return false;
        }
    }
}
