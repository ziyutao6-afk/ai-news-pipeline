# Local Deployment Guide

This project is an Azure Functions app that scans AI, crypto, policy, market-risk, and big-tech RSS feeds every 15 minutes, scores stories, generates Chinese X posts with the OpenAI API, and posts only high-signal news.

## What Changed

- Added `NewsAutoTweet`, a 15-minute RSS news automation function.
- Added `NewsAutoTweetTest`, a local dry-run HTTP endpoint.
- Added OpenAI tweet generation using `OPENAI_API_KEY`.
- Added five-factor scoring: timeliness, investment impact, controversy, global attention, and virality.
- Posts stories scoring `MIN_SCORE=80` or higher, with a fallback pool for the top stories scoring at least `FALLBACK_MIN_SCORE=70` when nothing reaches the main threshold.
- Limits standard autoposts to `NEWS_DAILY_POST_LIMIT=8` per day.
- Allows major breaking stories scoring `NEWS_BREAKING_SCORE=90` or higher to use an extra daily pool.
- Added dedupe state so the same article is not published twice.
- Added `DRY_RUN=true` mode so tweets print to logs and terminal instead of calling X.
- Kept the original X OAuth 1.0a posting client.
- Legacy Copilot / VS Code release-posting timers are disabled by default with `ENABLE_LEGACY_RELEASE_FUNCTIONS=false`.

## Install Dependencies

Install these locally:

```bash
brew install --cask dotnet-sdk
brew tap azure/functions
brew install azure-functions-core-tools@4
npm install -g azurite
```

If Homebrew is not available, install the .NET 10 SDK from Microsoft's official .NET download page, then install the local Azure tools with npm:

```bash
npm install -g azure-functions-core-tools@4 --unsafe-perm true
npm install -g azurite
```

Confirm the tools are available:

```bash
dotnet --info
func --version
azurite --version
```

The project currently targets `.NET 10` in `AutoTweetRss.csproj`.

## Configure Local Settings

Copy the example file:

```bash
cp local.settings.json.example local.settings.json
```

Keep `local.settings.json` private. It contains secrets.

## Configure OpenAI

Set:

```json
"OPENAI_API_KEY": "sk-your-openai-api-key",
"OPENAI_MODEL": "gpt-4o-mini",
"OPENAI_BASE_URL": "https://api.openai.com/v1"
```

The app sends each candidate story title, summary, category, source, timestamp, and link to OpenAI. The model returns:

- English tweet text
- Five score components
- Total score from 0 to 100
- Breaking-news flag
- Short rationale

The prompt explicitly tells the model not to invent facts and not to provide direct investment advice.

## Configure X API

In the X Developer Portal, create or use an app with Read and Write permissions. Generate OAuth 1.0a user credentials and set:

```json
"TWITTER_API_KEY": "your-x-consumer-api-key",
"TWITTER_API_SECRET": "your-x-consumer-api-secret",
"TWITTER_ACCESS_TOKEN": "your-x-access-token",
"TWITTER_ACCESS_TOKEN_SECRET": "your-x-access-token-secret"
```

These are the original project's X OAuth fields, and the existing OAuth posting client is still used.

## Configure RSS Sources

`NEWS_RSS_FEEDS` uses this format:

```text
Category|Source name|RSS URL;Category|Source name|RSS URL
```

Default categories are:

- AI News
- Crypto News
- Policy / Regulation
- Market Risk
- Big Tech

You can replace or add feeds in `local.settings.json`.

## Safety Settings

Use dry run while testing:

```json
"DRY_RUN": "true"
```

With `DRY_RUN=true`:

- RSS feeds are fetched.
- OpenAI generates tweets and scores.
- Qualified tweets print to logs and the test endpoint.
- No X API post is made.
- Publish state is not updated.

To really publish:

```json
"DRY_RUN": "false"
```

## Scoring and Rate Limits

Recommended defaults:

