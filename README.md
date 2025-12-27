# Gemini Bot

[![MIT licensed](https://img.shields.io/badge/license-MIT-blue.svg)](https://gitlab.aiursoft.com/aiursoft/geminibot/-/blob/master/LICENSE)
[![Pipeline stat](https://gitlab.aiursoft.com/aiursoft/geminibot/badges/master/pipeline.svg)](https://gitlab.aiursoft.com/aiursoft/geminibot/-/pipelines)
[![Test Coverage](https://gitlab.aiursoft.com/aiursoft/geminibot/badges/master/coverage.svg)](https://gitlab.aiursoft.com/aiursoft/geminibot/-/pipelines)
[![NuGet version (aiursoft.geminibot)](https://img.shields.io/nuget/v/Aiursoft.geminibot.svg)](https://www.nuget.org/packages/Aiursoft.geminibot/)
[![Man hours](https://manhours.aiursoft.com/r/gitlab.aiursoft.com/aiursoft/geminibot.svg)](https://manhours.aiursoft.com/r/gitlab.aiursoft.com/aiursoft/geminibot.html)

Gemini Bot:

* Auto start a merge request when an issue is created and assigned.
* Auto fix failed merge requests by analyzing the pipeline logs with Gemini API.
* Auto fix merge requests that have been reviewed with change requests.

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
    "GeminiTimeout": "00:20:00",
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
