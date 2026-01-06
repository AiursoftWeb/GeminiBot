using Aiursoft.GitRunner.Models;
using Aiursoft.GeminiBot.Configuration;
using Aiursoft.GeminiBot.Models;
using Aiursoft.GeminiBot.Services.Abstractions;
using Aiursoft.NugetNinja.GitServerBase.Models;
using Aiursoft.NugetNinja.GitServerBase.Services.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aiursoft.GeminiBot.Services;

/// <summary>
/// A declarative engine for executing bot workflows.
/// Provides high-level building blocks for issue resolution and MR maintenance.
/// </summary>
public class BotWorkflowEngine
{
    private readonly IVersionControlService _versionControl;
    private readonly IGeminiWorkspaceManager _workspaceManager;
    private readonly GeminiCliService _geminiCliService;
    private readonly IGeminiCommandService _commandService;
    private readonly GeminiBotOptions _options;
    private readonly ILogger<BotWorkflowEngine> _logger;

    public BotWorkflowEngine(
        IVersionControlService versionControl,
        IGeminiWorkspaceManager workspaceManager,
        GeminiCliService geminiCliService,
        IGeminiCommandService commandService,
        IOptions<GeminiBotOptions> options,
        ILogger<BotWorkflowEngine> logger)
    {
        _versionControl = versionControl;
        _workspaceManager = workspaceManager;
        _geminiCliService = geminiCliService;
        _commandService = commandService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<WorkflowContext> ExecuteAsync(WorkflowContext context, Func<WorkflowContext, Task>? finalizeAsync = null)
    {
        try
        {
            _logger.LogInformation("Starting workflow for workspace {WorkspaceName}. Focus: {CommitMessage}", context.WorkspaceName, context.CommitMessage);
            await GetRepository(context);
            await PrepareWorkspace(context);
            await TriggerMergeAsync(context);
            await RunGemini(context);

            if (await HasChanges(context))
            {
                await CommitChanges(context);
                if (finalizeAsync != null)
                {
                    await finalizeAsync(context);
                }
                else
                {
                    var pushPath = _versionControl.GetPushPath(context.Server, context.Repository!);
                    await PushAndFinalizeAsync(context, pushPath);
                }
            }
            else
            {
                _logger.LogInformation("No changes detected for {WorkspaceName}. Skipping finalize.", context.WorkspaceName);
                context.Result = ProcessResult.Skipped("No changes made");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Workflow failed for {WorkspaceName}", context.WorkspaceName);
            context.Result = ProcessResult.Failed(ex.Message, ex);
        }

        return context;
    }

    private async Task GetRepository(WorkflowContext context)
    {
        _logger.LogInformation("Fetching repository details for project {ProjectId}...", context.ProjectId);
        context.Repository = await _versionControl.GetRepository(
            context.Server.EndPoint,
            context.ProjectId,
            string.Empty,
            context.Server.Token);
    }

    private async Task PrepareWorkspace(WorkflowContext context)
    {
        var repoName = context.Repository?.Name ?? "unknown";
        context.WorkspacePath = Path.Combine(_options.WorkspaceFolder, $"{context.ProjectId}-{repoName}-{context.WorkspaceName}");

        _logger.LogInformation("Cloning repository to {WorkPath}...", context.WorkspacePath);

        await _workspaceManager.ResetRepo(
            context.WorkspacePath,
            context.SourceBranch,
            context.Repository?.CloneUrl ?? throw new InvalidOperationException("Repository clone URL is null"),
            CloneMode.Full,
            $"{context.Server.UserName}:{context.Server.Token}");

        await _workspaceManager.SetUserConfig(context.WorkspacePath, context.Server.DisplayName, context.Server.UserEmail);
    }

    private async Task TriggerMergeAsync(WorkflowContext context)
    {
        if (context.NeedResolveConflicts)
        {
            _logger.LogInformation("Proactively merging {TargetBranch} into {SourceBranch} to trigger conflicts...", context.TargetBranch, context.SourceBranch);

            // git fetch origin {TargetBranch}
            await _commandService.RunCommandAsync("git", $"fetch origin {context.TargetBranch}", context.WorkspacePath, TimeSpan.FromSeconds(30));

            // git merge origin/{TargetBranch}
            var (exitCode, output, _) = await _commandService.RunCommandAsync("git", $"merge origin/{context.TargetBranch}", context.WorkspacePath, TimeSpan.FromSeconds(30));

            if (exitCode != 0)
            {
                _logger.LogInformation("Merge resulted in expected conflicts. Output: {Output}", output);
            }
            else
            {
                _logger.LogInformation("Merge was successful without conflicts (unexpected but okay).");
            }
        }
    }

    private async Task RunGemini(WorkflowContext context)
    {
        _logger.LogInformation("Invoking Gemini CLI...");
        var success = await _geminiCliService.InvokeGeminiCliAsync(context.WorkspacePath, context.Prompt, context.HideGitFolder);
        if (!success)
        {
             _logger.LogWarning("Gemini CLI failed. Continuing to see if localization helps...");
        }

        // Gemini CLI may take a while to finish and flush files.
        await Task.Delay(1000);
    }

    private async Task<bool> HasChanges(WorkflowContext context)
    {
        var hasPendingChanges = await _workspaceManager.PendingCommit(context.WorkspacePath);
        var isAheadOfOrigin = await IsAheadOfOrigin(context.WorkspacePath, context.SourceBranch);
        return hasPendingChanges || isAheadOfOrigin;
    }

    private async Task CommitChanges(WorkflowContext context)
    {
        if (await _workspaceManager.PendingCommit(context.WorkspacePath))
        {
            _logger.LogInformation("Committing changes to branch {Branch}...", context.PushBranch);
            var saved = await _workspaceManager.CommitToBranch(context.WorkspacePath, context.CommitMessage, context.PushBranch);
            if (!saved)
            {
                throw new InvalidOperationException("Failed to commit changes");
            }
        }
    }

    public async Task PushAndFinalizeAsync(WorkflowContext context, string pushPath)
    {
        _logger.LogInformation("Pushing changes to {PushPath}...", pushPath);
        await _workspaceManager.Push(context.WorkspacePath, context.PushBranch, pushPath, force: true);
        context.Result = ProcessResult.Succeeded("Successfully pushed changes");
    }

    private async Task<bool> IsAheadOfOrigin(string workPath, string branchName)
    {
        try
        {
            var (exitCode, output, _) = await _commandService.RunCommandAsync(
                bin: "git",
                arg: $"rev-list --count HEAD ^origin/{branchName}",
                path: workPath,
                timeout: TimeSpan.FromSeconds(10));

            return exitCode == 0 && int.TryParse(output.Trim(), out var commitCount) && commitCount > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task EnsureRepositoryForkedAsync(Server server, Repository repository)
    {
        var ownerLogin = repository.Owner?.Login ?? throw new InvalidOperationException("Repository owner is null");
        var repoName = repository.Name ?? throw new InvalidOperationException("Repository name is null");

        if (!await _versionControl.RepoExists(server.EndPoint, server.UserName, repoName, server.Token))
        {
            _logger.LogInformation("Forking repository {Org}/{Repo}...", ownerLogin, repoName);
            await _versionControl.ForkRepo(server.EndPoint, ownerLogin, repoName, server.Token);

            await Task.Delay(_options.ForkWaitDelayMs);
            while (!await _versionControl.RepoExists(server.EndPoint, server.UserName, repoName, server.Token))
            {
                _logger.LogInformation("Waiting for fork to complete...");
                await Task.Delay(_options.ForkWaitDelayMs);
            }
        }
    }
}