```json
"MIN_SCORE": "80",
"FALLBACK_TOP_N": "3",
"FALLBACK_MIN_SCORE": "70",
"MAX_TWEETS_PER_RUN": "3",
"USE_OPENAI_FOR_SCORING": "false",
"USE_OPENAI_FOR_TWEET": "true",
"LOCAL_SCORE_MIN": "70",
"OPENAI_MAX_CANDIDATES": "3",
"NEWS_MIN_SCORE": "80",
"NEWS_BREAKING_SCORE": "90",
"NEWS_DAILY_POST_LIMIT": "8",
"NEWS_BREAKING_EXTRA_DAILY_LIMIT": "3",
"NEWS_MAX_POSTS_PER_RUN": "3",
"NEWS_MAX_CANDIDATES_PER_RUN": "20",
"NEWS_MAX_ARTICLE_AGE_HOURS": "48"
```

Stories scoring at least `MIN_SCORE=80` are eligible. If no story reaches `MIN_SCORE`, the app selects up to `FALLBACK_TOP_N=3` top-scoring fallback stories, but only when they score at least `FALLBACK_MIN_SCORE=70`. Once 8 standard posts have gone out for the UTC day, only breaking stories scoring at least `90` can use the extra breaking-news pool.

## Low-Cost OpenAI Mode

By default, OpenAI is not used for scoring. The app first applies local keyword/source/category/time scoring, keeps stories above `LOCAL_SCORE_MIN`, then sends only the top `OPENAI_MAX_CANDIDATES` stories to OpenAI for Chinese tweet generation.

Set `USE_OPENAI_FOR_SCORING=false` to keep scoring local. Set `USE_OPENAI_FOR_TWEET=false` to use local template tweets for dry-run testing only; template tweets are marked and should not be used for real publishing.

## Start Local Storage

Azure Functions timer and dedupe state need local storage. Start Azurite in one terminal:

```bash
azurite --silent --location ./azurite --debug ./azurite/debug.log
```

Keep this terminal running.

## Run Locally

In a second terminal:

```bash
func start
```

The timer runs every 15 minutes:

```text
0 */15 * * * *
```

## Test Without Posting

With the function host running:

```bash
curl "http://localhost:7071/api/news/test?maxPosts=2"
```

This forces dry-run mode for the request and returns a plain-text preview with:

- Fetched article count
- Candidate count after duplicate and age filters
- Number passing the score threshold
- Generated tweet previews
- Simulated publish count

## Confirm Real X Publishing

1. Set `DRY_RUN=false`.
2. Confirm all four `TWITTER_*` OAuth values are present.
3. Start Azurite.
4. Run `func start`.
5. Watch logs for:

```text
Posting tweet
Tweet posted successfully. Tweet ID: ...
Publish succeeded
```

6. Check the X account timeline for the published post.

## Logs To Watch

The news function logs:

- How many RSS articles were fetched
- How many candidates remain after dedupe and age filters
- Each story score and score components
- How many stories passed the threshold
- Generated tweet text in dry-run mode
- Whether posting succeeded or failed

## Common Errors

### `dotnet: command not found`

Install the .NET SDK, then open a new terminal:

```bash
brew install --cask dotnet-sdk
```

### `func: command not found`

Install Azure Functions Core Tools:

```bash
brew tap azure/functions
brew install azure-functions-core-tools@4
```

### `AzureWebJobsStorage` or storage connection error

Start Azurite and keep it running:

```bash
azurite --silent --location ./azurite --debug ./azurite/debug.log
```

Make sure these values exist:

```json
"AzureWebJobsStorage": "UseDevelopmentStorage=true",
"AZURE_STORAGE_CONNECTION_STRING": "UseDevelopmentStorage=true"
```

### OpenAI request fails

Check:

- `OPENAI_API_KEY` is correct.
- `OPENAI_MODEL` exists for your account.
- The machine has internet access.
- Your OpenAI account has available quota.

### X returns unauthorized or forbidden

Check:

- OAuth 1.0a credentials, not OAuth 2 bearer-only credentials.
- The X app has Read and Write permissions.
- Access token was regenerated after enabling Write permissions.
- System clock is accurate, because OAuth signatures are time-sensitive.

### No tweets are posted

Check:

- `DRY_RUN=false` for real publishing.
- Stories are scoring at least `MIN_SCORE`, or fallback stories are scoring at least `FALLBACK_MIN_SCORE`.
- Daily limit has not been reached.
- Logs do not show duplicate-filter skips.

## Useful Local Commands

Start storage:

```bash
azurite --silent --location ./azurite --debug ./azurite/debug.log
```

Start the function app:

```bash
func start
```

Run a dry-run preview:

```bash
curl "http://localhost:7071/api/news/test?maxPosts=2"
```
