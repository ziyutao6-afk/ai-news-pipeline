using System.Net;
using System.Text;
using System.Text.Json;
using AutoTweetRss.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Functions;

public sealed class NewsAutoTweetFunction
{
    private const string StateFileName = "news-auto-tweet-state.json";

    private readonly ILogger<NewsAutoTweetFunction> _logger;
    private readonly RssFeedService _rssFeedService;
    private readonly OpenAiNewsTweetService _tweetService;
    private readonly TelegramNotificationService _telegramNotificationService;
    private readonly TweetImageService _tweetImageService;
    private readonly TwitterApiClient _twitterApiClient;
    private readonly StateTrackingService _stateTrackingService;
    private readonly NewsPendingPostStore _pendingPostStore;

    public NewsAutoTweetFunction(
        ILogger<NewsAutoTweetFunction> logger,
        RssFeedService rssFeedService,
        OpenAiNewsTweetService tweetService,
        TelegramNotificationService telegramNotificationService,
        TweetImageService tweetImageService,
        TwitterApiClient twitterApiClient,
        StateTrackingService stateTrackingService,
        NewsPendingPostStore pendingPostStore)
    {
        _logger = logger;
        _rssFeedService = rssFeedService;
        _tweetService = tweetService;
        _telegramNotificationService = telegramNotificationService;
        _tweetImageService = tweetImageService;
        _twitterApiClient = twitterApiClient;
        _stateTrackingService = stateTrackingService;
        _pendingPostStore = pendingPostStore;
    }

    [Function("NewsAutoTweet")]
    public async Task Run([TimerTrigger("0 */2 * * * *")] TimerInfo timerInfo)
    {
        _logger.LogInformation("NewsAutoTweet started at {Time}", DateTimeOffset.UtcNow);
        await ProcessNewsAsync(forceDryRun: null, maxPostsOverride: null, maxCandidatesOverride: null);
        _logger.LogInformation("NewsAutoTweet completed at {Time}", DateTimeOffset.UtcNow);
    }

