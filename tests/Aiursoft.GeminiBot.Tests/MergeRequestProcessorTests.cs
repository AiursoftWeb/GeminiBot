using Aiursoft.GeminiBot.Configuration;
using Aiursoft.GeminiBot.Services;
using Aiursoft.GeminiBot.Models;
using Aiursoft.GeminiBot.Services.Abstractions;
using Aiursoft.NugetNinja.GitServerBase.Models;
using Aiursoft.NugetNinja.GitServerBase.Models.Abstractions;
using Aiursoft.NugetNinja.GitServerBase.Services;
using Aiursoft.NugetNinja.GitServerBase.Services.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System.Text.Json;
using System.Net;

namespace Aiursoft.GeminiBot.Tests;

public class FakeHttpMessageHandler : DelegatingHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return _handler(request);
    }
}

[TestClass]
public class MergeRequestProcessorTests
{
    private Mock<LocalizationService> _localizationServiceMock = null!;
    private Mock<IVersionControlService> _versionControlMock = null!;
    private Mock<IGeminiWorkspaceManager> _workspaceManagerMock = null!;
    private HttpWrapper _httpWrapper = null!;
    private Mock<GeminiCliService> _geminiCliServiceMock = null!;
    private Mock<IGeminiCommandService> _commandServiceMock = null!;
    private Mock<ILogger<MergeRequestProcessor>> _loggerMock = null!;
    private IOptions<GeminiBotOptions> _options = null!;
    private List<GitLabMergeRequestDto> _gitLabMrList = new();
    private GitLabUser _botUser = new();
    private List<GitLabMergeRequestDto> _botMrList = new();
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    [TestInitialize]
    public void SetUp()
    {
        var options = new GeminiBotOptions
        {
            WorkspaceFolder = Path.Combine(Path.GetTempPath(), "GeminiBotTests"),
            ForkWaitDelayMs = 0
        };
        _options = Options.Create(options);

        _commandServiceMock = new Mock<IGeminiCommandService>();
        
        _localizationServiceMock = new Mock<LocalizationService>(
            null!, // TranslateEntry
            _options,
            new Mock<ILogger<LocalizationService>>().Object);
            
        _versionControlMock = new Mock<IVersionControlService>();
        
        _workspaceManagerMock = new Mock<IGeminiWorkspaceManager>();
            
        _geminiCliServiceMock = new Mock<GeminiCliService>(
            _commandServiceMock.Object,
            _options,
            new Mock<ILogger<GeminiCliService>>().Object);
            
        _loggerMock = new Mock<ILogger<MergeRequestProcessor>>();

        // Mock HttpWrapper by mocking HttpClient
        var handler = new FakeHttpMessageHandler((req) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Get && url.Contains("merge_requests") && url.Contains("scope=assigned_to_me"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(_gitLabMrList))
                });
            }
            if (req.Method == HttpMethod.Get && url.Contains("merge_requests") && url.Contains("discussions"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]")
                });
            }
            if (req.Method == HttpMethod.Get && url.Contains("merge_requests") && url.Contains("commits"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]")
                });
            }
            if (req.Method == HttpMethod.Get && url.EndsWith("/api/v4/user"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(_botUser))
                });
            }
            if (req.Method == HttpMethod.Put && url.Contains("merge_requests/") && url.Contains("assignee_ids="))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}")
                });
            }
            if (req.Method == HttpMethod.Get && url.Contains("source_branch=fix-mr-1"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(_botMrList))
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var client = new HttpClient(handler);
        _httpWrapper = new HttpWrapper(new Mock<ILogger<HttpWrapper>>().Object, client);
    }

    [TestMethod]
    public async Task ProcessMergeRequestsAsync_OthersMr_ForksAndCreatesNewMr()
    {
        // Arrange
        var server = new Server
        {
            Provider = "GitLab",
            UserName = "bot-user",
            Token = "token",
            EndPoint = "https://gitlab.com",
            DisplayName = "Bot",
            UserEmail = "bot@aiursoft.com"
        };

        var gitLabMr = new GitLabMergeRequestDto
        {
            Iid = 1,
            Title = "Test MR",
            ProjectId = 101,
            SourceProjectId = 102,
            SourceBranch = "feature",
            TargetBranch = "main",
            Author = new GitLabUser { Username = "other-user", Id = 999 }
        };
        _gitLabMrList = new List<GitLabMergeRequestDto> { gitLabMr };
        _botUser = new GitLabUser { Id = 123, Username = "bot-user" };
        _botMrList = new List<GitLabMergeRequestDto> { new GitLabMergeRequestDto { Iid = 2, Title = "Replacement MR" } };

        var detailedMr = JsonSerializer.Deserialize<DetailedMergeRequest>(@"
        {
            ""HasConflicts"": false,
            ""MrPipeline"": { ""Status"": ""failed"", ""Id"": 555, ""WebUrl"": ""http://gitlab.com/pipeline/555"" }
        }", _jsonOptions)!;

        var repository = new Repository
        {
            CloneUrl = "https://gitlab.com/other-user/repo.git",
            Name = "repo",
            Owner = new User { Login = "other-user" }
        };

        var failedJob = JsonSerializer.Deserialize<PipelineJob>(@"
        {
            ""Id"": 1,
            ""Name"": ""test"",
            ""Status"": ""failed"",
            ""Stage"": ""test""
        }", _jsonOptions)!;

        _versionControlMock
            .Setup(v => v.GetMergeRequestDetails(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(detailedMr);

        _versionControlMock
            .Setup(v => v.GetRepository(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(repository);

        _versionControlMock
            .Setup(v => v.GetPipelineJobs(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new List<PipelineJob> { failedJob });

        _versionControlMock
            .Setup(v => v.GetJobLog(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync("Build failed at line 42");

        _versionControlMock
            .Setup(v => v.RepoExists(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        _geminiCliServiceMock
            .Setup(g => g.InvokeGeminiCliAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(true);

        _workspaceManagerMock
            .Setup(w => w.PendingCommit(It.IsAny<string>()))
            .ReturnsAsync(true);

        _workspaceManagerMock
            .Setup(w => w.CommitToBranch(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        _commandServiceMock
            .Setup(c => c.RunCommandAsync(
                It.IsAny<string>(), 
                It.IsAny<string>(), 
                It.IsAny<string>(), 
                It.IsAny<TimeSpan>(), 
                It.IsAny<bool>(), 
                It.IsAny<IDictionary<string, string?>>()))
            .ReturnsAsync((0, "1", ""));

        var processor = new MergeRequestProcessor(
            _localizationServiceMock.Object,
            _versionControlMock.Object,
            _workspaceManagerMock.Object,
            _httpWrapper,
            _geminiCliServiceMock.Object,
            _commandServiceMock.Object,
            _options,
            _loggerMock.Object);

        // Act
        var result = await processor.ProcessMergeRequestsAsync(server);

        // Assert
        Assert.IsTrue(result.Success);
        
        // Verify new MR creation
        _versionControlMock.Verify(v => v.CreatePullRequest(
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<string>(), 
            It.IsAny<string>()), Times.Once);
    }
    
    [TestMethod]
    public async Task ProcessMergeRequestsAsync_OwnMr_PushesToOriginalBranch()
    {
        // Arrange
        var server = new Server
        {
            Provider = "GitLab",
            UserName = "bot-user",
            Token = "token",
            EndPoint = "https://gitlab.com",
            DisplayName = "Bot",
            UserEmail = "bot@aiursoft.com"
        };

        var gitLabMr = new GitLabMergeRequestDto
        {
            Iid = 1,
            Title = "Test MR",
            ProjectId = 101,
            SourceProjectId = 101, // Same project
            SourceBranch = "fix-bug",
            TargetBranch = "main",
            Author = new GitLabUser { Username = "bot-user", Id = 123 }
        };
        _gitLabMrList = new List<GitLabMergeRequestDto> { gitLabMr };

        var detailedMr = JsonSerializer.Deserialize<DetailedMergeRequest>(@"
        {
            ""HasConflicts"": false,
            ""MrPipeline"": { ""Status"": ""failed"", ""Id"": 555, ""WebUrl"": ""http://gitlab.com/pipeline/555"" }
        }", _jsonOptions)!;

        var repository = new Repository
        {
            CloneUrl = "https://gitlab.com/bot-user/repo.git",
            Name = "repo",
            Owner = new User { Login = "bot-user" }
        };

        var failedJob = JsonSerializer.Deserialize<PipelineJob>(@"
        {
            ""Id"": 1,
            ""Name"": ""test"",
            ""Status"": ""failed"",
            ""Stage"": ""test""
        }", _jsonOptions)!;

        _versionControlMock
            .Setup(v => v.GetMergeRequestDetails(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(detailedMr);

        _versionControlMock
            .Setup(v => v.GetRepository(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(repository);

        _versionControlMock
            .Setup(v => v.GetPipelineJobs(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new List<PipelineJob> { failedJob });

        _versionControlMock
            .Setup(v => v.GetJobLog(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync("Build failed");

        _geminiCliServiceMock
            .Setup(g => g.InvokeGeminiCliAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(true);

        _workspaceManagerMock
            .Setup(w => w.PendingCommit(It.IsAny<string>()))
            .ReturnsAsync(true);

        _workspaceManagerMock
            .Setup(w => w.CommitToBranch(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        _commandServiceMock
            .Setup(c => c.RunCommandAsync(
                It.IsAny<string>(), 
                It.IsAny<string>(), 
                It.IsAny<string>(), 
                It.IsAny<TimeSpan>(), 
                It.IsAny<bool>(), 
                It.IsAny<IDictionary<string, string?>>()))
            .ReturnsAsync((0, "1", ""));

        var processor = new MergeRequestProcessor(
            _localizationServiceMock.Object,
            _versionControlMock.Object,
            _workspaceManagerMock.Object,
            _httpWrapper,
            _geminiCliServiceMock.Object,
            _commandServiceMock.Object,
            _options,
            _loggerMock.Object);

        // Act
        var result = await processor.ProcessMergeRequestsAsync(server);

        // Assert
        Assert.IsTrue(result.Success);
        
        // Verify push to original branch
        _workspaceManagerMock.Verify(w => w.Push(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), true), Times.Once);
        
        // Verify NO new MR was created
        _versionControlMock.Verify(v => v.CreatePullRequest(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}