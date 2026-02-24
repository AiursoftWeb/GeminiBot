using Aiursoft.GitRunner;
using Aiursoft.GitRunner.Models;
using Aiursoft.GeminiBot.Services.Abstractions;

namespace Aiursoft.GeminiBot.Services;

public class GeminiWorkspaceManager : IGeminiWorkspaceManager
{
    private readonly WorkspaceManager _workspaceManager;

    public GeminiWorkspaceManager(WorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public Task ResetRepo(string path, string branch, string url, CloneMode mode, string auth)
    {
        return _workspaceManager.ResetRepo(path, branch, url, mode, auth);
    }

    public Task<bool> CommitToBranch(string path, string message, string branch)
    {
        // Fix for issue #20: Escape double quotes to prevent git commit failure
        var escapedMessage = message.Replace("\"", "\\\"");
        return _workspaceManager.CommitToBranch(path, escapedMessage, branch);
    }

    public Task<bool> PendingCommit(string path)
    {
        return _workspaceManager.PendingCommit(path);
    }

    public Task Push(string path, string branch, string url, bool force)
    {
        return _workspaceManager.Push(path, branch, url, force);
    }

    public Task SetUserConfig(string path, string name, string email)
    {
        return _workspaceManager.SetUserConfig(path, name, email);
    }
}
