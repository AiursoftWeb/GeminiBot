using Aiursoft.CSTools.Services;
using Aiursoft.GeminiBot.Services.Abstractions;

namespace Aiursoft.GeminiBot.Services;

public class GeminiCommandService : IGeminiCommandService
{
    private readonly CommandService _commandService;

    public GeminiCommandService(CommandService commandService)
    {
        _commandService = commandService;
    }

    public Task<(int exitCode, string output, string error)> RunCommandAsync(
        string bin,
        string arg,
        string path,
        TimeSpan timeout,
        bool useShell = false,
        IDictionary<string, string?>? environmentVariables = null)
    {
        return _commandService.RunCommandAsync(bin, arg, path, timeout, useShell, environmentVariables);
    }
}
