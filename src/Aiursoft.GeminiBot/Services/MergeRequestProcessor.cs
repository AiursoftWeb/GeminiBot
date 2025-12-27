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
    private readonly GeminiBotOptions _options;
    private readonly ILogger<MergeRequestProcessor> _logger;

    public MergeRequestProcessor(
        LocalizationService localizationService,
        IVersionControlService versionControl,
        WorkspaceManager workspaceManager,
        HttpWrapper httpWrapper,
        GeminiCliService geminiCliService,
        IOptions<GeminiBotOptions> options,
        ILogger<MergeRequestProcessor> logger)
    {
        _localizationService = localizationService;
        _versionControl = versionControl;
        _workspaceManager = workspaceManager;
        _httpWrapper = httpWrapper;
        _geminiCliService = geminiCliService;
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
            _logger.LogInformation("Checking merge requests submitted by {UserName}...", server.UserName);

            var mergeRequests = await _versionControl.GetOpenMergeRequests(
                server.EndPoint,
                server.UserName,
                server.Token);

            var failedMRs = new List<(MergeRequestSearchResult mr, DetailedMergeRequest details)>();

            foreach (var mr in mergeRequests)
            {
                _logger.LogInformation("Checking MR #{IID}: {Title}...", mr.IID, mr.Title);

                var details = await _versionControl.GetMergeRequestDetails(
                    server.EndPoint,
                    server.UserName,
                    server.Token,
                    mr.ProjectId,
                    mr.IID);

                // Check if pipeline exists and has failed
                if (details.Pipeline != null && details.Pipeline.Status != "success")
                {
                    _logger.LogWarning("MR #{IID} has pipeline with status: {Status}", mr.IID, details.Pipeline.Status);

                    // Only process failed pipelines, skip running ones
                    if (details.Pipeline.Status == "failed")
                    {
                        failedMRs.Add((mr, details));
                    }
                }
                else
                {
                    // Pipeline is success or null. Check for human comments.
                    if (await ShouldProcessDueToReviewAsync(server, mr))
                    {
                        _logger.LogWarning("MR #{IID} has human comments after the last bot commit. Processing...", mr.IID);
                        failedMRs.Add((mr, details));
                    }
                    else
                    {
                        _logger.LogInformation("MR #{IID} pipeline is {Status} and no new human comments, no action needed", mr.IID, details.Pipeline?.Status ?? "null");
                    }
                }
            }

            if (failedMRs.Count == 0)
            {
                _logger.LogInformation("No failed merge requests found. All clear!");
                return ProcessResult.Succeeded("No failed MRs to fix");
            }

            _logger.LogInformation("Found {Count} failed merge requests to fix", failedMRs.Count);

            // Process each MR
            foreach (var (mr, details) in failedMRs)
            {
                await CheckAndFixMergeRequestAsync(mr, details, server);
            }

            return ProcessResult.Succeeded($"Processed {failedMRs.Count} MRs");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing merge requests");
            return ProcessResult.Failed("Error processing merge requests", ex);
        }
    }

    /// <summary>
    /// Check a single MR, download logs or discussions, and invoke Gemini to fix.
    /// </summary>
    private async Task CheckAndFixMergeRequestAsync(
        MergeRequestSearchResult mr,
        DetailedMergeRequest details,
        Server server)
    {
        try
        {
            _logger.LogInformation("Processing failed MR #{IID}: {Title}", mr.IID, mr.Title);

            if (details.Pipeline == null)
            {
                _logger.LogWarning("MR #{IID} has no pipeline information", mr.IID);
                return;
            }

            if (details.Pipeline.Id <= 0)
            {
                _logger.LogWarning("MR #{IID} has invalid pipeline ID: {PipelineId}", mr.IID, details.Pipeline.Id);
                return;
            }

            // CRITICAL: Pipeline runs in the SOURCE project (fork), not the target project!
            var pipelineProjectId = mr.SourceProjectId > 0 ? mr.SourceProjectId : mr.ProjectId;
            _logger.LogInformation("MR #{IID}: Using project ID {ProjectId} for pipeline operations (source: {SourceProjectId}, target: {TargetProjectId})",
                mr.IID, pipelineProjectId, mr.SourceProjectId, mr.ProjectId);

            // Get repository details from SOURCE project (where the branch exists)
            // The MR branch is in the fork, not in the target project!
            _logger.LogInformation("Getting repository details from source project {ProjectId}...", pipelineProjectId);
            var repository = await _versionControl.GetRepository(
                server.EndPoint,
                pipelineProjectId.ToString(),
                string.Empty,
                server.Token);

            // Get failure logs from SOURCE project (where pipeline runs)
            var failureLogs = string.Empty;
            var reviewNotes = string.Empty;

            if (details.Pipeline?.Status == "failed")
            {
                failureLogs = await GetFailureLogsAsync(server, pipelineProjectId, details.Pipeline.Id);
            }
            else
            {
                reviewNotes = await GetFormattedDiscussionsAsync(server, mr);
            }

            if (string.IsNullOrWhiteSpace(failureLogs) && string.IsNullOrWhiteSpace(reviewNotes))
            {
                _logger.LogWarning("No failure logs and no review notes found for MR #{IID}", mr.IID);
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

            // Build prompt
            string prompt;
            string commitMessage;
            if (!string.IsNullOrWhiteSpace(failureLogs))
            {
                prompt = BuildFailurePrompt(mr, details, failureLogs);
                commitMessage = $"Fix pipeline failure for MR #{mr.IID}\n\nAutomatically generated fix by Gemini Bot.";
            }
            else
            {
                prompt = BuildReviewPrompt(mr, reviewNotes);
                commitMessage = $"Address human review for MR #{mr.IID}\n\nAutomatically generated fix by Gemini Bot.";
            }

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

            // Check for changes
            if (!await _workspaceManager.PendingCommit(workPath))
            {
                _logger.LogInformation("MR #{IID} - Gemini made no changes", mr.IID);
                return;
            }

            // Commit and push fixes
            _logger.LogInformation("MR #{IID} has pending changes. Committing and pushing...", mr.IID);
            await _workspaceManager.SetUserConfig(workPath, server.DisplayName, server.UserEmail);

            var saved = await _workspaceManager.CommitToBranch(workPath, commitMessage, branchName);
            if (!saved)
            {
                _logger.LogError("Failed to commit changes for MR #{IID}", mr.IID);
                return;
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

                allLogs.AppendLine($"\n\n=== Job: {job.Name} (Stage: {job.Stage}) ===");
                allLogs.AppendLine(log);
                allLogs.AppendLine("=== End of Job Log ===\n");
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

Please analyze the failure logs, identify the root cause, and make the necessary code changes to fix the build/test failures.";
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

Please analyze the feedback, identify the requested changes, and make the necessary code modifications to satisfy the reviewers.";
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
}
