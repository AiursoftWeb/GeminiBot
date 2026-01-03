using Aiursoft.GeminiBot.Models;
using Aiursoft.NugetNinja.GitServerBase.Models;
using Aiursoft.NugetNinja.GitServerBase.Services;
using Aiursoft.NugetNinja.GitServerBase.Models.Abstractions;
using Aiursoft.NugetNinja.GitServerBase.Services.Providers;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Aiursoft.GeminiBot.Services;

/// <summary>
/// Handles checking and fixing failed merge requests.
/// </summary>
public class MergeRequestProcessor
{
    private readonly IVersionControlService _versionControl;
    private readonly BotWorkflowEngine _workflowEngine;
    private readonly HttpWrapper _httpWrapper;
    private readonly ILogger<MergeRequestProcessor> _logger;

    public MergeRequestProcessor(
        IVersionControlService versionControl,
        BotWorkflowEngine workflowEngine,
        HttpWrapper httpWrapper,
        ILogger<MergeRequestProcessor> logger)
    {
        _versionControl = versionControl;
        _workflowEngine = workflowEngine;
        _httpWrapper = httpWrapper;
        _logger = logger;
    }

    public async Task<ProcessResult> ProcessMergeRequestsAsync(Server server)
    {
        try
        {
            var mrsToProcess = await IdentifyMergeRequestsToProcessAsync(server);
            if (mrsToProcess.Count == 0)
            {
                _logger.LogInformation("No merge requests need attention. All clear!");
                return ProcessResult.Succeeded("No MRs to fix");
            }

            _logger.LogInformation("Found {Count} merge requests to fix", mrsToProcess.Count);

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

    private async Task<List<MRToProcess>> IdentifyMergeRequestsToProcessAsync(Server server)
    {
        IReadOnlyCollection<MergeRequestSearchResult> mergeRequests;
        var targetBranches = new Dictionary<int, string>();
        var gitLabMrs = new List<GitLabMergeRequestDto>();

        if (server.Provider == "GitLab")
        {
            _logger.LogInformation("Checking merge requests assigned to {UserName}...", server.UserName);
            var url = $"{server.EndPoint.TrimEnd('/')}/api/v4/merge_requests?scope=assigned_to_me&state=opened&per_page=100";
            gitLabMrs = await _httpWrapper.SendHttpAndGetJson<List<GitLabMergeRequestDto>>(url, HttpMethod.Get, server.Token);
            foreach (var m in gitLabMrs) targetBranches[m.Iid] = m.TargetBranch;
            mergeRequests = gitLabMrs.Select(m => new MergeRequestSearchResult
            {
                IID = m.Iid, Title = m.Title, ProjectId = m.ProjectId, SourceProjectId = m.SourceProjectId, SourceBranch = m.SourceBranch
            }).ToList();
        }
        else
        {
            _logger.LogInformation("Checking merge requests submitted by {UserName}...", server.UserName);
            mergeRequests = await _versionControl.GetOpenMergeRequests(server.EndPoint, server.UserName, server.Token);
        }

        var mrsToProcess = new List<MRToProcess>();
        foreach (var mr in mergeRequests)
        {
            _logger.LogInformation("Checking MR #{IID}: {Title}...", mr.IID, mr.Title);
            var details = await _versionControl.GetMergeRequestDetails(server.EndPoint, server.UserName, server.Token, mr.ProjectId, mr.IID);
            var hasConflicts = details.HasConflicts;
            var hasNewHumanReview = await ShouldProcessDueToReviewAsync(server, mr);
            var pipelineFailed = details.Pipeline?.Status == "failed";

            if (hasConflicts || hasNewHumanReview || pipelineFailed)
            {
                mrsToProcess.Add(new MRToProcess
                {
                    SearchResult = mr,
                    Details = details,
                    HasConflicts = hasConflicts,
                    HasNewHumanReview = hasNewHumanReview,
                    PipelineFailed = pipelineFailed,
                    TargetBranch = targetBranches.GetValueOrDefault(mr.IID, "main"),
                    AuthorName = server.Provider == "GitLab" ? gitLabMrs.FirstOrDefault(m => m.Iid == mr.IID)?.Author.Username : null
                });
            }
        }
        return mrsToProcess;
    }

    private async Task CheckAndFixMergeRequestAsync(MRToProcess item, Server server)
    {
        var mr = item.SearchResult;
        try
        {
            _logger.LogInformation("Processing MR #{IID}: {Title}", mr.IID, mr.Title);
            var pipelineProjectId = mr.SourceProjectId > 0 ? mr.SourceProjectId : mr.ProjectId;
            var branchName = mr.SourceBranch ?? throw new InvalidOperationException($"MR #{mr.IID} has no source branch");
            var isOthersMr = server.Provider == "GitLab" && !string.Equals(item.AuthorName, server.UserName, StringComparison.OrdinalIgnoreCase);
            
            var (prompt, commitMessage) = await BuildActionDetailsAsync(item, server, pipelineProjectId);
            
            var context = new WorkflowContext
            {
                Server = server,
                ProjectId = pipelineProjectId.ToString(),
                SourceBranch = branchName,
                TargetBranch = item.TargetBranch,
                WorkspaceName = $"mr-{mr.IID}",
                Prompt = prompt,
                CommitMessage = commitMessage,
                PushBranch = isOthersMr ? $"fix-mr-{mr.IID}" : branchName,
                HideGitFolder = false
            };

            await _workflowEngine.ExecuteAsync(context, async ctx => 
            {
                if (isOthersMr)
                {
                    await HandleOthersMrFinalizeAsync(ctx, mr, item.TargetBranch);
                }
                else
                {
                    var pushPath = _versionControl.GetPushPath(server, ctx.Repository!);
                    await _workflowEngine.PushAndFinalizeAsync(ctx, pushPath);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fixing MR #{IID}", mr.IID);
        }
    }

    private async Task<(string Prompt, string CommitMessage)> BuildActionDetailsAsync(MRToProcess item, Server server, int pipelineProjectId)
    {
        if (item.HasConflicts)
            return (BuildConflictPrompt(item.SearchResult, item.TargetBranch), $"Resolve merge conflicts for MR #{item.SearchResult.IID} by merging {item.TargetBranch}\n\nAutomatically generated fix by Gemini Bot.");
        
        if (item.HasNewHumanReview)
            return (BuildReviewPrompt(item.SearchResult, await GetFormattedDiscussionsAsync(server, item.SearchResult)), $"Address human review for MR #{item.SearchResult.IID}\n\nAutomatically generated fix by Gemini Bot.");

        var logs = await GetFailureLogsAsync(server, pipelineProjectId, item.Details.Pipeline!.Id);
        return (BuildFailurePrompt(item.SearchResult, item.Details, logs), $"Fix pipeline failure for MR #{item.SearchResult.IID}\n\nAutomatically generated fix by Gemini Bot.");
    }

    private async Task HandleOthersMrFinalizeAsync(WorkflowContext ctx, MergeRequestSearchResult oldMr, string targetBranch)
    {
        _logger.LogInformation("MR #{IID} - Creating bot fork and new MR...", oldMr.IID);
        var targetRepository = await _versionControl.GetRepository(ctx.Server.EndPoint, oldMr.ProjectId.ToString(), string.Empty, ctx.Server.Token);
        await _workflowEngine.EnsureRepositoryForkedAsync(ctx.Server, targetRepository);
        
        var botForkRepository = await _versionControl.GetRepository(ctx.Server.EndPoint, oldMr.ProjectId.ToString(), ctx.Server.UserName, ctx.Server.Token);
        var pushPath = _versionControl.GetPushPath(ctx.Server, botForkRepository);
        await _workflowEngine.PushAndFinalizeAsync(ctx, pushPath);

        await CreateNewMergeRequestAsync(ctx.Server, targetRepository, oldMr, targetBranch, ctx.PushBranch);
    }

    private async Task CreateNewMergeRequestAsync(Server server, Repository targetRepository, MergeRequestSearchResult oldMr, string targetBranch, string botBranchName)
    {
        var ownerLogin = targetRepository.Owner?.Login ?? throw new InvalidOperationException("Repository owner is null");
        var repoName = targetRepository.Name ?? throw new InvalidOperationException("Repository name is null");

        var title = $"[Bot Fix] {oldMr.Title} (Replacement for #{oldMr.IID})";
        var body = $@"This merge request was automatically generated by Gemini Bot to replace #{oldMr.IID}...";

        await _versionControl.CreatePullRequest(server.EndPoint, ownerLogin, repoName, $"{server.UserName}:{botBranchName}", targetBranch, title, body, server.Token);

        if (server.Provider == "GitLab")
        {
            await ManageGitLabAssignmentsAsync(server, oldMr, botBranchName);
        }
    }

    private async Task ManageGitLabAssignmentsAsync(Server server, MergeRequestSearchResult oldMr, string botBranchName)
    {
        try
        {
            var userUrl = $"{server.EndPoint.TrimEnd('/')}/api/v4/user";
            var user = await _httpWrapper.SendHttpAndGetJson<GitLabUser>(userUrl, HttpMethod.Get, server.Token);
            
            var updateOldMrUrl = $"{server.EndPoint.TrimEnd('/')}/api/v4/projects/{oldMr.ProjectId}/merge_requests/{oldMr.IID}?assignee_ids=";
            await _httpWrapper.SendHttpAndGetJson<object>(updateOldMrUrl, HttpMethod.Put, server.Token);

            var mrUrl = $"{server.EndPoint.TrimEnd('/')}/api/v4/projects/{oldMr.ProjectId}/merge_requests?state=opened&source_branch={botBranchName}";
            var mrs = await _httpWrapper.SendHttpAndGetJson<List<GitLabMergeRequestDto>>(mrUrl, HttpMethod.Get, server.Token);
            var newMr = mrs.FirstOrDefault();

            if (newMr != null)
            {
                var updateNewMrUrl = $"{server.EndPoint.TrimEnd('/')}/api/v4/projects/{oldMr.ProjectId}/merge_requests/{newMr.Iid}?assignee_ids={user.Id}";
                await _httpWrapper.SendHttpAndGetJson<object>(updateNewMrUrl, HttpMethod.Put, server.Token);
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to manage MR assignments in GitLab"); }
    }

    private async Task<string> GetFailureLogsAsync(Server server, int projectId, int pipelineId)
    {
        try
        {
            var jobs = await _versionControl.GetPipelineJobs(server.EndPoint, server.Token, projectId, pipelineId);
            var failedJobs = jobs.Where(j => j.Status == "failed").ToList();
            var allLogs = new StringBuilder();
            foreach (var job in failedJobs)
            {
                var log = await _versionControl.GetJobLog(server.EndPoint, server.Token, projectId, job.Id);
                if (!string.IsNullOrWhiteSpace(log))
                {
                    allLogs.AppendLine($"\n\n=== Job: {job.Name} (Stage: {job.Stage}) ===");
                    allLogs.AppendLine(log);
                    allLogs.AppendLine("=== End of Job Log ===\n");
                }
            }
            return allLogs.ToString();
        }
        catch (Exception ex) { _logger.LogError(ex, "Error getting failure logs for pipeline {PipelineId}", pipelineId); return string.Empty; }
    }

    private string BuildConflictPrompt(MergeRequestSearchResult mr, string targetBranch) => $@"Merge conflicts between '{mr.SourceBranch}' and '{targetBranch}' for #{mr.IID}: {mr.Title}...";
    private string BuildFailurePrompt(MergeRequestSearchResult mr, DetailedMergeRequest details, string failureLogs) => $@"CI/CD FAILED for #{mr.IID}: {mr.Title}. Pipeline: {details.Pipeline?.WebUrl}. Logs: {failureLogs}";
    private string BuildReviewPrompt(MergeRequestSearchResult mr, string reviewNotes) => $@"Human feedback for #{mr.IID}: {mr.Title}. Notes: {reviewNotes}...";

    private async Task<bool> ShouldProcessDueToReviewAsync(Server server, MergeRequestSearchResult mr)
    {
        if (server.Provider != "GitLab") return false;
        try
        {
            var commitsUrl = $"{server.EndPoint.TrimEnd('/')}/api/v4/projects/{mr.ProjectId}/merge_requests/{mr.IID}/commits";
            var commits = await _httpWrapper.SendHttpAndGetJson<List<GitLabCommit>>(commitsUrl, HttpMethod.Get, server.Token);
            var lastBotCommitTime = commits.Where(c => c.Message.Contains("Gemini Bot")).Select(c => c.Created_at).DefaultIfEmpty(DateTime.MinValue).Max();
            var discussionsUrl = $"{server.EndPoint.TrimEnd('/')}/api/v4/projects/{mr.ProjectId}/merge_requests/{mr.IID}/discussions";
            var discussions = await _httpWrapper.SendHttpAndGetJson<List<GitLabDiscussion>>(discussionsUrl, HttpMethod.Get, server.Token);
            var latestHumanNoteTime = discussions.SelectMany(d => d.Notes).Where(n => !n.System && !string.Equals(n.Author.Username, server.UserName, StringComparison.OrdinalIgnoreCase)).Select(n => n.Created_at).DefaultIfEmpty(DateTime.MinValue).Max();
            return latestHumanNoteTime > lastBotCommitTime;
        }
        catch { return false; }
    }

    private async Task<string> GetFormattedDiscussionsAsync(Server server, MergeRequestSearchResult mr)
    {
        if (server.Provider != "GitLab") return string.Empty;
        try
        {
            var discussionsUrl = $"{server.EndPoint.TrimEnd('/')}/api/v4/projects/{mr.ProjectId}/merge_requests/{mr.IID}/discussions";
            var discussions = await _httpWrapper.SendHttpAndGetJson<List<GitLabDiscussion>>(discussionsUrl, HttpMethod.Get, server.Token);
            var sb = new StringBuilder();
            foreach (var note in discussions.SelectMany(d => d.Notes).Where(n => !n.System).OrderBy(n => n.Created_at)) sb.AppendLine($"{note.Author.Username}: {note.Body} ({note.Created_at})");
            return sb.ToString();
        }
        catch { return string.Empty; }
    }

    private class GitLabUser { public int Id { get; set; } }
}
