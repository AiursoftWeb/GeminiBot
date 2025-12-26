namespace Aiursoft.GeminiBot.Configuration;

/// <summary>
/// Configuration options for the Gemini Bot.
/// Provides clean separation of configuration concerns from business logic.
/// </summary>
public class GeminiBotOptions
{
    /// <summary>
    /// Workspace folder where repositories are cloned for processing.
    /// </summary>
    public string WorkspaceFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NugetNinjaWorkspace");

    /// <summary>
    /// Timeout for Gemini CLI execution.
    /// </summary>
    public TimeSpan GeminiTimeout { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Delay in milliseconds when waiting for a forked repository to become available.
    /// </summary>
    public int ForkWaitDelayMs { get; set; } = 5000;

    /// <summary>
    /// The AI model to use for Gemini CLI (passed to --model parameter).
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// The API key for Gemini (set as GEMINI_API_KEY environment variable).
    /// </summary>
    public string? GeminiApiKey { get; set; }

    /// <summary>
    /// Whether to enable automatic localization after Gemini processing.
    /// </summary>
    public bool LocalizationEnabled { get; set; } = false;

    /// <summary>
    /// The Ollama API endpoint for localization (e.g., "https://api.deepseek.com/chat/completions").
    /// </summary>
    public string? OllamaApiEndpoint { get; set; }

    /// <summary>
    /// The Ollama model to use for localization (e.g., "deepseek-chat").
    /// </summary>
    public string? OllamaModel { get; set; }

    /// <summary>
    /// The API key for Ollama API.
    /// </summary>
    public string? OllamaApiKey { get; set; }

    /// <summary>
    /// Maximum concurrent requests for localization.
    /// </summary>
    public int LocalizationConcurrentRequests { get; set; } = 8;

    /// <summary>
    /// Target languages for localization (e.g., ["zh-CN", "en-US", "ja-JP"]).
    /// </summary>
    public string[] LocalizationTargetLanguages { get; set; } = [];
}
