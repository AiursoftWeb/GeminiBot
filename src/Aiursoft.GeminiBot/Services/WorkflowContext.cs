using Aiursoft.GeminiBot.Models;
using Aiursoft.NugetNinja.GitServerBase.Models;

namespace Aiursoft.GeminiBot.Services;

/// <summary>
/// Holds the state and context for a single bot workflow execution.
/// </summary>
public class WorkflowContext
{
    public required Server Server { get; init; }
    public required string ProjectId { get; init; }
    public required string SourceBranch { get; init; }
    public required string TargetBranch { get; init; }
    public required string WorkspaceName { get; init; }
    public string Prompt { get; set; } = string.Empty;
    public string CommitMessage { get; set; } = string.Empty;
    public bool HideGitFolder { get; set; }
    public string PushBranch { get; set; } = string.Empty;
    
    // Derived/State data
    public Repository? Repository { get; set; }
    public string WorkspacePath { get; set; } = string.Empty;
    public ProcessResult Result { get; set; } = ProcessResult.Succeeded("Initialized");
}
