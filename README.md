# Auto Tweet RSS

An Azure Function that monitors RSS feeds, generates English X post drafts plus Chinese briefs, and sends them to Telegram for manual review and posting.

## Table of Contents

- [Features](#features)
- [Tweet Formats](#tweet-formats)
- [Prerequisites](#prerequisites)
- [Configuration](#configuration)
- [Local Development](#local-development)
- [Functions and Endpoints](#functions-and-endpoints)
- [Project Structure](#project-structure)
- [Deployment to Azure](#deployment-to-azure)
- [How It Works](#how-it-works)
- [License](#license)

## Features

- Monitors GitHub Copilot CLI and Copilot SDK Atom feeds
- Polls feeds every 15 minutes
- Filters out pre-release versions and submodule releases
- **AI-powered threaded posts**: Uses Microsoft.Extensions.AI with Azure OpenAI to generate concise, emoji-enhanced threads with top highlights in the first post, grouped follow-up posts, and the release link in the final post
- **Optional Premium X mega-posts**: Per-account settings can switch X output from thread mode to a single post (up to 25,000 chars) organized into Top features, Enhancements, Bug fixes, and Misc
- **Deterministic fallback**: When AI is unavailable, threads are built from HTML-parsed release notes so posting always succeeds
- Supports Telegram review cards with source link, source image preview, AI market impact analysis, and a `🚀 Post to X` button
- Manual mode does not log in to X, store X passwords, store browser cookies, upload media to X, or click publish for you
- X Web Intent can only prefill text and links; images are sent separately in Telegram and must be uploaded to X manually
- Real one-click Telegram publishing uses the existing X API module and remains behind `DRY_RUN=false` plus `X_API_ENABLED=true`
- Cross-posts VS Code automation to Bluesky (with AT Protocol reply thread support)
- Tracks state in Azure Blob Storage to prevent duplicate posts (separate state for CLI and SDK)
- Respects platform character limits (280 for X, 300 for Bluesky) per post in the thread

## Thread Formats

Each stream now publishes a **thread** (reply chain) instead of a single post. AI is used to rank highlights and group follow-up posts; a deterministic fallback ensures posting succeeds when AI is unavailable.

### Thread Structure (all streams)

```
Post 1 (first post):
🚀 <Release title>
<N> new additions 🧵

✨ Top highlight 1
⚡ Top highlight 2
🐛 Top highlight 3

See thread below 👇

Post 2..N (follow-up posts, per-group highlights):
✨ Feature 4
✨ Feature 5
⚡ Feature 6

Post last:
https://github.com/.../releases/tag/v1.2.3

#GitHubCopilotCLI
```

### Copilot CLI

- First post: release header, addition count, top 3 highlights, thread lead-in
- Follow-up posts: remaining grouped highlights
- Last post: release URL + `#GitHubCopilotCLI`

### Copilot SDK

- Same structure as CLI, with `#GitHubCopilotSDK`

### VS Code Insiders Daily

- First post: date header, feature count, top highlights
- Follow-up posts: remaining feature groups
- Last post: Insiders update URL + `#vscode`

### VS Code Insiders Weekly Recap

- First post: date range header, total feature count, top highlights
- Follow-up posts: remaining feature groups
- Last post: release notes URL + `#vscode`

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Azure Functions Core Tools v4](https://docs.microsoft.com/azure/azure-functions/functions-run-local)
- [Azurite](https://docs.microsoft.com/azure/storage/common/storage-use-azurite) (for local development) or an Azure Storage account
- Telegram bot token and chat ID for manual review messages
- Twitter/X Developer credentials are not required for the default manual posting mode
- Bluesky account + App Password (only required if you want VS Code cross-posting)

## Configuration

### local.settings.json

Create a `local.settings.json` file in the project root (this file is git-ignored):

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    
    "X_API_ENABLED": "false",
    "MANUAL_X_POST_MODE": "true",
    "GENERATE_X_INTENT_LINK": "true",

    "TELEGRAM_ENABLED": "true",
    "TELEGRAM_REVIEW_MODE": "true",
    "TELEGRAM_BOT_USERNAME": "@your_bot_username",
    "TELEGRAM_BOT_TOKEN": "<your-telegram-bot-token>",
    "TELEGRAM_CHAT_ID": "<your-telegram-chat-id>",
    "ENABLE_TWEET_IMAGE": "true",
    "DEFAULT_TWEET_IMAGE_PATH": "assets/default-news.png",
    "MIN_AI_IMPACT_SCORE": "7",

    "TWITTER_API_KEY": "",
    "TWITTER_API_SECRET": "",
    "TWITTER_ACCESS_TOKEN": "",
    "TWITTER_ACCESS_TOKEN_SECRET": "",

    "TWITTER_VSCODE_API_KEY": "<your-vscode-consumer-key>",
    "TWITTER_VSCODE_API_SECRET": "<your-vscode-consumer-secret>",
    "TWITTER_VSCODE_ACCESS_TOKEN": "<your-vscode-access-token>",
    "TWITTER_VSCODE_ACCESS_TOKEN_SECRET": "<your-vscode-access-token-secret>",

   "BLUESKY_HANDLE": "<your-handle.bsky.social>",
   "BLUESKY_APP_PASSWORD": "<your-app-password>",
    
    "AZURE_STORAGE_CONNECTION_STRING": "UseDevelopmentStorage=true",
    "STATE_CONTAINER_NAME": "release-state",
    
    "RSS_FEED_URL": "https://github.com/github/copilot-cli/releases.atom",
    
    "AI_ENDPOINT": "<your-azure-openai-endpoint>",
    "AI_API_KEY": "<your-azure-openai-api-key>",
    "AI_MODEL": "gpt-4o-mini",
    "ENABLE_AI_SUMMARIES": "false",
      "AI_THREAD_PLAN_TIMEOUT_SECONDS": "60"
  }
}
```

### Environment Variables

| Variable | Description | Required |
|----------|-------------|----------|
| `AzureWebJobsStorage` | Azure Storage connection for Functions runtime | Yes |
| `FUNCTIONS_WORKER_RUNTIME` | Must be `dotnet-isolated` | Yes |
| `X_API_ENABLED` | Enables real X API calls when `true`. Keep `false` for zero X API cost review/dry-run mode. | No (default: `true`) |
| `MANUAL_X_POST_MODE` | Sends Telegram review messages and skips timer-based live publishing when `true`. The Telegram button can still publish when `DRY_RUN=false` and `X_API_ENABLED=true`. | No (recommended: `true`) |
| `GENERATE_X_INTENT_LINK` | Adds `https://twitter.com/intent/tweet?text=...` draft links to Telegram messages. | No (default: `true`) |
| `TELEGRAM_ENABLED` | Enables Telegram review notifications. | Yes for manual mode |
| `TELEGRAM_REVIEW_MODE` | Keeps generated posts in review instead of live publishing. | Yes for manual mode |
| `TELEGRAM_BOT_USERNAME` | Telegram bot username shown in diagnostics. | No |
| `TELEGRAM_BOT_TOKEN` | Telegram bot token. | Yes for manual mode |
| `TELEGRAM_CHAT_ID` | Telegram chat ID to receive review messages. | Yes for manual mode |
| `ENABLE_TWEET_IMAGE` | Sends image previews to Telegram when an article image/default image is available. | No (default: `true`) |
| `DEFAULT_TWEET_IMAGE_PATH` | Local fallback image path used when no article image is found. | No |
| `MIN_AI_IMPACT_SCORE` | Minimum GPT market-impact score for priority/autopost eligibility. Lower scores are still sent to Telegram as `Low impact`. | No (default: `7`) |
| `TWITTER_API_KEY` | Legacy Twitter OAuth 1.0a Consumer Key. Leave empty when `X_API_ENABLED=false`. | No |
| `TWITTER_API_SECRET` | Legacy Twitter OAuth 1.0a Consumer Secret. Leave empty when `X_API_ENABLED=false`. | No |
| `TWITTER_ACCESS_TOKEN` | Legacy Twitter OAuth 1.0a Access Token. Leave empty when `X_API_ENABLED=false`. | No |
| `TWITTER_ACCESS_TOKEN_SECRET` | Legacy Twitter OAuth 1.0a Access Token Secret. Leave empty when `X_API_ENABLED=false`. | No |
| `TWITTER_VSCODE_API_KEY` | Twitter OAuth 1.0a Consumer Key for VS Code updates | Yes (for VS Code tweets) |
| `TWITTER_VSCODE_API_SECRET` | Twitter OAuth 1.0a Consumer Secret for VS Code updates | Yes (for VS Code tweets) |
| `TWITTER_VSCODE_ACCESS_TOKEN` | Twitter OAuth 1.0a Access Token for VS Code updates | Yes (for VS Code tweets) |
| `TWITTER_VSCODE_ACCESS_TOKEN_SECRET` | Twitter OAuth 1.0a Access Token Secret for VS Code updates | Yes (for VS Code tweets) |
| `BLUESKY_HANDLE` | Bluesky handle (e.g., `yourhandle.bsky.social`) | No (only required for VS Code cross-posting) |
| `BLUESKY_APP_PASSWORD` | Bluesky App Password (Settings → App passwords) | No (only required for VS Code cross-posting) |
| `AZURE_STORAGE_CONNECTION_STRING` | Connection string for state tracking blob storage | Yes |
| `STATE_CONTAINER_NAME` | Blob container name for state file | No (default: `release-state`) |
| `RSS_FEED_URL` | Atom feed URL to monitor | No (default: Copilot CLI releases) |
| `AI_ENDPOINT` | Azure OpenAI endpoint URL (e.g., `https://your-resource.openai.azure.com/`) | No (if not set, falls back to manual extraction) |
| `AI_API_KEY` | Azure OpenAI API key | No (if not set, falls back to manual extraction) |
| `AI_MODEL` | Azure OpenAI deployment model name | No (default: `gpt-4o-mini`) |
| `ENABLE_AI_SUMMARIES` | Enable AI-powered thread planning for timer functions | No (default: `false`) |
| `AI_THREAD_PLAN_TIMEOUT_SECONDS` | Timeout (seconds) for AI thread-plan requests before fallback | No (default: `60`) |
| `X_CLI_CHANGELOG_PREMIUM_MODE` | When `true`, posts CLI/SDK updates to X as one Premium mega-post instead of a thread | No (default: `false`) |
| `X_VSCODE_PREMIUM_MODE` | When `true`, posts VS Code updates to X as one Premium mega-post instead of a thread | No (default: `false`) |
| `THREAD_MAX_POSTS` | Maximum number of posts per thread (including first and last) | No (default: `6`, minimum: `2`) |
| `THREAD_TOP_HIGHLIGHTS` | Number of top highlights shown in the first post | No (default: `3`, minimum: `1`) |

### Bluesky App Password

To enable VS Code cross-posting to Bluesky:

1. Sign in to Bluesky
2. Go to **Settings → App passwords**
3. Create a new app password (store it somewhere safe)
4. Set:
   - `BLUESKY_HANDLE` (your handle)
   - `BLUESKY_APP_PASSWORD` (the generated app password)

### Telegram Manual X Posting

The default news workflow is a no X API cost setup:

1. RSS feeds are fetched.
2. Local scoring selects candidates.
3. OpenAI generates an English post draft and Chinese brief.
4. The image extractor prepares an article image or local fallback image.
5. Telegram receives one review card per candidate with:
   - Title
   - Short headline
   - AI summary
   - Market Impact: Bullish / Bearish / Neutral
   - Impact Score
   - Affected Assets
   - Winners
   - Losers
   - Trade Take
   - AI Confidence Score
   - Source URL and `source_url`
   - Image URL / Local image path
   - Final X Post
   - Inline buttons: `🚀 Post to X`, `❌ Skip`, and `🔗 Open Source`
6. With `DRY_RUN=true`, clicking the button simulates publishing and marks the item as posted to prevent duplicate clicks.
7. With `DRY_RUN=false` and `X_API_ENABLED=true`, clicking the button publishes the saved final X post through the existing X module. If a local image file exists, it is attached to the X post; otherwise the post falls back to text plus source link.

The X Web Intent link is generated as:

```text
https://twitter.com/intent/tweet?text=<url_encoded_tweet_text>
```

where `tweet_text` is:

```text
englishTweetBody + " " + article.Url
```

Important behavior:

- X Web Intent only pre-fills text and links.
- Images cannot be attached automatically through Web Intent.
- When `ENABLE_TWEET_IMAGE=true`, Telegram sends the original article/OpenGraph/Twitter-card image as a preview when available.
- Image download failures do not block review; the app degrades to text plus source link.
- The app stores each candidate as `pending`, `posted`, `skipped`, or `failed` with id, title, source URL, image URL, local image path, AI analysis, final X post, and creation time.
- The Telegram button only works for `pending` items. Already posted items cannot be posted again.
- `❌ Skip` only works for `pending` items and changes the stored status to `skipped`.
- The app does not use Selenium, Playwright, browser cookies, saved X usernames/passwords, or simulated clicks.
- With `X_API_ENABLED=false`, the app does not call X API endpoints and will not trigger X API usage fees. In that mode, keep `DRY_RUN=true` for button testing.

### Telegram Post to X Button

The inline keyboard button uses this Function endpoint:

```text
POST /api/telegram/x-callback
```

Local callback URL:

```text
http://127.0.0.1:7071/api/telegram/x-callback
```

For a deployed Function App, set the Telegram webhook to the deployed URL, including the function key if your deployment uses Function authorization:

```bash
curl "https://api.telegram.org/bot<TELEGRAM_BOT_TOKEN>/setWebhook?url=https://<your-function-app>.azurewebsites.net/api/telegram/x-callback?code=<function-key>"
```

To test the button without real X posting:

```json
{
  "DRY_RUN": "true",
  "X_API_ENABLED": "false",
  "MANUAL_X_POST_MODE": "true"
}
```

To allow the button to really publish to X:

```json
{
  "DRY_RUN": "false",
  "X_API_ENABLED": "true",
  "MANUAL_X_POST_MODE": "true",
  "TWITTER_API_KEY": "...",
  "TWITTER_API_SECRET": "...",
  "TWITTER_ACCESS_TOKEN": "...",
  "TWITTER_ACCESS_TOKEN_SECRET": "..."
}
```

### Getting Twitter OAuth 1.0a Credentials

This is only needed if you intentionally re-enable legacy automatic X API publishing with `X_API_ENABLED=true`. Manual Telegram mode does not need X developer credentials.

### Setting up Azure OpenAI (Optional but Recommended)

To enable AI-powered summaries:

1. Go to [Azure Portal](https://portal.azure.com/)
2. Create an **Azure OpenAI** resource
3. Deploy a model (recommended: `gpt-4o-mini` for cost-effectiveness, or `gpt-4o`)
4. Get your endpoint and API key from the resource's "Keys and Endpoint" section
5. Configure the environment variables:
   - `AI_ENDPOINT`: Your Azure OpenAI endpoint URL
   - `AI_API_KEY`: Your Azure OpenAI API key
   - `AI_MODEL`: Your deployment name (e.g., `gpt-4o-mini`)
   - `AI_THREAD_PLAN_TIMEOUT_SECONDS`: Optional timeout for AI thread-plan requests (default: `60`)

**Note**: If AI configuration is not provided, the system will fall back to manual HTML parsing and extraction of release notes.

## Local Development

1. **Start Azurite** (Azure Storage emulator):
   ```bash
   azurite --silent --location ./azurite --debug ./azurite/debug.log
   ```

2. **Fill in credentials** in `local.settings.json`

3. **Run the function**:
   ```bash
   func start
   ```
   
   Or press F5 in VS Code with the Azure Functions extension.

4. **Thread behavior**: 
   - All streams now publish **threads** by default (first post + follow-ups + last post with link).
   - Thread structure is always applied; AI improves the ranking/grouping when configured.
   - You can opt specific X accounts into Premium mega-post mode:
     - `X_CLI_CHANGELOG_PREMIUM_MODE=true` for CLI/SDK account
     - `X_VSCODE_PREMIUM_MODE=true` for VS Code account
   - Control thread size with `THREAD_MAX_POSTS` (default: `6`) and `THREAD_TOP_HIGHLIGHTS` (default: `3`).

5. **AI Thread Planning**: 
   - Timer functions always run every 15 minutes (automatic)
   - By default, AI thread planning is **disabled** for timer functions (`ENABLE_AI_SUMMARIES=false`)
   - When disabled, timer functions use deterministic HTML extraction for thread content
   - To enable AI thread planning for timer functions, set `ENABLE_AI_SUMMARIES=true` and configure AI endpoint/key

6. **Testing threads** without posting:
   
   Use the test endpoints to preview threads (always uses AI if configured):
   
   ```bash
   # Test CLI release thread
   curl http://localhost:7071/api/test-summary/cli
   
   # Test SDK release thread
   curl http://localhost:7071/api/test-summary/sdk
   
    # Test VS Code daily thread (today)
    curl http://localhost:7071/api/test-summary/vscode

   # Test CLI weekly recap thread
   curl http://localhost:7071/api/test-weekly-recap
   
    # Test VS Code weekly recap thread
    curl http://localhost:7071/api/test-weekly-recap/vscode

   ```
   
   Responses show numbered posts like `[Post 1/3]`, `[Post 2/3]`, `[Post 3/3]` for easy validation.

## Functions and Endpoints

### Authorization Notes

- Local dev (`func start`) uses `http://localhost:7071/api/...` and does not require a key by default.
- Deployed Function Apps require a function key unless you change the auth level. Use `?code=<function-key>` or set `x-functions-key`.

### HTTP Endpoints (local examples)

**CliReleaseSummary**

Generate an AI summary paragraph for a specific Copilot CLI version.

- Route: `GET /api/cli-summary`
- Params:
   - `version` (required)
   - `maxLength` (optional, default: 700)
   - `format` (optional: `json` or `text`, default: `json`)

```bash
curl "http://localhost:7071/api/cli-summary?version=v1.7.0&maxLength=500&format=json"
```

**TestSummary**

Preview a formatted thread for the latest CLI, SDK, or VS Code update (always uses AI when configured).

- Route: `GET /api/test-summary/{cli|sdk|vscode}`
- For VS Code: optional `?date=yyyy-MM-dd` query parameter

```bash
curl "http://localhost:7071/api/test-summary/cli"
curl "http://localhost:7071/api/test-summary/sdk"
curl "http://localhost:7071/api/test-summary/vscode?date=2026-02-01"
```

Returns numbered thread preview (e.g., `[Post 1/3]`, `[Post 2/3]`, `[Post 3/3]`) without posting.

**TestWeeklyRecap**

Preview the weekly thread for a given week window (PT).

- Route: `GET /api/test-weekly-recap/{cli|vscode}`
- Params:
   - `date` (optional, format `yyyy-MM-dd`, sets the week end date at 10:00 AM PT)

```bash
curl "http://localhost:7071/api/test-weekly-recap"
curl "http://localhost:7071/api/test-weekly-recap?date=2026-02-01"
curl "http://localhost:7071/api/test-weekly-recap/vscode?date=2026-02-01"
```

Returns numbered thread preview for validation.

**VSCodeInsiders**

Get VS Code Insiders release notes with optional AI summary.

- Route: `GET /api/vscode-insiders`
- Params:
   - `date` (optional: `yyyy-MM-dd`, `full`, `this week`, or `this-week`)
   - `format` (optional: `json` or `text`, default: `json`)
   - `forceRefresh` (optional: `true` or `false`)
   - `aionly` (optional: `true` or `false`)
   - `newline` (optional: `br`, `lf`, `crlf`, or `literal`)

```bash
curl "http://localhost:7071/api/vscode-insiders?date=this-week&format=json"
curl "http://localhost:7071/api/vscode-insiders?date=full&format=text"
```

### Timer Functions

**ReleaseNotifier**

- Schedule: `0 */15 * * * *` (every 15 minutes)
- Feed: `RSS_FEED_URL` (default Copilot CLI releases)

**SdkReleaseNotifier**

- Schedule: `0 */15 * * * *` (every 15 minutes)
- Feed: fixed `https://github.com/github/copilot-sdk/releases.atom`

**WeeklyCliRecap**

- Schedule: `0 0 17,18 * * 6` (runs Saturday; posts at 10 AM PT)
- Feed: fixed `https://github.com/github/copilot-cli/releases.atom`

**VSCodeInsidersChangelogTweet**

- Schedule: `0 */30 * * * *` (every 30 minutes, polls for today's release notes)
- Source: Raw markdown from `https://raw.githubusercontent.com/microsoft/vscode-docs/.../release-notes/v1_*.md`
- Posts: Twitter/X (VS Code account) and Bluesky (if configured)

**VSCodeWeeklyRecap**

- Schedule: `0 0 18,19 * * 6` (Saturday; posts at 10 AM PT)
- Source: Raw markdown from `https://raw.githubusercontent.com/microsoft/vscode-docs/.../release-notes/v1_*.md`
- Posts: Twitter/X (VS Code account) and Bluesky (if configured)

## Project Structure

```
auto-tweet-rss/
├── AutoTweetRss.csproj           # .NET 10 project file
├── Program.cs                     # Dependency injection setup
├── host.json                      # Azure Functions host configuration
├── local.settings.json            # Local environment variables (git-ignored)
├── Functions/
│   ├── ReleaseNotifierFunction.cs # Timer trigger for Copilot CLI (every 15 min)
│   ├── SdkReleaseNotifierFunction.cs # Timer trigger for Copilot SDK (every 15 min)
│   ├── VSCodeInsidersChangelogTweetFunction.cs # Timer trigger for VS Code insiders changelog
│   ├── VSCodeWeeklyRecapFunction.cs # Timer trigger for VS Code weekly recap (Saturday)
│   └── TestSummaryFunction.cs     # HTTP endpoint for testing AI summaries
└── Services/
   ├── Feeds/
   │   ├── RssFeedService.cs                  # Fetches and filters RSS feeds
   │   └── VSCodeReleaseNotesService.cs       # Fetches/parses VS Code release notes
   ├── Formatting/
   │   ├── TweetFormatterService.cs           # Formats thread and post content
   │   └── XPostLengthHelper.cs               # Weighted length/truncation utilities
   ├── Social/
   │   ├── Auth/
   │   │   └── OAuth1Helper.cs                # HMAC-SHA1 signature helper for X
   │   ├── Twitter/
   │   │   ├── TwitterApiClient.cs            # Direct HTTP calls to X/Twitter API v2
   │   │   ├── VSCodeTwitterApiClient.cs      # X client for VS Code account
   │   ├── BlueskyApiClient.cs                # Bluesky (AT Protocol) client
   │   ├── ISocialMediaClient.cs              # Abstraction for social clients
   │   ├── SocialMediaPost.cs                 # Post payload model
   │   └── VSCodeSocialMediaPublisher.cs      # Publishes VS Code posts to configured platforms
   ├── State/
   │   └── StateTrackingService.cs            # Blob storage for last processed IDs
   └── Summarization/
      ├── ReleaseSummarizerService.cs        # AI-powered summarization orchestrator
      ├── ReleaseSummarizerPrompts.cs        # Prompt templates/builders
      ├── ReleaseSummaryPlans.cs             # Plan/result models for AI output
      └── VSCodeSummaryCacheService.cs       # Caches VS Code summaries

> Note: Namespaces remain `AutoTweetRss.Services` after this folder regrouping.
```

## Deployment to Azure

1. Create an Azure Function App (Linux, .NET 10 Isolated)
2. Create an Azure Storage Account
3. Configure Application Settings with the environment variables above
4. Deploy using:
   ```bash
   func azure functionapp publish <your-function-app-name>
   ```

## How It Works

### Thread Generation (all streams)

Each stream now generates and posts a **thread** (reply chain):

1. **First post**: Release/update header, feature/addition count, top N highlights (emoji-prefixed), and a "See thread below 👇" lead-in. Post is capped at the platform limit (280 chars for X, 300 for Bluesky).
2. **Follow-up posts**: Remaining highlights grouped into posts of ≤4 items each.
3. **Last post**: The release/update URL and the stream's hashtag.

**AI path** (when `ENABLE_AI_SUMMARIES=true` and AI endpoint configured): Azure OpenAI ranks and groups highlights into `topHighlights` (first post) and `threadPosts` (follow-up posts) via a structured JSON response from `PlanThreadAsync`.

**Deterministic fallback** (no AI): HTML list items are extracted, the first `THREAD_TOP_HIGHLIGHTS` become the first-post highlights, and the remainder are grouped into follow-up posts automatically.

### ReleaseNotifier Function (Copilot CLI)

1. **Timer Trigger**: Runs every 15 minutes (`0 */15 * * * *`)
2. **Fetch Feed**: Downloads and parses the CLI Atom feed from https://github.com/github/copilot-cli/releases.atom
3. **Filter**: Removes entries with pre-release patterns (`-0`, `-1`, etc.) or "Pre-release" in content
4. **Check State**: Compares against last processed entry ID stored in blob storage (`last-processed-id.txt`)
5. **Thread Plan** (if `ENABLE_AI_SUMMARIES=true`): Calls Azure OpenAI for AI-generated thread plan; falls back to HTML extraction
6. **Format**: Builds a thread: first post with top highlights, follow-up posts with grouped features, last post with URL + `#GitHubCopilotCLI`
7. **Post**: Posts each tweet in sequence as a reply chain via Twitter API v2
8. **Update State**: Saves the processed entry ID to prevent duplicates

### SdkReleaseNotifier Function (Copilot SDK)

Same as CLI but targets https://github.com/github/copilot-sdk/releases.atom, uses `sdk` feed type for AI, and appends `#GitHubCopilotSDK`.

### TestSummary Function (HTTP Endpoint)

1. **HTTP Request**: GET `/api/test-summary/{cli|sdk|vscode}`
2. **Fetch Feed**: Downloads and parses the appropriate Atom feed (or VS Code notes)
3. **Get Latest**: Retrieves the most recent stable release
4. **Thread Plan** (always enabled for test): Uses Azure OpenAI to generate thread plan
5. **Format**: Builds thread format
6. **Return**: Returns numbered thread preview (e.g., `[Post 1/3]`) without posting

## License

MIT
