namespace Aiursoft.GeminiBot.Services.Abstractions;

public interface IGeminiCommandService
{
    Task<(int exitCode, string output, string error)> RunCommandAsync(
        string bin,
        string arg,
        string path,
        TimeSpan timeout,
        bool useShell = false,
        IDictionary<string, string?>? environmentVariables = null);
}
