using Aiursoft.Canon;
using Aiursoft.GeminiBot;
using Aiursoft.GitRunner;
using Aiursoft.GptClient;
using Aiursoft.GeminiBot.Configuration;
using Aiursoft.GeminiBot.Services;
using Aiursoft.NugetNinja.GitServerBase.Models;
using Aiursoft.NugetNinja.GitServerBase.Services.Providers;
using Aiursoft.NugetNinja.GitServerBase.Services.Providers.AzureDevOps;
using Aiursoft.NugetNinja.GitServerBase.Services.Providers.Gitea;
using Aiursoft.NugetNinja.GitServerBase.Services.Providers.GitHub;
using Aiursoft.NugetNinja.GitServerBase.Services.Providers.GitLab;
using Aiursoft.CSTools.Services;
using Aiursoft.Dotlang.AspNetTranslate.Services;
using Aiursoft.Dotlang.Shared;
using Aiursoft.NugetNinja.GitServerBase.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

await CreateHostBuilder(args)
    .Build()
    .Services
    .GetRequiredService<Entry>()
    .RunAsync();

static IHostBuilder CreateHostBuilder(string[] args)
{
    return Host
        .CreateDefaultBuilder(args)
        .ConfigureLogging(logging =>
        {
            logging
                .AddFilter("Microsoft.Extensions", LogLevel.Warning)
                .AddFilter("System", LogLevel.Warning);
            logging.AddSimpleConsole(options =>
            {
                options.IncludeScopes = false;
                options.SingleLine = true;
                options.TimestampFormat = "mm:ss ";
            });
        })
        .ConfigureServices((context, services) =>
        {
            services.AddMemoryCache();
            services.AddHttpClient();
            services.Configure<List<Server>>(context.Configuration.GetSection("Servers"));
            services.Configure<GeminiBotOptions>(context.Configuration.GetSection("GeminiBot"));
            services.AddGitRunner();
            services.AddTransient<IVersionControlService, GitHubService>();
            services.AddTransient<IVersionControlService, GiteaService>();
            services.AddTransient<IVersionControlService, AzureDevOpsService>();
            services.AddTransient<IVersionControlService, GitLabService>();
            services.AddTransient<HttpWrapper>();
            services.AddTransient<WorkspaceManager>();
            services.AddTransient<CommandService>();
            services.AddTransient<IssueProcessor>();
            services.AddTransient<MergeRequestProcessor>();
            services.AddTransient<Entry>();
            services.AddTransient<GeminiCliService>();

            // Register localization services
            services.Configure<TranslateOptions>(translateOptions =>
            {
                var botOptions = new GeminiBotOptions();
                context.Configuration.GetSection("GeminiBot").Bind(botOptions);
                translateOptions.OllamaInstance = botOptions.OllamaApiEndpoint ?? string.Empty;
                translateOptions.OllamaModel = botOptions.OllamaModel ?? string.Empty;
                translateOptions.OllamaToken = botOptions.OllamaApiKey ?? string.Empty;
            });

            // Register all required services from Aiursoft.Dotlang.AspNetTranslate
            services.AddScoped<DataAnnotationKeyExtractor>();
            services.AddScoped<CshtmlLocalizer>();
            services.AddScoped<CSharpKeyExtractor>();
            services.AddTaskCanon();
            services.AddScoped<OllamaBasedTranslatorEngine>();
            services.AddScoped<CachedTranslateEngine>();
            services.AddGptClient();
            services.AddTransient<TranslateEntry>();
            services.AddTransient<LocalizationService>();
        });
}
