# Gemini Bot

[![MIT licensed](https://img.shields.io/badge/license-MIT-blue.svg)](https://gitlab.aiursoft.com/aiursoft/geminibot/-/blob/master/LICENSE)
[![Pipeline stat](https://gitlab.aiursoft.com/aiursoft/geminibot/badges/master/pipeline.svg)](https://gitlab.aiursoft.com/aiursoft/geminibot/-/pipelines)
[![Test Coverage](https://gitlab.aiursoft.com/aiursoft/geminibot/badges/master/coverage.svg)](https://gitlab.aiursoft.com/aiursoft/geminibot/-/pipelines)
[![NuGet version (aiursoft.geminibot)](https://img.shields.io/nuget/v/Aiursoft.geminibot.svg)](https://www.nuget.org/packages/Aiursoft.geminibot/)
[![Man hours](https://manhours.aiursoft.com/r/gitlab.aiursoft.com/aiursoft/geminibot.svg)](https://manhours.aiursoft.com/r/gitlab.aiursoft.com/aiursoft/geminibot.html)

## How it works

Gemini Bot is designed to autonomously manage the entire lifecycle of software development tasks on GitLab/GitHub. It follows a strictly prioritized workflow to ensure existing work is maintained before starting new tasks.

### 1. Workflow Priorities

The bot operates in two main phases:

#### Phase 1: Merge Request Maintenance (Highest Priority)
Before looking for new work, the bot ensures its existing contributions are healthy. It scans for Merge Requests assigned to it or created by it that need attention:
- **Conflict Resolution**: If an MR has merge conflicts, the bot merges the target branch and invokes Gemini to resolve the conflicts.
- **Addressing Reviews**: If a human reviewer provides feedback (discussions), the bot reads the feedback and asks Gemini to implement the requested changes.
- **Fixing Pipelines**: If the CI/CD pipeline fails, the bot automatically downloads the failure logs from all failed jobs and asks Gemini to fix the root cause.

#### Phase 2: Issue Resolution
Once all existing MRs are healthy, the bot looks for new issues assigned to it:
- It clones the repository and creates a dedicated branch.
- It passes the issue description to Gemini to implement the feature or fix.
- It automatically handles forking if it doesn't have direct push access to the repository.
- It creates a new Merge Request and assigns itself to it for continued maintenance.

### 2. Internal Logic Details

- **Workspace Management**: Each task is processed in a unique temporary directory within the `WorkspaceFolder`. This prevents cross-task interference.
- **Git Visibility Control**:
    - **For new issues**: The `.git` folder is hidden during Gemini's execution. This ensures Gemini focuses only on the code and doesn't attempt to manipulate git history or state.
    - **For MR maintenance**: The `.git` folder remains visible. This allows Gemini to analyze the project history and previous commits to better understand the context of failures or review comments.
- **Automatic Localization**: After Gemini completes its task, the bot optionally runs a localization pass. It scans for projects with `Resources` directories and uses `Aiursoft.Dotlang.AspNetTranslate` to ensure all strings are correctly localized across target languages.
- **Smart Pushing**:
    - If the bot has push access, it pushes directly to the source branch.
    - If fixing a third-party MR where it lacks permissions, it pushes to its own fork and creates a replacement MR, unassigning itself from the original one.
- **NuGet Versioning**: The bot is programmed to automatically bump NuGet package versions when it detects changes that warrant a new release.

## Installation

Requirements:

1. [.NET 10 SDK](http://dot.net/)

Run the following command to install this tool:

```bash
dotnet tool install --global Aiursoft.GeminiBot
```

## Usage

After getting the binary, setup your GitLab and Gemini API KEY:

```
cat ./appsettings.json
{
  "Servers": [
    {
      "Provider": "GitLab",
      "EndPoint": "https://gitlab.aiursoft.com",
      "PushEndPoint": "https://{0}@gitlab.aiursoft.com",
      "DisplayName": "Gemini Bot",
      "UserName": "gemini-bot",
      "UserEmail": "gemini@aiursoft.com",
      "ContributionBranch": "users/gemini/auto-fix-issue",
      "Token": "",
      "OnlyUpdate": false
    }
  ],
  "GeminiBot": {
    "WorkspaceFolder": "/tmp/NugetNinjaWorkspace",
    "GeminiTimeout": "00:35:00",
    "ForkWaitDelayMs": 5000,
    "Model": "gemini-3-pro-preview",
    "GeminiApiKey": ""
  }
}
```

Make sure to fill in the `Token` and `GeminiApiKey` fields with your actual GitLab personal access token and Gemini API key, respectively.

run it directly in the terminal.

```cmd
C:\workspace> gemini-bot

16:28 info: Aiursoft.GeminiBot.Entry[0] Starting Gemini Bot for issue processing...
16:28 info: Aiursoft.GeminiBot.Entry[0] Processing server: GitLab...
16:28 info: Aiursoft.GeminiBot.Entry[0]   ================ CHECKING MERGE REQUESTS ================ 
16:28 info: Aiursoft.GeminiBot.Entry[0] Checking merge requests before processing issues...
16:28 info: Aiursoft.GeminiBot.Services.MergeRequestProcessor[0] Checking merge requests submitted by gemini-bot...
16:28 info: Aiursoft.GeminiBot.Services.MergeRequestProcessor[0] Checking MR #33: Fix for issue #8: 砍掉PublicId。设计的不好。...
16:28 info: Aiursoft.GeminiBot.Services.MergeRequestProcessor[0] MR #33 pipeline is success, no action needed
16:28 info: Aiursoft.GeminiBot.Services.MergeRequestProcessor[0] Checking MR #2: Fix for issue #1: UDP测试，只要连续10个包都丢了，且一个包都没收到，立刻停止测试，给0分。不需要浪费时间了。...
16:28 info: Aiursoft.GeminiBot.Services.MergeRequestProcessor[0] MR #2 pipeline is success, no action needed
16:28 info: Aiursoft.GeminiBot.Services.MergeRequestProcessor[0] Checking MR #32: Fix for issue #9: 文档的分享功能非常confusing....
16:28 warn: Aiursoft.GeminiBot.Services.MergeRequestProcessor[0] MR #32 has pipeline with status: failed
16:28 info: Aiursoft.GeminiBot.Services.MergeRequestProcessor[0] Found 1 failed merge requests to fix
16:28 info: Aiursoft.GeminiBot.Services.MergeRequestProcessor[0] Processing failed MR #32: Fix for issue #9: 文档的分享功能非常confusing.
16:28 info: Aiursoft.GeminiBot.Services.MergeRequestProcessor[0] MR #32: Using project ID 375 for pipeline operations (source: 375, target: 341)
16:28 info: Aiursoft.GeminiBot.Services.MergeRequestProcessor[0] Getting repository details from source project 375...
16:28 info: Aiursoft.NugetNinja.GitServerBase.Services.Providers.GitLab.GitLabService[0] Getting repository details for 375/ in GitLab...
16:28 info: Aiursoft.GeminiBot.Services.MergeRequestProcessor[0] Fetching jobs for pipeline 40179 in project 375...
16:28 info: Aiursoft.GeminiBot.Services.MergeRequestProcessor[0] Found 1 failed jobs in pipeline 40179
16:28 info: Aiursoft.GeminiBot.Services.MergeRequestProcessor[0] Downloading log for failed job: lint (ID: 230579)
```

## Run locally

Requirements about how to run

1. [.NET 10 SDK](http://dot.net/)
2. Execute `dotnet run` to run the app

## Run in Microsoft Visual Studio

1. Open the `.sln` file in the project path.
2. Press `F5`.

## How to contribute

There are many ways to contribute to the project: logging bugs, submitting pull requests, reporting issues, and creating suggestions.

Even if you with push rights on the repository, you should create a personal fork and create feature branches there when you need them. This keeps the main repository clean and your workflow cruft out of sight.

We're also interested in your feedback on the future of this project. You can submit a suggestion or feature request through the issue tracker. To make this process more effective, we're asking that these include more information to help define them more clearly.
