using System.Text.Json.Serialization;

namespace Aiursoft.GeminiBot.Models;

public class GitLabCommit
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTime Created_at { get; set; }
}

public class GitLabDiscussion
{
    [JsonPropertyName("notes")]
    public IEnumerable<GitLabNote> Notes { get; set; } = [];
}

public class GitLabNote
{
    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public GitLabUser Author { get; set; } = new();

    [JsonPropertyName("created_at")]
    public DateTime Created_at { get; set; }

    [JsonPropertyName("system")]
    public bool System { get; set; }
}

public class GitLabUser
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;
}

public class GitLabMergeRequestDto
{
    [JsonPropertyName("iid")]
    public int Iid { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("project_id")]
    public int ProjectId { get; set; }

    [JsonPropertyName("source_project_id")]
    public int SourceProjectId { get; set; }

    [JsonPropertyName("source_branch")]
    public string SourceBranch { get; set; } = string.Empty;

    [JsonPropertyName("target_branch")]
    public string TargetBranch { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public GitLabUser Author { get; set; } = new();
}
