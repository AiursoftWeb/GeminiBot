using Aiursoft.GeminiBot.Models;
using Aiursoft.NugetNinja.GitServerBase.Models;
using Aiursoft.NugetNinja.GitServerBase.Services;
using Aiursoft.NugetNinja.GitServerBase.Services.Providers;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Net.Http.Headers;

namespace Aiursoft.GeminiBot.Services;

/// <summary>
/// Handles checking for failing pipelines on starred projects and creating issues.
/// </summary>
public class PipelineProcessor
{
    private readonly IVersionControlService _versionControl;
    private readonly HttpWrapper _httpWrapper;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PipelineProcessor> _logger;

    public PipelineProcessor(
        IVersionControlService versionControl,
        HttpWrapper httpWrapper,
        IHttpClientFactory httpClientFactory,
        ILogger<PipelineProcessor> logger)
    {
        _versionControl = versionControl;
        _httpWrapper = httpWrapper;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<ProcessResult> ProcessStarredProjectsAsync(Server server)
    {
        if (server.Provider != "GitLab")
        {
            _logger.LogInformation("Pipeline monitoring is currently only supported for GitLab. Skipping server {EndPoint}", server.EndPoint);
            return ProcessResult.Succeeded("Skipped non-GitLab server");
        }

        try
        {
            var starredProjects = await GetStarredProjectsAsync(server);
            if (starredProjects.Count == 0)
            {
                _logger.LogInformation("No starred projects found.");
                return ProcessResult.Succeeded("No starred projects");
            }

            _logger.LogInformation("Found {Count} starred projects. Checking pipelines...", starredProjects.Count);

            foreach (var project in starredProjects)
            {
                await CheckProjectPipelineAsync(server, project);
            }

            return ProcessResult.Succeeded($"Checked pipelines for {starredProjects.Count} projects");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing pipelines for starred projects");
            return ProcessResult.Failed("Error processing pipelines", ex);
        }
    }

    private async Task<List<GitLabProjectDto>> GetStarredProjectsAsync(Server server)
    {
        var url = $"{server.EndPoint.TrimEnd('/')}/api/v4/projects?starred=true&per_page=100&membership=true";
        return await _httpWrapper.SendHttpAndGetJson<List<GitLabProjectDto>>(url, HttpMethod.Get, server.Token);
    }

    private async Task CheckProjectPipelineAsync(Server server, GitLabProjectDto project)
    {
        try
        {
            _logger.LogInformation("Checking pipeline for project {ProjectName} (ID: {ProjectId})...", project.Name, project.Id);

            var pipelinesUrl = $"{server.EndPoint.TrimEnd('/')}/api/v4/projects/{project.Id}/pipelines?ref={project.DefaultBranch}&per_page=1";
            var pipelines = await _httpWrapper.SendHttpAndGetJson<List<GitLabPipelineDto>>(pipelinesUrl, HttpMethod.Get, server.Token);

            var latestPipeline = pipelines.FirstOrDefault();
            if (latestPipeline != null && latestPipeline.Status == "failed")
            {
                _logger.LogWarning("Pipeline failed for {ProjectName} on branch {Branch}. Checking if issue already exists...", project.Name, project.DefaultBranch);
                
                var issueTitle = "主分支的编译管道失败";
                if (await IssueAlreadyExistsAsync(server, project.Id, issueTitle))
                {
                    _logger.LogInformation("Issue already exists and is assigned to bot. Skipping.");
                    return;
                }

                _logger.LogInformation("Creating issue for failed pipeline in {ProjectName}...", project.Name);
                var logs = await GetFailureLogsAsync(server, project.Id, latestPipeline.Id);
                await CreateIssueAsync(server, project.Id, issueTitle, logs);
            }
            else
            {
                _logger.LogInformation("Pipeline for {ProjectName} is {Status}.", project.Name, latestPipeline?.Status ?? "not found");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check pipeline for project {ProjectName}", project.Name);
        }
    }

    private async Task<bool> IssueAlreadyExistsAsync(Server server, int projectId, string title)
    {
        var url = $"{server.EndPoint.TrimEnd('/')}/api/v4/projects/{projectId}/issues?state=opened&search={Uri.EscapeDataString(title)}&assignee_username={server.UserName}";
        var issues = await _httpWrapper.SendHttpAndGetJson<List<GitLabIssueDto>>(url, HttpMethod.Get, server.Token);
        return issues.Any(i => i.Title == title);
    }

    private async Task CreateIssueAsync(Server server, int projectId, string title, string description)
    {
        var userUrl = $"{server.EndPoint.TrimEnd('/')}/api/v4/user";
        var user = await _httpWrapper.SendHttpAndGetJson<GitLabUser>(userUrl, HttpMethod.Get, server.Token);

        var url = $"{server.EndPoint.TrimEnd('/')}/api/v4/projects/{projectId}/issues";
        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", server.Token);
        
        var response = await client.PostAsJsonAsync(url, new
        {
            title,
            description,
            assignee_ids = new[] { user.Id }
        });
        
        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Successfully created and assigned issue in project {ProjectId}", projectId);
        }
        else
        {
            var error = await response.Content.ReadAsStringAsync();
            _logger.LogError("Failed to create issue in project {ProjectId}: {Error}", projectId, error);
        }
    }

    private async Task<string> GetFailureLogsAsync(Server server, int projectId, int pipelineId)
    {
        try
        {
            var jobs = await _versionControl.GetPipelineJobs(server.EndPoint, server.Token, projectId, pipelineId);
            var failedJobs = jobs.Where(j => j.Status == "failed").ToList();
            var allLogs = new StringBuilder();
            allLogs.AppendLine("主分支的编译管道失败。");
            allLogs.AppendLine($"Pipeline ID: {pipelineId}");
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting failure logs for pipeline {PipelineId}", pipelineId);
            return "Failed to fetch logs.";
        }
    }
}
