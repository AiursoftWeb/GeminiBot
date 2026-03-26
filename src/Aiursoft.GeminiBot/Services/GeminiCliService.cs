using Aiursoft.GeminiBot.Configuration;
using Aiursoft.GeminiBot.Services.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aiursoft.GeminiBot.Services;

/// <summary>
/// Service responsible for invoking the Gemini CLI to process code changes.
/// Handles task file creation, .git directory management, and CLI execution.
/// </summary>
public class GeminiCliService(
    IGeminiCommandService commandService,
    IOptions<GeminiBotOptions> options,
    ILogger<GeminiCliService> logger)
{
    private readonly GeminiBotOptions _options = options.Value;

    /// <summary>
    /// Invoke Gemini CLI to fix the code based on the task description.
    /// </summary>
    /// <param name="workPath">The workspace path where the repository is cloned.</param>
    /// <param name="taskDescription">The task description to pass to Gemini CLI.</param>
    /// <param name="hideGitFolder">If true, hides .git folder to prevent Gemini from manipulating git. If false, allows Gemini to see git history.</param>
    /// <returns>True if Gemini CLI completed successfully, false otherwise.</returns>
    public virtual async Task<(bool Success, string Output, string Error)> InvokeGeminiCliAsync(string workPath, string taskDescription, bool hideGitFolder)
    {
        string? tempFile = null;
        var gitPath = Path.Combine(workPath, ".git");
        var gitBackupPath = workPath + "-hidden-git";

        try
        {
            // Write task to temp file
            tempFile = Path.Combine(workPath, ".gemini-task.txt");
            await File.WriteAllTextAsync(tempFile, taskDescription);

            // Hide .git directory to prevent Gemini from manipulating git (if requested)
            if (hideGitFolder && Directory.Exists(gitPath))
            {
                logger.LogInformation("Hiding .git directory to prevent Gemini CLI from manipulating git...");
                Directory.Move(gitPath, gitBackupPath);
            }
            else if (!hideGitFolder)
            {
                logger.LogInformation(".git directory is accessible to Gemini CLI for viewing history");
            }

            logger.LogInformation("Running Gemini CLI in {WorkPath}", workPath);

            // Build Gemini command with optional --model parameter
            var geminiCommand = "gemini --yolo";
            if (!string.IsNullOrWhiteSpace(_options.Model))
            {
                geminiCommand += $" --model {_options.Model}";
            }
            geminiCommand += " < .gemini-task.txt";

            // Build environment variables dictionary
            IDictionary<string, string?>? envVars = null;
            if (!string.IsNullOrWhiteSpace(_options.GeminiApiKey))
            {
                envVars = new Dictionary<string, string?>
                {
                    ["GEMINI_API_KEY"] = _options.GeminiApiKey
                };
            }

            var (code, output, error) = await commandService.RunCommandAsync(
                bin: "/bin/bash",
                arg: $"-c \"{geminiCommand}\"",
                path: workPath,
                timeout: _options.GeminiTimeout,
                environmentVariables: envVars);

            if (code != 0)
            {
                logger.LogError("Gemini CLI failed with exit code {Code}. Output: {Output}. Error: {Error}", code, output, error);
                return (false, output, error);
            }

            logger.LogInformation("Gemini CLI completed successfully. It says: {Output}", output);
            return (true, output, error);
        }
        finally
        {
            // Restore .git directory
            if (Directory.Exists(gitBackupPath))
            {
                try
                {
                    logger.LogInformation("Restoring .git directory...");
                    if (Directory.Exists(gitPath))
                    {
                        Directory.Delete(gitPath, recursive: true);
                    }
                    Directory.Move(gitBackupPath, gitPath);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to restore .git directory from backup!");
                }
            }

            // Clean up temp file
            if (tempFile != null && File.Exists(tempFile))
            {
                try
                {
                    File.Delete(tempFile);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to delete temporary file: {FilePath}", tempFile);
                }
            }
        }
    }
}