    [Function("NewsAutoTweetTest")]
    public async Task<HttpResponseData> Test(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "news/test")] HttpRequestData req)
    {
        try
        {
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "text/plain; charset=utf-8");

            var maxPosts = TryGetPositiveInt(GetQueryParameter(req, "maxPosts"));
            var maxCandidates = TryGetPositiveInt(GetQueryParameter(req, "maxCandidates"));
            var result = await ProcessNewsAsync(forceDryRun: true, maxPostsOverride: maxPosts, maxCandidatesOverride: maxCandidates);
            await response.WriteStringAsync(result);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NewsAutoTweetTest failed");

            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");

            var error = new
            {
                ok = false,
                error = ex.Message,
                stackTrace = ex.ToString(),
                env = BuildDiagnostics()
            };

            await response.WriteStringAsync(JsonSerializer.Serialize(error, new JsonSerializerOptions
            {
                WriteIndented = true
            }));

            return response;
        }
    }

    [Function("NewsAutoTweetHealth")]
    public async Task<HttpResponseData> Health(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "news/health")] HttpRequestData req)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");

        var health = new
        {
            ok = true,
            timeUtc = DateTimeOffset.UtcNow,
            env = BuildDiagnostics(),
            config = new
            {
                useOpenAIForScoring = IsEnabled("USE_OPENAI_FOR_SCORING", defaultValue: false),
                useOpenAIForTweet = IsEnabled("USE_OPENAI_FOR_TWEET", defaultValue: true),
                localScoreMin = GetInt(70, "LOCAL_SCORE_MIN", "NEWS_LOCAL_SCORE_MIN"),
                openAIMaxCandidates = GetInt(3, "OPENAI_MAX_CANDIDATES", "NEWS_OPENAI_MAX_CANDIDATES"),
                minScore = GetInt(80, "MIN_SCORE", "NEWS_MIN_SCORE"),
                minAIImpactScore = GetInt("MIN_AI_IMPACT_SCORE", 7),
                dryRun = IsEnabled("DRY_RUN", defaultValue: true),
                xApiEnabled = IsEnabled("X_API_ENABLED", defaultValue: true),
                manualXPostMode = IsEnabled("MANUAL_X_POST_MODE", defaultValue: false),
                generateXIntentLink = IsEnabled("GENERATE_X_INTENT_LINK", defaultValue: true),
                telegramEnabled = _telegramNotificationService.IsEnabled,
                telegramReviewMode = _telegramNotificationService.IsReviewMode,
                telegramConfigured = _telegramNotificationService.IsConfigured,
                telegramBotUsername = _telegramNotificationService.BotUsername
            }
        };

        await response.WriteStringAsync(JsonSerializer.Serialize(health, new JsonSerializerOptions
        {
            WriteIndented = true
        }));

        return response;
    }

    private async Task<string> ProcessNewsAsync(bool? forceDryRun, int? maxPostsOverride, int? maxCandidatesOverride)
    {
        var output = new StringBuilder();
        var dryRun = forceDryRun ?? IsEnabled("DRY_RUN", defaultValue: true);
        var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
        var openAiModel = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o-mini";
        var openAiBaseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL") ?? "https://api.openai.com/v1";
        var telegramEnabled = _telegramNotificationService.IsEnabled;
        var telegramReviewMode = _telegramNotificationService.IsReviewMode;
        var telegramConfigured = _telegramNotificationService.IsConfigured;
        var xApiEnabled = IsEnabled("X_API_ENABLED", defaultValue: true);
        var manualXPostMode = IsEnabled("MANUAL_X_POST_MODE", defaultValue: false);
        var generateXIntentLink = IsEnabled("GENERATE_X_INTENT_LINK", defaultValue: true);
        var livePublishEnabled = xApiEnabled && !manualXPostMode && !dryRun && !telegramReviewMode;
        var useOpenAiForScoring = IsEnabled("USE_OPENAI_FOR_SCORING", defaultValue: false);
        var useOpenAiForTweet = IsEnabled("USE_OPENAI_FOR_TWEET", defaultValue: true);
        var openAiMaxRetries = GetInt("OPENAI_MAX_RETRIES", 1);
        var openAiRetryOnOverload = IsEnabled("OPENAI_RETRY_ON_OVERLOAD", defaultValue: false);
        var openAiStopOnServiceUnavailable = IsEnabled("OPENAI_STOP_ON_SERVICE_UNAVAILABLE", defaultValue: true);
        var localScoreMin = GetInt(70, "LOCAL_SCORE_MIN", "NEWS_LOCAL_SCORE_MIN");
        var openAiMaxCandidates = GetInt(3, "OPENAI_MAX_CANDIDATES", "NEWS_OPENAI_MAX_CANDIDATES");
        var minimumScore = GetInt(80, "MIN_SCORE", "NEWS_MIN_SCORE");
        var minAiImpactScore = GetInt("MIN_AI_IMPACT_SCORE", 7);
        var fallbackTopN = GetInt(3, "FALLBACK_TOP_N", "NEWS_FALLBACK_TOP_N");
        var fallbackMinScore = GetInt(70, "FALLBACK_MIN_SCORE", "NEWS_FALLBACK_MIN_SCORE");
        var breakingScore = GetInt("NEWS_BREAKING_SCORE", 90);
        var dailyLimit = GetInt("NEWS_DAILY_POST_LIMIT", 8);
        var breakingExtraLimit = GetInt("NEWS_BREAKING_EXTRA_DAILY_LIMIT", 3);
        var maxCandidates = maxCandidatesOverride ?? GetInt("NEWS_MAX_CANDIDATES_PER_RUN", 20);
        var maxPostsPerRun = maxPostsOverride ?? GetInt(3, "MAX_TWEETS_PER_RUN", "NEWS_MAX_POSTS_PER_RUN");
        var maxArticleAgeHours = GetInt("NEWS_MAX_ARTICLE_AGE_HOURS", 48);

        var sources = NewsFeedOptions.FromEnvironment();
        var articles = await _rssFeedService.GetNewsArticlesAsync(sources);
        var now = DateTimeOffset.UtcNow;
        var dateKey = now.ToString("yyyy-MM-dd");
        var state = await LoadStateAsync(dateKey);

        var postedHashes = state.PostedArticleHashes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var evaluatedHashes = state.EvaluatedArticleHashes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = articles
            .Where(article => now - article.PublishedAt <= TimeSpan.FromHours(maxArticleAgeHours))
            .Where(article =>
            {
                var hash = NewsIdentityService.BuildStableHash(article);
                return !postedHashes.Contains(hash) && !evaluatedHashes.Contains(hash);
            })
            .OrderByDescending(article => article.PublishedAt)
            .Take(maxCandidates)
            .ToList();
        var keywordFilteredCandidates = candidates
            .Where(LocalRuleNewsScorer.ShouldConsider)
            .ToList();
        var localScoredPlans = keywordFilteredCandidates
            .Select(article => new NewsTweetPlan
            {
                Article = article,
                Tweet = string.Empty,
                Score = LocalRuleNewsScorer.Score(article, now)
            })
            .OrderByDescending(plan => plan.Score.Total)
            .ThenByDescending(plan => plan.Article.PublishedAt)
            .ToList();
        var localScorePassedPlans = localScoredPlans
            .Where(plan => plan.Score.Total >= localScoreMin)
            .Take(openAiMaxCandidates)
            .ToList();

        _logger.LogInformation("Fetched {FetchedCount} article(s); {CandidateCount} new candidate(s); {KeywordCount} after keyword prefilter; {LocalScoreCount} after local scoring",
            articles.Count, candidates.Count, keywordFilteredCandidates.Count, localScorePassedPlans.Count);
        output.AppendLine($"Fetched articles: {articles.Count}");
        output.AppendLine($"New candidates after filters: {candidates.Count}");
        output.AppendLine($"Keyword prefilter candidates: {keywordFilteredCandidates.Count}");
        output.AppendLine($"Local rule score candidates: {localScorePassedPlans.Count}");
        output.AppendLine($"DRY_RUN: {dryRun}");
        output.AppendLine($"OpenAI key configured: {!string.IsNullOrWhiteSpace(openAiKey)}");
        output.AppendLine($"OpenAI key prefix: {MaskKey(openAiKey)}");
        output.AppendLine($"OpenAI model: {openAiModel}");
        output.AppendLine($"OpenAI base URL: {openAiBaseUrl}");
        output.AppendLine($"TELEGRAM_ENABLED: {telegramEnabled}");
        output.AppendLine($"TELEGRAM_REVIEW_MODE: {telegramReviewMode}");
        output.AppendLine($"Telegram configured: {telegramConfigured}");
        output.AppendLine($"Telegram bot username: {_telegramNotificationService.BotUsername}");
        output.AppendLine($"X_API_ENABLED: {xApiEnabled}");
        output.AppendLine($"MANUAL_X_POST_MODE: {manualXPostMode}");
        output.AppendLine($"GENERATE_X_INTENT_LINK: {generateXIntentLink}");
        output.AppendLine($"Live X publish enabled: {livePublishEnabled}");
        output.AppendLine($"USE_OPENAI_FOR_SCORING: {useOpenAiForScoring}");
        output.AppendLine($"USE_OPENAI_FOR_TWEET: {useOpenAiForTweet}");
        output.AppendLine($"OPENAI_MAX_RETRIES: {openAiMaxRetries}");
        output.AppendLine($"OPENAI_RETRY_ON_OVERLOAD: {openAiRetryOnOverload}");
        output.AppendLine($"OPENAI_STOP_ON_SERVICE_UNAVAILABLE: {openAiStopOnServiceUnavailable}");
        output.AppendLine($"LOCAL_SCORE_MIN: {localScoreMin}");
        output.AppendLine($"OPENAI_MAX_CANDIDATES: {openAiMaxCandidates}");
        output.AppendLine($"Minimum score: {minimumScore}");
        output.AppendLine($"MIN_AI_IMPACT_SCORE: {minAiImpactScore}");
        output.AppendLine($"Fallback top N: {fallbackTopN}");
        output.AppendLine($"Fallback minimum score: {fallbackMinScore}");
        output.AppendLine($"Max tweets per run: {maxPostsPerRun}");
        output.AppendLine($"Max candidates this run: {maxCandidates}");
        AppendContentMix(output, articles);

        _logger.LogInformation(
            "NewsAutoTweet config: DRY_RUN={DryRun}, OpenAI key configured={OpenAiConfigured}, OpenAI key prefix={OpenAiKeyPrefix}, model={OpenAiModel}, baseUrl={OpenAiBaseUrl}, useOpenAiForScoring={UseOpenAiForScoring}, useOpenAiForTweet={UseOpenAiForTweet}, openAiMaxRetries={OpenAiMaxRetries}, retryOnOverload={RetryOnOverload}, stopOnServiceUnavailable={StopOnServiceUnavailable}, localScoreMin={LocalScoreMin}, openAiMaxCandidates={OpenAiMaxCandidates}, minScore={MinimumScore}",
            dryRun,
            !string.IsNullOrWhiteSpace(openAiKey),
            MaskKey(openAiKey),
            openAiModel,
            openAiBaseUrl,
            useOpenAiForScoring,
            useOpenAiForTweet,
            openAiMaxRetries,
            openAiRetryOnOverload,
            openAiStopOnServiceUnavailable,
            localScoreMin,
            openAiMaxCandidates,
            minimumScore);

        foreach (var plan in localScoredPlans)
        {
            evaluatedHashes.Add(NewsIdentityService.BuildStableHash(plan.Article));
            _logger.LogInformation(
                "LocalRuleScore {Score}/100 [{Category}] {Title}. Components: T={Timeliness}, Impact={Impact}, Controversy={Controversy}, Global={Global}, Virality={Virality}. Breaking={Breaking}. Rationale={Rationale}",
                plan.Score.Total,
                plan.Article.Category,
                plan.Article.Title,
                plan.Score.Timeliness,
                plan.Score.InvestmentImpact,
                plan.Score.Controversy,
                plan.Score.GlobalAttention,
                plan.Score.Virality,
                plan.Score.IsBreaking,
                plan.Score.Rationale);
        }

        var qualified = localScorePassedPlans
            .Where(plan => plan.Score.Total >= minimumScore)
            .OrderByDescending(plan => plan.Score.Total)
            .ThenByDescending(plan => plan.Article.PublishedAt)
            .ToList();
        var fallbackSelected = new List<NewsTweetPlan>();

        if (qualified.Count == 0 && localScorePassedPlans.Count > 0)
        {
            fallbackSelected = localScorePassedPlans
                .Where(plan => plan.Score.Total >= fallbackMinScore)
                .OrderByDescending(plan => plan.Score.Total)
                .ThenByDescending(plan => plan.Article.PublishedAt)
                .Take(fallbackTopN)
                .ToList();
        }

        var selectedPlans = qualified.Count > 0 ? qualified : fallbackSelected;

        _logger.LogInformation("{QualifiedCount} article(s) passed score threshold {MinimumScore}", qualified.Count, minimumScore);
        _logger.LogInformation("{FallbackCount} fallback article(s) selected with minimum score {FallbackMinScore}", fallbackSelected.Count, fallbackMinScore);
        output.AppendLine($"Passed score threshold: {qualified.Count}");
        output.AppendLine($"Fallback candidates selected: {fallbackSelected.Count}");

        if (localScoredPlans.Count > 0)
        {
            output.AppendLine();
            output.AppendLine("Local scored candidates:");
            foreach (var plan in localScoredPlans
                .OrderByDescending(plan => plan.Score.Total)
                .ThenByDescending(plan => plan.Article.PublishedAt))
            {
                var status = plan.Score.Total >= minimumScore
                    ? "PASS"
                    : fallbackSelected.Contains(plan)
                        ? "FALLBACK"
                        : "BELOW_THRESHOLD";
                output.AppendLine(
                    $"- [{status}] {plan.Score.Total}/100 | {plan.Article.Category} | {plan.Article.SourceName} | {plan.Article.Title}");
                output.AppendLine(
                    $"  components: timeliness={plan.Score.Timeliness}, investmentImpact={plan.Score.InvestmentImpact}, controversy={plan.Score.Controversy}, globalAttention={plan.Score.GlobalAttention}, virality={plan.Score.Virality}, breaking={plan.Score.IsBreaking}");
                if (!string.IsNullOrWhiteSpace(plan.Score.Rationale))
                {
                    output.AppendLine($"  rationale: {plan.Score.Rationale}");
                }
            }
        }

        output.AppendLine();
        output.AppendLine("Telegram review summary:");
        output.AppendLine(telegramEnabled
            ? "Telegram integration enabled; notification will be sent if bot token and chat id are configured."
            : "Telegram integration disabled; summary is printed for review.");
        output.AppendLine($"Top local-score news: {localScorePassedPlans.Count}");
        foreach (var plan in localScorePassedPlans.Take(3))
        {
            output.AppendLine($"- {plan.Score.Total}/100 | {plan.Article.Category} | {plan.Article.SourceName} | {plan.Article.Title}");
        }
        output.AppendLine($"Will call OpenAI: {useOpenAiForScoring || useOpenAiForTweet}");
        output.AppendLine($"Manual review mode: {dryRun}");

        var postedThisRun = 0;
        var generatedTweets = new List<string>();
        var generatedPlans = new List<NewsTweetPlan>();
        var pendingPosts = new List<PendingNewsXPost>();
        var publishFailureCount = 0;
        var skippedInvalidTweetCount = 0;
        var tweetTextsThisRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var openAiCallCount = 0;
        var openAiSuccessfulCallCount = 0;
        var openAiFailedCallCount = 0;
        var openAiAttemptedCandidateCount = 0;
        var openAiFailureReason = string.Empty;
        var openAiStoppedReason = string.Empty;
        var skippedCandidatesDueToOpenAiFailure = 0;

        for (var selectedIndex = 0; selectedIndex < selectedPlans.Count; selectedIndex++)
        {
            var plan = selectedPlans[selectedIndex];
            var articleHash = NewsIdentityService.BuildStableHash(plan.Article);

            if (postedThisRun >= maxPostsPerRun)
            {
                break;
            }

            var isBreakingExtra = state.StandardPostsToday >= dailyLimit
                && plan.Score.Total >= breakingScore
                && plan.Score.IsBreaking
                && state.BreakingExtraPostsToday < breakingExtraLimit;

            if (state.StandardPostsToday >= dailyLimit && !isBreakingExtra)
            {
                _logger.LogInformation("Daily post limit reached ({DailyLimit}); skipping {Title}", dailyLimit, plan.Article.Title);
                continue;
            }

            NewsTweetPlan? publishPlan;
            if (useOpenAiForScoring)
            {
                openAiAttemptedCandidateCount++;
                publishPlan = await _tweetService.CreateTweetPlanAsync(plan.Article);
                openAiCallCount++;
                if (publishPlan.Score.Rationale.Contains("Fallback scoring used", StringComparison.OrdinalIgnoreCase)
                    && publishPlan.Score.Rationale.Contains("OpenAI", StringComparison.OrdinalIgnoreCase))
                {
                    openAiFailureReason = publishPlan.Score.Rationale;
                    publishPlan = null;
                }
            }
            else
            {
                var generation = await _tweetService.CreateTweetFromLocalScoreAsync(plan.Article, plan.Score, useOpenAiForTweet);
                openAiCallCount += generation.OpenAiCallCount;
                openAiSuccessfulCallCount += generation.OpenAiSuccessfulCallCount;
                openAiFailedCallCount += generation.OpenAiFailedCallCount;
                if (generation.OpenAiAttempted)
                {
                    openAiAttemptedCandidateCount++;
                }

                if (!string.IsNullOrWhiteSpace(generation.FailureReason))
                {
                    openAiFailureReason = generation.FailureReason;
                    output.AppendLine($"OpenAI failure: {generation.FailureReason}");
                }

                if (generation.StopOpenAiForRun)
                {
                    openAiStoppedReason = generation.StopReason ?? generation.FailureReason ?? "OpenAI stopped";
                    skippedCandidatesDueToOpenAiFailure = Math.Max(0, selectedPlans.Count - selectedIndex - 1);
                    _logger.LogWarning(
                        "Stopping OpenAI calls for this run at candidate {CandidateIndex}/{CandidateTotal}: {Title}. Reason: {Reason}. Skipping {SkippedCount} remaining candidate(s).",
                        selectedIndex + 1,
                        selectedPlans.Count,
                        plan.Article.Title,
                        openAiStoppedReason,
                        skippedCandidatesDueToOpenAiFailure);
                    output.AppendLine($"OpenAI stopped reason: {openAiStoppedReason}");
                    output.AppendLine($"OpenAI failed candidate: {plan.Article.Title}");
                    output.AppendLine($"Skipped candidates due to OpenAI failure: {skippedCandidatesDueToOpenAiFailure}");
                    break;
                }

                publishPlan = generation.Plan;
            }

            if (publishPlan == null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(publishPlan.Article.Link))
            {
                skippedInvalidTweetCount++;
                var failedPostId = NewsPendingPostStore.BuildId(publishPlan.Article);
                pendingPosts.Add(new PendingNewsXPost
                {
                    Id = failedPostId,
                    Title = publishPlan.Article.Title,
                    ShortHeadline = publishPlan.MarketAnalysis.ShortHeadline,
                    Source = publishPlan.Article.SourceName,
                    SourceUrl = string.Empty,
                    LocalScore = publishPlan.Score.Total,
                    Summary = publishPlan.MarketAnalysis.Summary,
                    MarketImpact = publishPlan.MarketAnalysis.MarketImpact,
                    MarketImpactScore = publishPlan.MarketAnalysis.MarketImpactScore,
                    AffectedAssets = publishPlan.MarketAnalysis.AffectedAssets,
                    Winners = publishPlan.MarketAnalysis.Winners,
                    Losers = publishPlan.MarketAnalysis.Losers,
                    TradeTake = publishPlan.MarketAnalysis.TradeTake,
                    ConfidenceScore = publishPlan.MarketAnalysis.ConfidenceScore,
                    AiAnalysis = publishPlan.MarketAnalysis,
                    FinalXPost = string.Empty,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Status = PendingNewsXPostStatus.Failed,
                    ErrorMessage = "source_url missing",
                    FailureReason = "source_url missing"
                });
                _logger.LogWarning("Skipping and marking failed because source_url is missing for {Title}", publishPlan.Article.Title);
                continue;
            }

            if (!IsTweetPublishable(publishPlan.Tweet, publishPlan.Article.Link, tweetTextsThisRun))
            {
                skippedInvalidTweetCount++;
                var invalidReason = BuildInvalidTweetReason(publishPlan.Tweet, publishPlan.Article.Link, tweetTextsThisRun);
                _logger.LogWarning("Skipping invalid or duplicate tweet for {Title}. Reason={Reason}. HasLink={HasLink}, Empty={Empty}",
                    publishPlan.Article.Title,
                    invalidReason,
                    !string.IsNullOrWhiteSpace(publishPlan.Article.Link) && publishPlan.Tweet.Contains(publishPlan.Article.Link, StringComparison.OrdinalIgnoreCase),
                    string.IsNullOrWhiteSpace(publishPlan.Tweet));
                output.AppendLine($"Skipped invalid tweet: {publishPlan.Article.Title}");
                output.AppendLine($"Invalid reason: {invalidReason}");
                continue;
            }

            var finalXPost = string.IsNullOrWhiteSpace(publishPlan.FinalXPost)
                ? publishPlan.Tweet
                : publishPlan.FinalXPost;
            publishPlan = publishPlan with
            {
                FinalXPost = finalXPost,
                Tweet = finalXPost
            };

            var image = await _tweetImageService.PrepareImageAsync(publishPlan.Article);
            var xDraftIntentUrl = generateXIntentLink
                ? BuildXWebIntentUrl(finalXPost, publishPlan.Article.Link)
                : null;

            publishPlan = publishPlan with
            {
                ImagePathOrUrl = image.PathOrUrl,
                ImageSource = image.Source,
                ImageUrl = image.ImageUrl,
                ImageStatus = image.Status,
                XDraftIntentUrl = xDraftIntentUrl
            };

            var pendingPostId = NewsPendingPostStore.BuildId(publishPlan.Article);
            publishPlan = publishPlan with
            {
                PendingPostId = pendingPostId,
                FinalXPost = finalXPost,
                Tweet = finalXPost
            };

            pendingPosts.Add(new PendingNewsXPost
            {
                Id = pendingPostId,
                Title = publishPlan.Article.Title,
                ShortHeadline = publishPlan.MarketAnalysis.ShortHeadline,
                Source = publishPlan.Article.SourceName,
                SourceUrl = publishPlan.Article.Link,
                ImageUrl = publishPlan.ImageUrl,
                LocalImagePath = publishPlan.ImagePathOrUrl,
                LocalScore = publishPlan.Score.Total,
                Summary = publishPlan.MarketAnalysis.Summary,
                MarketImpact = publishPlan.MarketAnalysis.MarketImpact,
                MarketImpactScore = publishPlan.MarketAnalysis.MarketImpactScore,
                AffectedAssets = publishPlan.MarketAnalysis.AffectedAssets,
                Winners = publishPlan.MarketAnalysis.Winners,
                Losers = publishPlan.MarketAnalysis.Losers,
                TradeTake = publishPlan.MarketAnalysis.TradeTake,
                ConfidenceScore = publishPlan.MarketAnalysis.ConfidenceScore,
                AiAnalysis = publishPlan.MarketAnalysis,
                FinalXPost = finalXPost,
                CreatedAt = DateTimeOffset.UtcNow,
                Status = PendingNewsXPostStatus.Pending
            });

            generatedTweets.Add(publishPlan.Tweet);
            generatedPlans.Add(publishPlan);
            tweetTextsThisRun.Add(publishPlan.Tweet);
            output.AppendLine();
            output.AppendLine($"[{publishPlan.Score.Total}/100] {publishPlan.Article.Category} | {publishPlan.Article.SourceName}");
            output.AppendLine(publishPlan.Article.Title);
            output.AppendLine($"English Tweet Body: {publishPlan.EnglishTweetBody}");
            output.AppendLine($"Chinese Brief: {publishPlan.ChineseBrief}");
            output.AppendLine($"Short Headline: {publishPlan.MarketAnalysis.ShortHeadline}");
            output.AppendLine($"Original URL: {publishPlan.Article.Link}");
            output.AppendLine($"Image: {publishPlan.ImageSource} | {publishPlan.ImageStatus}");
            output.AppendLine($"Open X Draft: {publishPlan.XDraftIntentUrl ?? "disabled"}");
            if (publishPlan.IsTemplateTweet)
            {
                output.AppendLine("template tweet");
            }
            output.AppendLine(publishPlan.Tweet);

            bool success;
            if (!livePublishEnabled)
            {
                success = true;
                _logger.LogInformation("{Mode} tweet preview ({Length} chars, image={ImageSource}, imageStatus={ImageStatus}):\n{Tweet}",
                    manualXPostMode ? "MANUAL_X_POST_MODE" : dryRun ? "DRY_RUN" : "TELEGRAM_REVIEW",
                    publishPlan.Tweet.Length,
                    publishPlan.ImageSource,
                    publishPlan.ImageStatus,
                    publishPlan.Tweet);
            }
            else
            {
                if (publishPlan.IsTemplateTweet)
                {
                    _logger.LogWarning("Skipping real publish for template tweet: {Title}", publishPlan.Article.Title);
                    success = false;
                }
                else
                {
                    var media = string.IsNullOrWhiteSpace(publishPlan.ImagePathOrUrl)
                        ? null
                        : new[] { publishPlan.ImagePathOrUrl };
                    success = await _twitterApiClient.PostTweetAsync(publishPlan.Tweet, media);
                }
            }

            if (success)
            {
                postedThisRun++;
                if (livePublishEnabled)
                {
                    postedHashes.Add(articleHash);

                    if (!dryRun)
                    {
                        if (isBreakingExtra)
                        {
                            state = state with { BreakingExtraPostsToday = state.BreakingExtraPostsToday + 1 };
                        }
                        else
                        {
                            state = state with { StandardPostsToday = state.StandardPostsToday + 1 };
                        }
                    }
                }

                _logger.LogInformation("{Mode} succeeded for {Title}. ImageSource={ImageSource}, ImageStatus={ImageStatus}",
                    livePublishEnabled ? "Publish" : manualXPostMode ? "MANUAL_X_POST_MODE" : dryRun ? "DRY_RUN" : "TELEGRAM_REVIEW",
                    publishPlan.Article.Title,
                    publishPlan.ImageSource,
                    publishPlan.ImageStatus);
            }
            else
            {
                publishFailureCount++;
                evaluatedHashes.Remove(articleHash);
                _logger.LogWarning("Publish failed for {Title}", publishPlan.Article.Title);
            }
        }

        _logger.LogInformation(
            "Run summary: fetched={FetchedCount}, candidates={CandidateCount}, keywordFiltered={KeywordFilteredCount}, localScored={LocalScoredCount}, openAiCandidates={OpenAiCandidates}, openAiCalls={OpenAiCalls}, skippedOpenAiScoring={SkippedOpenAiScoring}, estimatedSavedCalls={EstimatedSavedCalls}, qualified={QualifiedCount}, fallback={FallbackCount}, generated={GeneratedCount}, postedOrSimulated={PostedCount}, dryRun={DryRun}",
            articles.Count,
            candidates.Count,
            keywordFilteredCandidates.Count,
            localScorePassedPlans.Count,
            openAiAttemptedCandidateCount,
            openAiCallCount,
            !useOpenAiForScoring,
            Math.Max(0, candidates.Count - openAiCallCount),
            qualified.Count,
            fallbackSelected.Count,
            generatedTweets.Count,
            postedThisRun,
            dryRun);

        output.AppendLine();
        output.AppendLine("Run summary:");
        output.AppendLine($"RSS total: {articles.Count}");
        output.AppendLine($"Keyword prefilter count: {keywordFilteredCandidates.Count}");
        output.AppendLine($"Local rule score count: {localScorePassedPlans.Count}");
        output.AppendLine($"Actual OpenAI candidate count: {openAiAttemptedCandidateCount}");
        output.AppendLine($"Skipped OpenAI scoring: {!useOpenAiForScoring}");
        output.AppendLine($"Estimated saved OpenAI calls: {Math.Max(0, candidates.Count - openAiCallCount)}");
        output.AppendLine($"OpenAI attempted calls: {openAiCallCount}");
        output.AppendLine($"OpenAI successful calls: {openAiSuccessfulCallCount}");
        output.AppendLine($"OpenAI failed calls: {openAiFailedCallCount}");
        output.AppendLine($"OpenAI stopped reason: {(string.IsNullOrWhiteSpace(openAiStoppedReason) ? "none" : openAiStoppedReason)}");
        output.AppendLine($"Skipped candidates due to OpenAI failure: {skippedCandidatesDueToOpenAiFailure}");
        output.AppendLine($"Partial success: {generatedPlans.Count > 0 && !string.IsNullOrWhiteSpace(openAiStoppedReason)}");
        output.AppendLine($"Initial candidates: {candidates.Count}");
        output.AppendLine($"Qualified candidates: {qualified.Count}");
        output.AppendLine($"Fallback candidates: {fallbackSelected.Count}");
        output.AppendLine($"Generated tweets: {generatedTweets.Count}");
        output.AppendLine($"Posted/simulated successfully: {postedThisRun}");
        output.AppendLine($"DRY_RUN: {dryRun}");
        output.AppendLine($"Telegram OpenAI calls: {openAiCallCount}");
        output.AppendLine($"Telegram manual review mode: {telegramReviewMode}");
        output.AppendLine($"Manual X post mode: {manualXPostMode}");
        output.AppendLine($"X API bypassed: {!livePublishEnabled}");

        var openAiRunStats = new OpenAiRunStats
        {
            AttemptedCalls = openAiCallCount,
            SuccessfulCalls = openAiSuccessfulCallCount,
            FailedCalls = openAiFailedCallCount,
            StoppedReason = openAiStoppedReason,
            SkippedCandidatesDueToFailure = skippedCandidatesDueToOpenAiFailure,
            PartialSuccess = generatedPlans.Count > 0 && !string.IsNullOrWhiteSpace(openAiStoppedReason)
        };

        await _pendingPostStore.SavePendingAsync(pendingPosts);
        output.AppendLine($"Pending X posts saved: {pendingPosts.Count}");

        var telegramResult = await _telegramNotificationService.SendReviewAsync(
            localScorePassedPlans,
            generatedPlans,
            openAiRunStats,
            !useOpenAiForScoring,
            dryRun,
            openAiFailureReason);
        output.AppendLine($"Telegram notification: {(telegramResult.Sent ? "sent" : "not sent")}");
        output.AppendLine($"Telegram status: {telegramResult.Message}");
        _logger.LogInformation("Telegram notification status: sent={Sent}, message={Message}", telegramResult.Sent, telegramResult.Message);

        if (generatedTweets.Count == 0)
        {
            var reason = !string.IsNullOrWhiteSpace(openAiFailureReason)
                ? $"OpenAI失败: {openAiFailureReason}"
                : BuildNoTweetReason(candidates.Count, localScoredPlans, fallbackMinScore, publishFailureCount, skippedInvalidTweetCount);
            _logger.LogWarning("No tweets generated. Reason: {Reason}", reason);
            output.AppendLine($"No tweets generated reason: {reason}");
        }

        if (!dryRun)
        {
            var compactEvaluated = evaluatedHashes.TakeLast(500).ToList();
            var compactPosted = postedHashes.TakeLast(500).ToList();
            await _stateTrackingService.SetStateAsync(state with
            {
                EvaluatedArticleHashes = compactEvaluated,
                PostedArticleHashes = compactPosted
            }, StateFileName);
        }
        else
        {
            _logger.LogInformation("DRY_RUN enabled; state was not updated.");
        }

        return output.ToString();
    }

    private async Task<NewsAutomationState> LoadStateAsync(string dateKey)
    {
        var state = await _stateTrackingService.GetStateAsync<NewsAutomationState>(StateFileName);
        if (state == null)
        {
            return new NewsAutomationState { DateKey = dateKey };
        }

        if (!string.Equals(state.DateKey, dateKey, StringComparison.OrdinalIgnoreCase))
        {
            return state with
            {
                DateKey = dateKey,
                StandardPostsToday = 0,
                BreakingExtraPostsToday = 0
            };
        }

        return state;
    }

    private static string? GetQueryParameter(HttpRequestData req, string name)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        return query[name];
    }

    private static int? TryGetPositiveInt(string? value)
    {
        if (int.TryParse(value, out var parsed) && parsed > 0)
        {
            return parsed;
        }

        return null;
    }

    private static int GetInt(string name, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    private static int GetInt(int defaultValue, params string[] names)
    {
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (int.TryParse(value, out var parsed))
            {
                return parsed;
            }
        }

        return defaultValue;
    }

    private static bool IsTweetPublishable(string tweet, string link, HashSet<string> tweetTextsThisRun)
    {
        return !string.IsNullOrWhiteSpace(tweet)
            && !string.IsNullOrWhiteSpace(link)
            && OpenAiNewsTweetService.IsSafeEnglishTweet(tweet, link)
            && !tweetTextsThisRun.Contains(tweet);
    }

    private static string BuildInvalidTweetReason(string tweet, string link, HashSet<string> tweetTextsThisRun)
    {
        if (string.IsNullOrWhiteSpace(tweet))
        {
            return "final_x_post empty after safety compression";
        }

        if (string.IsNullOrWhiteSpace(link))
        {
            return "source_url missing";
        }

        if (tweetTextsThisRun.Contains(tweet))
        {
            return "duplicate final_x_post in this run";
        }

        var weightedLength = XPostLengthHelper.GetWeightedLength(tweet);
        if (weightedLength > 280)
        {
            return $"tweet too long ({weightedLength}/280 X-weighted chars)";
        }

        if (!OpenAiNewsTweetService.FinalQualityGate(tweet, link, string.Empty, out var gateReason))
        {
            return gateReason;
        }

        return "failed final_x_post safety checks";
    }

    private static string BuildXWebIntentUrl(string tweetText, string articleUrl)
    {
        var finalText = tweetText.Trim();
        return $"https://twitter.com/intent/tweet?text={WebUtility.UrlEncode(finalText)}";
    }

    private static void AppendContentMix(StringBuilder output, IReadOnlyList<NewsArticle> articles)
    {
        var macro = 0;
        var ai = 0;
        var defi = 0;
        var web3 = 0;
        var meme = 0;

        foreach (var article in articles)
        {
            var bucket = ClassifyContentBucket(article);
            switch (bucket)
            {
                case "Macro":
                    macro++;
                    break;
                case "AI":
                    ai++;
                    break;
                case "DeFi":
                    defi++;
                    break;
                case "Web3":
                    web3++;
                    break;
                case "Meme":
                    meme++;
                    break;
            }
        }

        output.AppendLine();
        output.AppendLine("Today’s Content Mix:");
        output.AppendLine($"Macro: {macro}");
        output.AppendLine($"AI: {ai}");
        output.AppendLine($"DeFi: {defi}");
        output.AppendLine($"Web3: {web3}");
        output.AppendLine($"Meme: {meme}");
        output.AppendLine("Suggested mix: Macro/ETF/Regulation 30%, AI 25%, DeFi 20%, Web3 15%, Meme 10%");
    }

    private static string ClassifyContentBucket(NewsArticle article)
    {
        var text = $"{article.Title} {article.Summary} {article.Category} {article.SourceName}";
        if (article.Category.Contains("Web3", StringComparison.OrdinalIgnoreCase) || Web3NewsClassifier.IsWeb3(text))
        {
            return "Web3";
        }

        if (ContainsAny(text, "defi", "decentralized finance", "dex", "lending protocol", "aave", "uniswap", "curve", "makerdao"))
        {
            return "DeFi";
        }

        if (ContainsAny(text, "meme", "memecoin", "dogecoin", "shiba", "pepe", "bonk", "wif"))
        {
            return "Meme";
        }

        if (ContainsAny(text, "openai", "anthropic", "ai", "artificial intelligence", "nvidia", "chip", "semiconductor"))
        {
            return "AI";
        }

        if (ContainsAny(text, "macro", "etf", "sec", "fed", "cpi", "regulation", "policy", "treasury", "inflation", "rates"))
        {
            return "Macro";
        }

        return "Other";
    }

    private static bool ContainsAny(string text, params string[] needles)
        => needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static string BuildNoTweetReason(
        int candidateCount,
        IReadOnlyList<NewsTweetPlan> plans,
        int fallbackMinScore,
        int publishFailureCount,
        int skippedInvalidTweetCount)
    {
        if (candidateCount == 0)
        {
            return "无候选";
        }

        if (plans.Count > 0 && plans.Max(plan => plan.Score.Total) < fallbackMinScore)
        {
            return $"分数低于{fallbackMinScore}";
        }

        if (plans.Count > 0 && plans.All(plan => plan.Score.Rationale.Contains("OpenAI", StringComparison.OrdinalIgnoreCase)
            && plan.Score.Rationale.Contains("failed", StringComparison.OrdinalIgnoreCase)))
        {
            return "OpenAI失败";
        }

        if (publishFailureCount > 0)
        {
            return "X API失败";
        }

        if (skippedInvalidTweetCount > 0)
        {
            return "推文为空、缺少链接或重复";
        }

        return "无候选";
    }

    private static bool IsEnabled(string name, bool defaultValue = false)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return bool.TryParse(value, out var enabled) ? enabled : defaultValue;
    }

    private static object BuildDiagnostics()
    {
        var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
        return new
        {
            hasOpenAIKey = !string.IsNullOrWhiteSpace(openAiKey),
            openAIKeyPrefix = GetKeyPrefix(openAiKey),
            model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o-mini",
            dryRun = IsEnabled("DRY_RUN", defaultValue: true),
            xApiEnabled = IsEnabled("X_API_ENABLED", defaultValue: true),
            manualXPostMode = IsEnabled("MANUAL_X_POST_MODE", defaultValue: false),
            generateXIntentLink = IsEnabled("GENERATE_X_INTENT_LINK", defaultValue: true)
        };
    }

    private static string MaskKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "(empty)";
        }

        var trimmed = key.Trim();
        if (trimmed.Length <= 10)
        {
            return $"{trimmed[..Math.Min(4, trimmed.Length)]}...";
        }

        return $"{trimmed[..7]}...{trimmed[^4..]} ({trimmed.Length} chars)";
    }

    private static string GetKeyPrefix(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        var trimmed = key.Trim();
        return trimmed[..Math.Min(8, trimmed.Length)];
    }
}
