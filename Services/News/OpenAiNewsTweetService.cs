using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Services;

public sealed partial class OpenAiNewsTweetService
{
    private const int MaxTweetLength = 280;
    private readonly ILogger<OpenAiNewsTweetService> _logger;
    private readonly HttpClient _httpClient;

    public OpenAiNewsTweetService(
        ILogger<OpenAiNewsTweetService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));

    public async Task<NewsTweetGenerationResult> CreateTweetFromLocalScoreAsync(
        NewsArticle article,
        NewsScore score,
        bool useOpenAiForTweet,
        CancellationToken cancellationToken = default)
    {
        if (!useOpenAiForTweet)
        {
            return new NewsTweetGenerationResult
            {
                Plan = CreateTemplatePlan(article, score, isTemplateTweet: true),
                OpenAiAttempted = false,
                OpenAiCallCount = 0
            };
        }

        if (!IsConfigured)
        {
            return new NewsTweetGenerationResult
            {
                OpenAiAttempted = false,
                OpenAiCallCount = 0,
                StopOpenAiForRun = true,
                FailureReason = "OPENAI_API_KEY missing",
                StopReason = "OPENAI_API_KEY missing"
            };
        }

        var callCount = 0;
        var successCount = 0;
        var failedCount = 0;
        var maxRetries = GetInt("OPENAI_MAX_RETRIES", 1);
        var maxLanguageRetries = Math.Clamp(maxRetries, 0, 1);
        var maxAttempts = 1 + maxLanguageRetries;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            callCount++;
            try
            {
                var response = await SendOpenAiRequestAsync(article, includeScoring: false, cancellationToken);
                successCount += response.OpenAiSuccessfulCallCount;
                failedCount += response.OpenAiFailedCallCount;

                if (response.StopOpenAiForRun)
                {
                    return response with
                    {
                        OpenAiCallCount = callCount,
                        OpenAiSuccessfulCallCount = successCount,
                        OpenAiFailedCallCount = failedCount
                    };
                }

                if (response.RetryableLanguageFailure && attempt < maxAttempts)
                {
                    _logger.LogWarning(
                        "OpenAI language validation failed for {Title}; retrying once. Reason: {Reason}",
                        article.Title,
                        response.FailureReason);
                    continue;
                }

                if (response.FailureReason != null)
                {
                    return response with
                    {
                        OpenAiCallCount = callCount,
                        OpenAiSuccessfulCallCount = successCount,
                        OpenAiFailedCallCount = failedCount
                    };
                }

                if (response.Plan != null)
                {
                    return response with
                    {
                        Plan = response.Plan with { Score = score },
                        OpenAiCallCount = callCount,
                        OpenAiSuccessfulCallCount = successCount,
                        OpenAiFailedCallCount = failedCount
                    };
                }
            }
            catch (Exception ex)
            {
                failedCount++;
                _logger.LogError(ex, "OpenAI tweet generation failed for {Title}", article.Title);
                var stopReason = GetOpenAiStopReason(ex.Message);
                return new NewsTweetGenerationResult
                {
                    OpenAiAttempted = true,
                    OpenAiCallCount = callCount,
                    OpenAiSuccessfulCallCount = successCount,
                    OpenAiFailedCallCount = failedCount,
                    StopOpenAiForRun = stopReason != null,
                    FailureReason = ex.Message,
                    StopReason = stopReason
                };
            }
        }

        return new NewsTweetGenerationResult
        {
            OpenAiAttempted = true,
            OpenAiCallCount = callCount,
            OpenAiSuccessfulCallCount = successCount,
            OpenAiFailedCallCount = failedCount,
            FailureReason = "OpenAI language validation failed after retry"
        };
    }

    public async Task<NewsTweetPlan> CreateTweetPlanAsync(NewsArticle article, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("OPENAI_API_KEY is not configured. Using deterministic fallback for {Title}", article.Title);
            return CreateFallbackPlan(article, "OPENAI_API_KEY missing");
        }

        try
        {
            var result = await SendOpenAiRequestAsync(article, includeScoring: true, cancellationToken);
            return result.Plan ?? CreateFallbackPlan(article, result.FailureReason ?? "OpenAI failed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenAI tweet generation failed for {Title}", article.Title);
            return CreateFallbackPlan(article, ex.Message);
        }
    }

    private async Task<NewsTweetGenerationResult> SendOpenAiRequestAsync(
        NewsArticle article,
        bool includeScoring,
        CancellationToken cancellationToken)
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")!.Trim();
        var model = Environment.GetEnvironmentVariable("OPENAI_MODEL")?.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            model = "gpt-4o-mini";
        }

        var baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL")?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = "https://api.openai.com/v1";
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(new OpenAiChatRequest
        {
            Model = model,
            Temperature = 0.2,
            ResponseFormat = new OpenAiResponseFormat { Type = "json_object" },
            Messages =
            [
                new OpenAiChatMessage("system", includeScoring ? BuildSystemPrompt() : BuildTweetOnlySystemPrompt()),
                new OpenAiChatMessage("user", BuildUserPrompt(article))
            ]
        });

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var reason = $"OpenAI request failed: {response.StatusCode}. {Truncate(CleanText(payload), 500)}";
            _logger.LogWarning("OpenAI request failed. Status={StatusCode}, Response={Response}", response.StatusCode, payload);
            return new NewsTweetGenerationResult
            {
                OpenAiAttempted = true,
                OpenAiCallCount = 1,
                OpenAiFailedCallCount = 1,
                StopOpenAiForRun = ShouldStopOpenAiForRun(payload, response.StatusCode.ToString()),
                FailureReason = reason,
                StopReason = GetOpenAiStopReason(payload) ?? GetOpenAiStopReason(response.StatusCode.ToString())
            };
        }

        var chat = JsonSerializer.Deserialize<OpenAiChatResponse>(payload);
        var json = chat?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(json))
        {
            return new NewsTweetGenerationResult
            {
                OpenAiAttempted = true,
                OpenAiCallCount = 1,
                OpenAiFailedCallCount = 1,
                FailureReason = $"OpenAI returned empty content. Raw response: {Truncate(CleanText(payload), 500)}"
            };
        }

        GeneratedNewsTweet? generated;
        try
        {
            generated = JsonSerializer.Deserialize<GeneratedNewsTweet>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "OpenAI returned invalid JSON for {Title}: {OpenAiContent}", article.Title, json);
            return new NewsTweetGenerationResult
            {
                OpenAiAttempted = true,
                OpenAiCallCount = 1,
                OpenAiFailedCallCount = 1,
                FailureReason = $"OpenAI returned invalid JSON: {ex.Message}. Content: {Truncate(CleanText(json), 500)}"
            };
        }

        if (generated == null || string.IsNullOrWhiteSpace(generated.EnglishTweetBody))
        {
            return new NewsTweetGenerationResult
            {
                OpenAiAttempted = true,
                OpenAiCallCount = 1,
                OpenAiFailedCallCount = 1,
                FailureReason = $"OpenAI returned JSON without tweet. Content: {Truncate(CleanText(json), 500)}"
            };
        }

        var bodyMax = GetInt("ENGLISH_TWEET_BODY_MAX_CHARS", 240);
        var briefMax = GetInt("CHINESE_BRIEF_MAX_CHARS", 160);
        if (ContainsCjk(generated.EnglishTweetBody))
        {
            return new NewsTweetGenerationResult
            {
                OpenAiAttempted = true,
                OpenAiCallCount = 1,
                OpenAiFailedCallCount = 1,
                RetryableLanguageFailure = true,
                FailureReason = $"OpenAI returned non-English characters in englishTweetBody. Content: {Truncate(CleanText(generated.EnglishTweetBody), 300)}"
            };
        }

        var englishBody = SanitizeEnglishBody(generated.EnglishTweetBody, bodyMax);
        if (string.IsNullOrWhiteSpace(englishBody))
        {
            return new NewsTweetGenerationResult
            {
                OpenAiAttempted = true,
                OpenAiCallCount = 1,
                OpenAiFailedCallCount = 1,
                RetryableLanguageFailure = true,
                FailureReason = "OpenAI englishTweetBody became empty after safety cleanup"
            };
        }

        var chineseBrief = TruncateAtWord(CleanText(generated.ChineseBrief ?? string.Empty), briefMax);
        var marketAnalysis = BuildMarketAnalysis(generated, article);
        var tweet = BuildFinalXPost(article.Title, englishBody, marketAnalysis, article.Link, article.Category);
        var score = includeScoring ? BuildOpenAiScore(generated) : new NewsScore
        {
            Total = 0,
            Rationale = "OpenAI tweet generated; local score retained by caller"
        };

        return new NewsTweetGenerationResult
        {
            Plan = new NewsTweetPlan
            {
                Article = article,
                Tweet = tweet,
                EnglishTweetBody = englishBody,
                ChineseBrief = chineseBrief,
                MarketAnalysis = marketAnalysis,
                FinalXPost = tweet,
                Score = score
            },
            OpenAiAttempted = true,
            OpenAiCallCount = 1,
            OpenAiSuccessfulCallCount = 1
        };
    }

    private static string BuildSystemPrompt() =>
        """
        You write English X posts for a Global Markets Intelligence account covering AI, crypto, DeFi, meme coins, stocks, macro, politics, China, world events, and major sports stories.
        Return only valid JSON.
        Do not invent facts beyond the provided title, summary, source, timestamp, category, and link.
        Do not include, translate, alter, shorten, or rewrite the URL.
        Do not give direct investment advice or tell readers to buy, sell, hold, short, or enter trades.
        You are not a news summarizer. Write like a native-English investor, Web3 KOL, macro observer, and human market editor with a large X audience.
        Learn the energy of Cobie, Mario Nawfal, The Kobeissi Letter, Autism Capital, Alex Kruger, Ryan Selkis, The Defiant, Milk Road, unusual_whales, Wall Street Silver, and Watcher Guru without copying catchphrases.
        News is 30%; analysis, second-order effects, controversy, and point of view are 70%.
        Prefer human framing: "Most people are looking at the headline.", "They're missing the real story.", "The market may be pricing this completely wrong.", "The headline is obvious. The downstream consequences are not.", "Would love to hear the bear case here."
        final_x_post must never contain mechanical labels or robot phrasing: Impact:, Score:, Impact Score, AI Score, Confidence Score, AI Confidence, Winners, Losers, Assets:, Affected Assets, Market Impact, Bullish:, Bearish:, Source:, According to the report, This article states.
        final_x_post must not use bullets, numbered lists, or template labels. It should read like a human investor posting a market view.
        Avoid hype words and repeated bot hooks: "This is bullish", "Big news", "Huge", "Breaking", and "Most people miss".
        englishTweetBody is for X publishing. It must be English, no URL, 240 English characters or fewer.
        chineseBrief is for Telegram review only. It must be Simplified Chinese, 160 Chinese characters or fewer, explaining why the story is worth posting.
        Also produce concise market impact analysis for review and X posting.
        short_headline must be newly written by you, max 8 English words, not a direct truncation of the source title.
        summary must be one complete English sentence, 20-35 words when possible.
        market_impact must be exactly Bullish, Bearish, or Neutral.
        market_impact_score rubric: single small altcoin only = 2-4; BTC/ETH/ETF/Fed/CPI/SEC/major crypto policy = 5-8; market-wide macro or major regulation = 8-10.
        If the story mentions BTC, ETH, Bitcoin ETF, Fed rates, CPI, SEC, BlackRock ETF, Coinbase, Binance, or major crypto regulation, market_impact_score should normally be at least 5 unless the news is stale or clearly weak.
        The account sections are AI, Crypto, DeFi, Meme, Web3, Stocks, Macro, Politics, China, World, Sports, and Global Impact.
        affected_assets should use tickers/assets such as BTC, ETH, SOL, XRP, DOGE, PEPE, AAVE, MKR, PENDLE, ENA, UNI, CRV, NVDA, MSFT, AAPL, META, AMZN, TSLA, AMD, AVGO, PLTR, ORCL, SMCI, COIN, MSTR, BABA, BIDU, PDD, QQQ, SPY, IBIT, FBTC, Gold, USD; if none, use ["No direct asset impact"].
        winners and losers should use ["none"] if there is no clear beneficiary or harmed asset/company.
        Actively identify crypto assets, public companies, ETFs, macro assets, and relevant sectors from the story.
        Prefer recognizable ticker/asset names in affected_assets, winners, and losers; code will standardize them into X cashtags.
        trade_take must be one market interpretation sentence, 20-35 words when possible, without direct buy/sell instructions.
        bottom_line must be one short judgment sentence, 12-25 words when possible, without direct buy/sell instructions.
        contrarian_angle must explain what most readers may miss, in one complete sentence.
        why_it_matters must explain why the story matters beyond the headline, in one complete sentence.
        market_implication must explain affected assets, sectors, or trends, in one complete sentence with up to 4 recognizable tickers/assets.
        market_context must be one short sentence explaining the broader market context.
        If category is Web3 News, web3_segment must be exactly one of NFT, DAO, GameFi, SocialFi, Wallet, Airdrop, L2, Identity, or Web3.
        final_x_post must be 450-800 characters whenever possible and never below 400 characters. Premium-length posts are allowed. If longer than 280, the app will show it as a thread for manual review.
        final_x_post structure: hook; natural explanation of what happened; market implication; contrarian opinion; discussion question; 2-5 relevant cashtags when known; up to 3 hashtags.
        Do not output "Source:" or the raw URL in final_x_post. The app stores source_url separately for Telegram and link-card workflows.
        source_url must still exist in the JSON context. Do not hard-cut words, leave broken sentences, or use ellipses.
        Before returning, self-check: Human Score must be 8/10 or higher, AI Smell Score must be 4/10 or lower, Discussion Potential must be 7/10 or higher, Market Insight must be 7/10 or higher. Regenerate internally if it fails.
        Avoid absolute investment claims such as "必涨", "必跌", "稳赚", "确定利好", or "确定利空".
        Score the story using five 0-20 fields: timeliness, investmentImpact, controversy, globalAttention, virality.
        totalScore must be 0-100.
        isBreaking should be true only for unusually urgent, high-impact news.
        JSON schema:
        {
          "short_headline": "max 8 English words, rewritten for X, not a truncated source title",
          "summary": "one complete English sentence, 20-35 words when possible",
          "market_impact": "Bullish|Bearish|Neutral",
          "market_impact_score": 1,
          "affected_assets": ["BTC", "NVDA", "QQQ"],
          "winners": ["none"],
          "losers": ["none"],
          "trade_take": "one sentence market read, 20-35 words when possible, no direct buy/sell advice",
          "bottom_line": "one short judgment, 12-25 words when possible, no direct buy/sell advice",
          "contrarian_angle": "one complete sentence on what the market may be missing",
          "why_it_matters": "one complete sentence explaining why the story matters",
          "market_implication": "one complete sentence explaining asset, sector, or trend implications",
          "market_context": "one short sentence about broader market context",
          "web3_segment": "NFT|DAO|GameFi|SocialFi|Wallet|Airdrop|L2|Identity|Web3",
          "confidence_score": 1,
          "final_x_post": "human market commentary 450-800 chars, no raw URL, no Source label, no mechanical field labels",
          "englishTweetBody": "English post body without URL",
          "chineseBrief": "中文审核解读",
          "timeliness": 0,
          "investmentImpact": 0,
          "controversy": 0,
          "globalAttention": 0,
          "virality": 0,
          "totalScore": 0,
          "isBreaking": false,
          "rationale": "short reason"
        }
        """;

    private static string BuildTweetOnlySystemPrompt() =>
        """
        You write English X posts for a Global Markets Intelligence account covering AI, crypto, DeFi, meme coins, stocks, macro, politics, China, world events, and major sports stories.
        Return only valid JSON.
        Do not invent facts beyond the provided title, summary, source, timestamp, category, and link.
        Do not include, translate, alter, shorten, or rewrite the URL.
        Do not give direct investment advice or tell readers to buy, sell, hold, short, or enter trades.
        You are not a news summarizer. Write like a native-English investor, Web3 KOL, macro observer, and human market editor with a large X audience.
        Learn the energy of Cobie, Mario Nawfal, The Kobeissi Letter, Autism Capital, Alex Kruger, Ryan Selkis, The Defiant, Milk Road, unusual_whales, Wall Street Silver, and Watcher Guru without copying catchphrases.
        News is 30%; analysis, second-order effects, controversy, and point of view are 70%.
        Prefer human framing: "Most people are looking at the headline.", "They're missing the real story.", "The market may be pricing this completely wrong.", "The headline is obvious. The downstream consequences are not.", "Would love to hear the bear case here."
        final_x_post must never contain mechanical labels or robot phrasing: Impact:, Score:, Impact Score, AI Score, Confidence Score, AI Confidence, Winners, Losers, Assets:, Affected Assets, Market Impact, Bullish:, Bearish:, Source:, According to the report, This article states.
        final_x_post must not use bullets, numbered lists, or template labels. It should read like a human investor posting a market view.
        Avoid hype words and repeated bot hooks: "This is bullish", "Big news", "Huge", "Breaking", and "Most people miss".
        englishTweetBody is for X publishing. It must be English, no URL, 240 English characters or fewer.
        chineseBrief is for Telegram review only. It must be Simplified Chinese, 160 Chinese characters or fewer, explaining why the story is worth posting.
        Also produce concise market impact analysis for review and X posting.
        short_headline must be newly written by you, max 8 English words, not a direct truncation of the source title.
        summary must be one complete English sentence, 20-35 words when possible.
        market_impact must be exactly Bullish, Bearish, or Neutral.
        market_impact_score rubric: single small altcoin only = 2-4; BTC/ETH/ETF/Fed/CPI/SEC/major crypto policy = 5-8; market-wide macro or major regulation = 8-10.
        If the story mentions BTC, ETH, Bitcoin ETF, Fed rates, CPI, SEC, BlackRock ETF, Coinbase, Binance, or major crypto regulation, market_impact_score should normally be at least 5 unless the news is stale or clearly weak.
        The account sections are AI, Crypto, DeFi, Meme, Web3, Stocks, Macro, Politics, China, World, Sports, and Global Impact.
        affected_assets should use tickers/assets such as BTC, ETH, SOL, XRP, DOGE, PEPE, AAVE, MKR, PENDLE, ENA, UNI, CRV, NVDA, MSFT, AAPL, META, AMZN, TSLA, AMD, AVGO, PLTR, ORCL, SMCI, COIN, MSTR, BABA, BIDU, PDD, QQQ, SPY, IBIT, FBTC, Gold, USD; if none, use ["No direct asset impact"].
        winners and losers should use ["none"] if there is no clear beneficiary or harmed asset/company.
        Actively identify crypto assets, public companies, ETFs, macro assets, and relevant sectors from the story.
        Prefer recognizable ticker/asset names in affected_assets, winners, and losers; code will standardize them into X cashtags.
        trade_take must be one market interpretation sentence, 20-35 words when possible, without direct buy/sell instructions.
        bottom_line must be one short judgment sentence, 12-25 words when possible, without direct buy/sell instructions.
        contrarian_angle must explain what most readers may miss, in one complete sentence.
        why_it_matters must explain why the story matters beyond the headline, in one complete sentence.
        market_implication must explain affected assets, sectors, or trends, in one complete sentence with up to 4 recognizable tickers/assets.
        market_context must be one short sentence explaining the broader market context.
        If category is Web3 News, web3_segment must be exactly one of NFT, DAO, GameFi, SocialFi, Wallet, Airdrop, L2, Identity, or Web3.
        final_x_post must be 450-800 characters whenever possible and never below 400 characters. Premium-length posts are allowed. If longer than 280, the app will show it as a thread for manual review.
        final_x_post structure: hook; natural explanation of what happened; market implication; contrarian opinion; discussion question; 2-5 relevant cashtags when known; up to 3 hashtags.
        Do not output "Source:" or the raw URL in final_x_post. The app stores source_url separately for Telegram and link-card workflows.
        source_url must still exist in the JSON context. Do not hard-cut words, leave broken sentences, or use ellipses.
        Before returning, self-check: Human Score must be 8/10 or higher, AI Smell Score must be 4/10 or lower, Discussion Potential must be 7/10 or higher, Market Insight must be 7/10 or higher. Regenerate internally if it fails.
        Avoid absolute investment claims.
        JSON schema:
        {
          "short_headline": "max 8 English words, rewritten for X, not a truncated source title",
          "summary": "one complete English sentence, 20-35 words when possible",
          "market_impact": "Bullish|Bearish|Neutral",
          "market_impact_score": 1,
          "affected_assets": ["BTC", "NVDA", "QQQ"],
          "winners": ["none"],
          "losers": ["none"],
          "trade_take": "one sentence market read, 20-35 words when possible, no direct buy/sell advice",
          "bottom_line": "one short judgment, 12-25 words when possible, no direct buy/sell advice",
          "contrarian_angle": "one complete sentence on what the market may be missing",
          "why_it_matters": "one complete sentence explaining why the story matters",
          "market_implication": "one complete sentence explaining asset, sector, or trend implications",
          "market_context": "one short sentence about broader market context",
          "web3_segment": "NFT|DAO|GameFi|SocialFi|Wallet|Airdrop|L2|Identity|Web3",
          "confidence_score": 1,
          "final_x_post": "human market commentary 450-800 chars, no raw URL, no Source label, no mechanical field labels",
          "englishTweetBody": "English post body without URL",
          "chineseBrief": "中文审核解读"
        }
        """;

    private static string BuildUserPrompt(NewsArticle article) =>
        $"""
        Category: {article.Category}
        Source: {article.SourceName}
        Published UTC: {article.PublishedAt:O}
        Title: {article.Title}
        Summary: {Truncate(CleanText(article.Summary), 900)}
        source_url: {article.Link}
        """;

    private static NewsTweetPlan CreateFallbackPlan(NewsArticle article, string reason)
    {
        var englishBody = SanitizeEnglishBody($"{article.Category}: {CleanText(article.Title)}", GetInt("ENGLISH_TWEET_BODY_MAX_CHARS", 240));
        var marketAnalysis = CreateFallbackMarketAnalysis(article);
        var tweet = BuildFinalXPost(article.Title, englishBody, marketAnalysis, article.Link, article.Category);
        var ageHours = Math.Max(0, (DateTimeOffset.UtcNow - article.PublishedAt).TotalHours);
        var timeliness = ageHours <= 3 ? 18 : ageHours <= 12 ? 14 : ageHours <= 24 ? 10 : 6;
        var impact = KeywordScore(article.Title + " " + article.Summary, ["Fed", "SEC", "ban", "lawsuit", "tariff", "inflation", "rate", "AI chip", "Bitcoin", "Ethereum", "OpenAI", "Nvidia"]);
        var controversy = KeywordScore(article.Title + " " + article.Summary, ["probe", "lawsuit", "hack", "breach", "fraud", "ban", "sanction", "crackdown", "antitrust"]);
        var global = KeywordScore(article.Title + " " + article.Summary, ["US", "EU", "China", "global", "G7", "central bank", "Treasury", "Nvidia", "Apple", "Microsoft", "Google"]);
        var virality = KeywordScore(article.Title + " " + article.Summary, ["surge", "plunge", "record", "warning", "breaks", "halts", "launches", "emergency"]);
        var total = ClampTotal(timeliness + impact + controversy + global + virality);

        return new NewsTweetPlan
        {
            Article = article,
            Tweet = tweet,
            EnglishTweetBody = englishBody,
            ChineseBrief = $"本地模板：{CleanText(article.Title)}",
            MarketAnalysis = marketAnalysis,
            FinalXPost = tweet,
            Score = new NewsScore
            {
                Timeliness = timeliness,
                InvestmentImpact = impact,
                Controversy = controversy,
                GlobalAttention = global,
                Virality = virality,
                Total = total,
                IsBreaking = total >= 90,
                Rationale = $"Fallback scoring used: {reason}"
            }
        };
    }

    private static NewsTweetPlan CreateTemplatePlan(NewsArticle article, NewsScore score, bool isTemplateTweet)
    {
        var englishBody = SanitizeEnglishBody($"Template tweet: {article.Category}: {CleanText(article.Title)}", GetInt("ENGLISH_TWEET_BODY_MAX_CHARS", 240));
        var marketAnalysis = CreateFallbackMarketAnalysis(article);
        var tweet = BuildFinalXPost(article.Title, englishBody, marketAnalysis, article.Link, article.Category);
        return new NewsTweetPlan
        {
            Article = article,
            Tweet = tweet,
            EnglishTweetBody = englishBody,
            ChineseBrief = $"本地模板推文，仅用于测试审核流程：{CleanText(article.Title)}",
            MarketAnalysis = marketAnalysis,
            FinalXPost = tweet,
            Score = score,
            IsTemplateTweet = isTemplateTweet
        };
    }

    private static NewsScore BuildOpenAiScore(GeneratedNewsTweet generated)
    {
        var score = new NewsScore
        {
            Timeliness = Clamp(generated.Timeliness),
            InvestmentImpact = Clamp(generated.InvestmentImpact),
            Controversy = Clamp(generated.Controversy),
            GlobalAttention = Clamp(generated.GlobalAttention),
            Virality = Clamp(generated.Virality),
            IsBreaking = generated.IsBreaking,
            Rationale = generated.Rationale ?? string.Empty
        };

        var total = ClampTotal(generated.TotalScore);
        if (total == 0)
        {
            total = ClampTotal(score.Timeliness + score.InvestmentImpact + score.Controversy + score.GlobalAttention + score.Virality);
        }

        return score with { Total = total };
    }

    private static int KeywordScore(string text, IReadOnlyList<string> keywords)
    {
        var matches = keywords.Count(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        return Math.Clamp(8 + (matches * 3), 0, 20);
    }

    private static string SanitizeTweet(string tweet, string link)
    {
        var cleaned = CleanText(tweet);
        cleaned = InvestmentAdviceRegex().Replace(cleaned, "仍需观察后续影响");

        if (!cleaned.Contains(link, StringComparison.OrdinalIgnoreCase))
        {
            cleaned = $"{cleaned} {link}";
        }

        if (cleaned.Length <= MaxTweetLength)
        {
            return cleaned;
        }

        var linkBudget = string.IsNullOrWhiteSpace(link) ? 0 : link.Length + 1;
        var textBudget = Math.Max(40, MaxTweetLength - linkBudget - 1);
        var withoutLink = cleaned.Replace(link, string.Empty, StringComparison.OrdinalIgnoreCase).Trim();

        if (withoutLink.Length > textBudget)
        {
            withoutLink = withoutLink[..textBudget].TrimEnd(' ', '.', ',', ';', ':') + "...";
        }

        return string.IsNullOrWhiteSpace(link) ? withoutLink : $"{withoutLink} {link}";
    }

    public static string BuildFinalTweet(string englishTweetBody, string originalUrl)
    {
        var body = SanitizeEnglishBody(englishTweetBody, GetInt("ENGLISH_TWEET_BODY_MAX_CHARS", 240));
        return $"{body} {originalUrl}".Trim();
    }

    public static string BuildFinalXPost(string title, string englishTweetBody, NewsMarketAnalysis analysis, string sourceUrl, string category = "")
    {
        var shortHeadline = LimitWords(SanitizeFinalPostText(
            string.IsNullOrWhiteSpace(analysis.ShortHeadline) ? title : analysis.ShortHeadline,
            80), 8);
        if (string.IsNullOrWhiteSpace(shortHeadline))
        {
            shortHeadline = "Market News Update";
        }

        var summarySource = string.IsNullOrWhiteSpace(analysis.Summary)
            ? string.IsNullOrWhiteSpace(analysis.AiSummary) ? englishTweetBody : analysis.AiSummary
            : analysis.Summary;
        var takeSource = string.IsNullOrWhiteSpace(analysis.TradeTake)
            ? string.IsNullOrWhiteSpace(analysis.OneLineImpact) ? $"{analysis.MarketImpact} impact needs follow-through." : analysis.OneLineImpact
            : analysis.TradeTake;

        var confidence = Math.Clamp(analysis.ConfidenceScore == 0 ? analysis.AiConfidenceScore : analysis.ConfidenceScore, 1, 10);

        return BuildSafeFinalXPost(
            shortHeadline,
            summarySource,
            analysis.MarketImpact,
            analysis.AffectedAssets,
            analysis.Winners,
            analysis.Losers,
            takeSource,
            analysis.BottomLine,
            analysis.ContrarianAngle,
            analysis.WhyItMatters,
            analysis.MarketImplication,
            analysis.MarketContext,
            analysis.Web3Segment,
            analysis.Hashtags,
            analysis.MarketImpactScore,
            confidence,
            sourceUrl,
            category);
    }

    public static bool IsSafeEnglishTweet(string tweet, string originalUrl)
    {
        return !string.IsNullOrWhiteSpace(tweet)
            && !string.IsNullOrWhiteSpace(originalUrl)
            && !ContainsCjk(tweet)
            && !ContainsMechanicalXLabel(tweet)
            && tweet.Count(ch => ch == '\n') < 32
            && FinalQualityGate(tweet, originalUrl, string.Empty, out _);
    }

    private static NewsMarketAnalysis BuildMarketAnalysis(GeneratedNewsTweet generated, NewsArticle article)
    {
        var impact = NormalizeMarketImpact(generated.MarketImpact);
        var confidence = Math.Clamp(generated.ConfidenceScore == 0 ? generated.AiConfidenceScore : generated.ConfidenceScore, 1, 10);
        var detectedAssets = AssetSymbolMapper.ExtractFromText($"{article.Title} {article.Summary}");
        var affectedAssets = CleanAssetList((generated.AffectedAssets ?? []).Concat(detectedAssets), "No direct asset impact");
        var winners = CleanAssetList(generated.Winners);
        var losers = CleanAssetList(generated.Losers);
        var impactScore = NormalizeImpactScore(
            generated.MarketImpactScore == 0 ? 5 : generated.MarketImpactScore,
            article,
            affectedAssets,
            generated.Summary ?? generated.AiSummary ?? generated.EnglishTweetBody);
        var summary = SanitizeFinalPostText(string.IsNullOrWhiteSpace(generated.Summary)
            ? string.IsNullOrWhiteSpace(generated.AiSummary) ? generated.EnglishTweetBody ?? article.Title : generated.AiSummary
            : generated.Summary, 150);
        var tradeTake = SanitizeFinalPostText(string.IsNullOrWhiteSpace(generated.TradeTake)
            ? string.IsNullOrWhiteSpace(generated.OneLineImpact) ? $"{impact} market impact; watch affected assets." : generated.OneLineImpact
            : generated.TradeTake, 150);
        var bottomLine = SanitizeFinalPostText(string.IsNullOrWhiteSpace(generated.BottomLine)
            ? BuildFallbackBottomLine(article, impact)
            : generated.BottomLine, 120);
        var marketContext = SanitizeFinalPostText(string.IsNullOrWhiteSpace(generated.MarketContext)
            ? BuildFallbackMarketContext(article)
            : generated.MarketContext, 150);
        var contrarianAngle = SanitizeFinalPostText(string.IsNullOrWhiteSpace(generated.ContrarianAngle)
            ? BuildFallbackContrarianAngle(article.Category, $"{article.Title} {article.Summary}")
            : generated.ContrarianAngle, 170);
        var whyItMatters = SanitizeFinalPostText(string.IsNullOrWhiteSpace(generated.WhyItMatters)
            ? tradeTake
            : generated.WhyItMatters, 190);
        var marketImplication = SanitizeFinalPostText(string.IsNullOrWhiteSpace(generated.MarketImplication)
            ? BuildHumanMarketLine(impact, affectedAssets, article.Category)
            : generated.MarketImplication, 190);
        tradeTake = NormalizeAssetMentions(tradeTake);
        bottomLine = NormalizeAssetMentions(bottomLine);
        contrarianAngle = NormalizeAssetMentions(contrarianAngle);
        whyItMatters = NormalizeAssetMentions(whyItMatters);
        marketImplication = NormalizeAssetMentions(marketImplication);
        var web3Segment = article.Category.Contains("Web3", StringComparison.OrdinalIgnoreCase)
            ? NormalizeWeb3Segment(string.IsNullOrWhiteSpace(generated.Web3Segment)
                ? Web3NewsClassifier.Segment($"{article.Title} {article.Summary}")
                : generated.Web3Segment)
            : string.Empty;
        if (!string.IsNullOrWhiteSpace(web3Segment)
            && (string.IsNullOrWhiteSpace(marketContext) || marketContext.Equals(BuildFallbackMarketContext(article), StringComparison.OrdinalIgnoreCase)))
        {
            marketContext = Web3NewsClassifier.BuildContext(web3Segment);
        }

        return new NewsMarketAnalysis
        {
            ShortHeadline = LimitWords(SanitizeFinalPostText(string.IsNullOrWhiteSpace(generated.ShortHeadline)
                ? GenerateFallbackHeadline(article.Title)
                : generated.ShortHeadline, 80), 8),
            Summary = summary,
            AiSummary = summary,
            MarketImpact = impact,
            MarketImpactScore = impactScore,
            AffectedAssets = affectedAssets,
            Winners = winners,
            Losers = losers,
            Hashtags = BuildHashtags(article, affectedAssets),
            TradeTake = tradeTake,
            BottomLine = bottomLine,
            ContrarianAngle = contrarianAngle,
            WhyItMatters = whyItMatters,
            MarketImplication = marketImplication,
            MarketContext = marketContext,
            Web3Segment = web3Segment,
            ConfidenceScore = confidence,
            AiConfidenceScore = confidence,
            OneLineImpact = tradeTake
        };
    }

    private static NewsMarketAnalysis CreateFallbackMarketAnalysis(NewsArticle article)
    {
        var text = $"{article.Title} {article.Summary}";
        var assets = new List<string>();
        AddIfContains(text, assets, "Bitcoin", "BTC");
        AddIfContains(text, assets, "BTC", "BTC");
        AddIfContains(text, assets, "Ethereum", "ETH");
        AddIfContains(text, assets, "Nvidia", "$NVDA");
        AddIfContains(text, assets, "Microsoft", "$MSFT");
        AddIfContains(text, assets, "Apple", "$AAPL");
        AddIfContains(text, assets, "Google", "$GOOGL");
        AddIfContains(text, assets, "OpenAI", "AI");
        assets.AddRange(AssetSymbolMapper.ExtractFromText(text));

        return new NewsMarketAnalysis
        {
            ShortHeadline = GenerateFallbackHeadline(article.Title),
            Summary = SanitizeEnglishBody(article.Summary, 180),
            AiSummary = SanitizeEnglishBody(article.Summary, 180),
            MarketImpact = "Neutral",
            MarketImpactScore = 5,
            AffectedAssets = CleanAssetList(assets, "No direct asset impact"),
            Winners = ["none"],
            Losers = ["none"],
            Hashtags = BuildHashtags(article, assets),
            TradeTake = "Neutral near-term read; market impact depends on follow-through.",
            BottomLine = BuildFallbackBottomLine(article, "Neutral"),
            ContrarianAngle = BuildFallbackContrarianAngle(article.Category, text),
            WhyItMatters = "The story matters if it changes investor expectations, policy risk, or capital flows beyond the immediate headline.",
            MarketImplication = BuildHumanMarketLine("Neutral", CleanAssetList(assets, "No direct asset impact"), article.Category),
            MarketContext = BuildFallbackMarketContext(article),
            Web3Segment = article.Category.Contains("Web3", StringComparison.OrdinalIgnoreCase)
                ? Web3NewsClassifier.Segment(text)
                : string.Empty,
            ConfidenceScore = 5,
            AiConfidenceScore = 5,
            OneLineImpact = "Neutral near-term read; market impact depends on follow-through."
        };
    }

    private static string NormalizeMarketImpact(string? value)
    {
        if (string.Equals(value, "Bullish", StringComparison.OrdinalIgnoreCase))
        {
            return "Bullish";
        }

        if (string.Equals(value, "Bearish", StringComparison.OrdinalIgnoreCase))
        {
            return "Bearish";
        }

        return "Neutral";
    }

    private static string NormalizeWeb3Segment(string? value)
    {
        var cleaned = CleanText(value ?? string.Empty);
        return cleaned.ToLowerInvariant() switch
        {
            "nft" => "NFT",
            "dao" => "DAO",
            "gamefi" or "web3 gaming" => "GameFi",
            "socialfi" or "decentralized social" => "SocialFi",
            "wallet" => "Wallet",
            "airdrop" => "Airdrop",
            "l2" or "layer2" or "layer 2" => "L2",
            "identity" => "Identity",
            _ => "Web3"
        };
    }

    private static List<string> CleanAssetList(IEnumerable<string>? values, string emptyValue = "none")
    {
        var cleanedValues = values?
            .Select(value => AssetSymbolMapper.Normalize(value))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList() ?? [];
        if (cleanedValues.Count == 0)
        {
            return [emptyValue];
        }

        if (cleanedValues.Count > 1)
        {
            cleanedValues = cleanedValues
                .Where(value => !value.Equals("none", StringComparison.OrdinalIgnoreCase)
                    && !value.Equals("No direct asset impact", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return cleanedValues.Count == 0 ? [emptyValue] : cleanedValues;
    }

    private static string FormatList(IReadOnlyList<string> values, string emptyValue)
        => values.Count == 0 ? emptyValue : string.Join(" ", values.Take(5));

    private static string TruncateAssetList(string value, int maxLength)
        => CompressPhrase(value, maxLength);

    private static void AddIfContains(string text, List<string> assets, string needle, string asset)
    {
        if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            assets.Add(asset);
        }
    }

    private static string BuildFallbackMarketContext(NewsArticle article)
        => article.Category.Contains("Web3", StringComparison.OrdinalIgnoreCase)
            ? Web3NewsClassifier.BuildContext(Web3NewsClassifier.Segment($"{article.Title} {article.Summary}"))
            : article.Category.Contains("Crypto", StringComparison.OrdinalIgnoreCase)
            ? "Crypto market sensitivity remains tied to liquidity, ETF flows, and regulatory headlines."
            : article.Category.Contains("AI", StringComparison.OrdinalIgnoreCase) || article.Category.Contains("Big Tech", StringComparison.OrdinalIgnoreCase)
                ? "AI and big-tech headlines can shift platform, chip, and cloud sentiment."
                : article.Category.Contains("Market", StringComparison.OrdinalIgnoreCase)
                    ? "Macro headlines can influence risk appetite across crypto, equities, and the dollar."
                    : "Market reaction depends on whether the story changes sector-level expectations.";

    private static int NormalizeImpactScore(int modelScore, NewsArticle article, IReadOnlyList<string>? generatedAssets, string? generatedText)
    {
        var score = Math.Clamp(modelScore, 1, 10);
        var text = $"{article.Title} {article.Summary} {generatedText} {string.Join(' ', generatedAssets ?? [])}";

        var majorCryptoOrMacro = ContainsAny(text,
            "BTC", "Bitcoin", "ETH", "Ethereum", "Bitcoin ETF", "ETF", "IBIT", "FBTC",
            "Fed", "Federal Reserve", "rates", "rate hike", "CPI", "inflation", "SEC",
            "BlackRock", "Coinbase", "Binance", "crypto regulation", "regulation",
            "Meta", "Facebook", "Nvidia", "Apple", "Microsoft", "Google", "Alphabet");
        var broadMarket = ContainsAny(text,
            "global market", "macro", "central bank", "liquidity", "risk assets", "market-wide",
            "regulatory crackdown", "major regulation", "systemic", "tariff", "recession");

        if (broadMarket)
        {
            return Math.Max(score, 8);
        }

        if (majorCryptoOrMacro)
        {
            return Math.Max(score, 5);
        }

        return score;
    }

    private static bool ContainsAny(string text, params string[] needles)
        => needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static string CompressSentence(string value, int maxLength)
    {
        var cleaned = CleanFinalPostText(value);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return string.Empty;
        }

        var safeSentence = MakeSafeCompleteSentence(cleaned);
        if (!string.IsNullOrWhiteSpace(safeSentence) && XPostLengthHelper.GetWeightedLength(safeSentence) <= maxLength)
        {
            return safeSentence;
        }

        var firstCompleteSentence = Regex.Matches(cleaned, @"[^.!?\n]+[.!?]", RegexOptions.Multiline)
            .Select(match => MakeSafeCompleteSentence(match.Value.Trim()))
            .FirstOrDefault(sentence => !string.IsNullOrWhiteSpace(sentence)
                && XPostLengthHelper.GetWeightedLength(sentence) <= maxLength);

        return firstCompleteSentence ?? string.Empty;
    }

    private static string BuildSafeFinalXPost(
        string shortHeadline,
        string summary,
        string marketImpact,
        IReadOnlyList<string> affectedAssets,
        IReadOnlyList<string> winners,
        IReadOnlyList<string> losers,
        string tradeTake,
        string bottomLine,
        string contrarianAngle,
        string whyItMatters,
        string marketImplication,
        string marketContext,
        string web3Segment,
        IReadOnlyList<string> hashtags,
        int impactScore,
        int confidenceScore,
        string sourceUrl,
        string category)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return string.Empty;
        }

        if (category.Contains("Web3", StringComparison.OrdinalIgnoreCase))
        {
            return BuildSafeWeb3FinalXPost(
                summary,
                marketImpact,
                affectedAssets,
                tradeTake,
                bottomLine,
                contrarianAngle,
                whyItMatters,
                marketImplication,
                marketContext,
                web3Segment,
                hashtags,
                impactScore,
                sourceUrl);
        }

        var safeSummary = MakeSafeCompleteSentence(summary);
        var safeTradeTake = MakeSafeCompleteSentence(EnsureAiTake(tradeTake, category, marketImpact));
        var safeBottomLine = MakeSafeCompleteSentence(string.IsNullOrWhiteSpace(bottomLine)
            ? BuildFallbackBottomLine(category, marketImpact)
            : bottomLine);
        var safeContrarian = MakeSafeCompleteSentence(string.IsNullOrWhiteSpace(contrarianAngle)
            ? BuildFallbackContrarianAngle(category, $"{summary} {tradeTake} {marketContext}")
            : contrarianAngle);
        var safeWhy = MakeSafeCompleteSentence(string.IsNullOrWhiteSpace(whyItMatters) ? tradeTake : whyItMatters);
        var safeMarketImplication = MakeSafeCompleteSentence(string.IsNullOrWhiteSpace(marketImplication)
            ? BuildHumanMarketLine(marketImpact, affectedAssets, category)
            : marketImplication);
        var hashtagLine = FormatHashtags(hashtags);
        var hook = BuildHumanHook(category, $"{summary} {tradeTake} {marketContext}");
        var marketLine = BuildHumanMarketLine(marketImpact, affectedAssets, category);
        var question = BuildDiscussionQuestion(category, marketImpact, affectedAssets);
        if (string.IsNullOrWhiteSpace(safeSummary))
        {
            safeSummary = "This update may shift market attention across the related sector.";
        }

        if (string.IsNullOrWhiteSpace(safeTradeTake))
        {
            safeTradeTake = EnsureAiTake(string.Empty, category, marketImpact);
        }

        if (string.IsNullOrWhiteSpace(safeBottomLine))
        {
            safeBottomLine = BuildFallbackBottomLine(category, marketImpact);
        }

        if (string.IsNullOrWhiteSpace(safeContrarian))
        {
            safeContrarian = BuildFallbackContrarianAngle(category, $"{summary} {tradeTake} {marketContext}");
        }

        if (string.IsNullOrWhiteSpace(safeWhy))
        {
            safeWhy = EnsureAiTake(string.Empty, category, marketImpact);
        }

        if (string.IsNullOrWhiteSpace(safeMarketImplication))
        {
            safeMarketImplication = marketLine;
        }

        var compactSummary = NonEmpty(MakeSafeCompleteSentence(CompressSentence(safeSummary, 82)), safeSummary);
        var compactWhy = NonEmpty(MakeSafeCompleteSentence(CompressSentence(safeWhy, 98)), safeWhy);
        var compactImplication = NonEmpty(MakeSafeCompleteSentence(CompressSentence(safeMarketImplication, 98)), safeMarketImplication);
        var compactBottomLine = NonEmpty(MakeSafeCompleteSentence(CompressSentence(safeBottomLine, 66)), safeBottomLine);
        var tightSummary = NonEmpty(MakeSafeCompleteSentence(CompressSentence(safeSummary, 52)), compactSummary);
        var tightWhy = NonEmpty(MakeSafeCompleteSentence(CompressSentence(safeWhy, 72)), compactWhy);
        var tightImplication = NonEmpty(MakeSafeCompleteSentence(CompressSentence(safeMarketImplication, 72)), compactImplication);
        var tightBottomLine = NonEmpty(MakeSafeCompleteSentence(CompressSentence(safeBottomLine, 42)), compactBottomLine);
        var ultraSummary = NonEmpty(MakeSafeCompleteSentence(CompressSentence(safeSummary, 42)), tightSummary);
        var ultraWhy = NonEmpty(MakeSafeCompleteSentence(CompressSentence(safeWhy, 58)), tightWhy);
        var ultraImplication = NonEmpty(MakeSafeCompleteSentence(CompressSentence(safeMarketImplication, 58)), tightImplication);
        var ultraBottomLine = NonEmpty(MakeSafeCompleteSentence(CompressSentence(safeBottomLine, 34)), tightBottomLine);
        var compactMarketLine = BuildHumanMarketLine(marketImpact, affectedAssets.Take(3).ToList(), category);
        var tightMarketLine = BuildHumanMarketLine(marketImpact, affectedAssets.Take(2).ToList(), category);
        var shortestMarketLine = BuildHumanMarketLine(marketImpact, affectedAssets.Take(1).ToList(), category);

        var candidates = new List<string>
        {
            JoinLines(
                hook,
                string.Empty,
                safeContrarian,
                string.Empty,
                safeSummary,
                string.Empty,
                safeWhy,
                string.Empty,
                safeMarketImplication,
                string.Empty,
                safeBottomLine,
                string.Empty,
                FormatMarketAssets(affectedAssets),
                string.Empty,
                question,
                string.Empty,
                hashtagLine),
            JoinLines(
                hook,
                string.Empty,
                safeContrarian,
                string.Empty,
                safeSummary,
                safeWhy,
                safeMarketImplication,
                safeBottomLine,
                FormatMarketAssets(affectedAssets),
                question,
                ReduceHashtags(hashtagLine, 2)),
            JoinLines(
                hook,
                string.Empty,
                safeContrarian,
                string.Empty,
                compactSummary,
                compactWhy,
                compactImplication,
                compactBottomLine,
                FormatMarketAssets(affectedAssets),
                question),
            JoinLines(
                hook,
                string.Empty,
                safeContrarian,
                string.Empty,
                tightSummary,
                tightWhy,
                tightImplication,
                tightBottomLine,
                FormatMarketAssets(affectedAssets),
                question),
            JoinLines(
                hook,
                string.Empty,
                safeContrarian,
                string.Empty,
                ultraSummary,
                ultraWhy,
                ultraImplication,
                ultraBottomLine,
                shortestMarketLine,
                FormatMarketAssets(affectedAssets),
                question),
            JoinLines(
                hook,
                safeSummary,
                safeWhy,
                safeMarketImplication,
                question),
            JoinLines(
                hook,
                tightSummary,
                tightWhy,
                tightMarketLine,
                question)
        };

        return SelectFinalCandidate(candidates, sourceUrl, category, impactScore);
    }

    private static string BuildSafeWeb3FinalXPost(
        string summary,
        string marketImpact,
        IReadOnlyList<string> affectedAssets,
        string tradeTake,
        string bottomLine,
        string contrarianAngle,
        string whyItMatters,
        string marketImplication,
        string marketContext,
        string web3Segment,
        IReadOnlyList<string> hashtags,
        int impactScore,
        string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return string.Empty;
        }

        var safeSummary = MakeSafeCompleteSentence(summary);
        var safeTake = MakeSafeCompleteSentence(EnsureAiTake(tradeTake, Web3NewsClassifier.Category, marketImpact));
        var safeBottomLine = MakeSafeCompleteSentence(string.IsNullOrWhiteSpace(bottomLine)
            ? BuildFallbackBottomLine(Web3NewsClassifier.Category, marketImpact)
            : bottomLine);
        var safeContrarian = MakeSafeCompleteSentence(string.IsNullOrWhiteSpace(contrarianAngle)
            ? BuildFallbackContrarianAngle(Web3NewsClassifier.Category, $"{summary} {tradeTake} {marketContext} {web3Segment}")
            : contrarianAngle);
        var safeWhy = MakeSafeCompleteSentence(string.IsNullOrWhiteSpace(whyItMatters) ? tradeTake : whyItMatters);
        var safeMarketImplication = MakeSafeCompleteSentence(string.IsNullOrWhiteSpace(marketImplication)
            ? BuildHumanMarketLine(marketImpact, affectedAssets, Web3NewsClassifier.Category)
            : marketImplication);
        var safeContext = MakeSafeCompleteSentence(string.IsNullOrWhiteSpace(marketContext)
            ? Web3NewsClassifier.BuildContext(web3Segment)
            : marketContext);
        var marketLine = BuildHumanMarketLine(marketImpact, affectedAssets, Web3NewsClassifier.Category);
        if (string.IsNullOrWhiteSpace(safeSummary))
        {
            safeSummary = "Web3 ecosystem activity is shifting around this update.";
        }

        if (string.IsNullOrWhiteSpace(safeTake))
        {
            safeTake = "This matters if it improves user activity, liquidity, or developer attention across Web3 ecosystems.";
        }

        if (string.IsNullOrWhiteSpace(safeBottomLine))
        {
            safeBottomLine = "The signal is constructive only if adoption metrics improve.";
        }

        if (string.IsNullOrWhiteSpace(safeContext))
        {
            safeContext = Web3NewsClassifier.BuildContext(web3Segment);
        }

        if (string.IsNullOrWhiteSpace(safeContrarian))
        {
            safeContrarian = BuildFallbackContrarianAngle(Web3NewsClassifier.Category, $"{summary} {tradeTake} {marketContext} {web3Segment}");
        }

        if (string.IsNullOrWhiteSpace(safeWhy))
        {
            safeWhy = safeTake;
        }

        if (string.IsNullOrWhiteSpace(safeMarketImplication))
        {
            safeMarketImplication = marketLine;
        }

        var hashtagLine = FormatHashtags(hashtags);
        var hook = BuildHumanHook(Web3NewsClassifier.Category, $"{summary} {tradeTake} {marketContext} {web3Segment}");
        var question = BuildDiscussionQuestion(Web3NewsClassifier.Category, marketImpact, affectedAssets);
        var compactSummary = NonEmpty(MakeSafeCompleteSentence(CompressSentence(safeSummary, 82)), safeSummary);
        var compactWhy = NonEmpty(MakeSafeCompleteSentence(CompressSentence(safeWhy, 98)), safeWhy);
        var compactImplication = NonEmpty(MakeSafeCompleteSentence(CompressSentence(safeMarketImplication, 98)), safeMarketImplication);
        var compactBottomLine = NonEmpty(MakeSafeCompleteSentence(CompressSentence(safeBottomLine, 66)), safeBottomLine);
        var tightSummary = NonEmpty(MakeSafeCompleteSentence(CompressSentence(safeSummary, 52)), compactSummary);
        var tightWhy = NonEmpty(MakeSafeCompleteSentence(CompressSentence(safeWhy, 72)), compactWhy);
        var tightImplication = NonEmpty(MakeSafeCompleteSentence(CompressSentence(safeMarketImplication, 72)), compactImplication);
        var tightBottomLine = NonEmpty(MakeSafeCompleteSentence(CompressSentence(safeBottomLine, 42)), compactBottomLine);
        var ultraSummary = NonEmpty(MakeSafeCompleteSentence(CompressSentence(safeSummary, 42)), tightSummary);
        var ultraWhy = NonEmpty(MakeSafeCompleteSentence(CompressSentence(safeWhy, 58)), tightWhy);
        var ultraImplication = NonEmpty(MakeSafeCompleteSentence(CompressSentence(safeMarketImplication, 58)), tightImplication);
        var ultraBottomLine = NonEmpty(MakeSafeCompleteSentence(CompressSentence(safeBottomLine, 34)), tightBottomLine);
        var compactMarketLine = BuildHumanMarketLine(marketImpact, affectedAssets.Take(3).ToList(), Web3NewsClassifier.Category);
        var tightMarketLine = BuildHumanMarketLine(marketImpact, affectedAssets.Take(2).ToList(), Web3NewsClassifier.Category);
        var shortestMarketLine = BuildHumanMarketLine(marketImpact, affectedAssets.Take(1).ToList(), Web3NewsClassifier.Category);

        var candidates = new List<string>
        {
            JoinLines(
                hook,
                string.Empty,
                safeContrarian,
                string.Empty,
                safeSummary,
                string.Empty,
                safeWhy,
                string.Empty,
                safeContext,
                string.Empty,
                safeMarketImplication,
                string.Empty,
                safeBottomLine,
                string.Empty,
                FormatMarketAssets(affectedAssets),
                string.Empty,
                question,
                string.Empty,
                hashtagLine),
            JoinLines(
                hook,
                string.Empty,
                safeContrarian,
                string.Empty,
                safeSummary,
                safeWhy,
                safeContext,
                safeMarketImplication,
                safeBottomLine,
                FormatMarketAssets(affectedAssets),
                question,
                ReduceHashtags(hashtagLine, 2)),
            JoinLines(
                hook,
                string.Empty,
                safeContrarian,
                string.Empty,
                compactSummary,
                compactWhy,
                compactImplication,
                compactBottomLine,
                FormatMarketAssets(affectedAssets),
                question),
            JoinLines(
                hook,
                string.Empty,
                safeContrarian,
                string.Empty,
                tightSummary,
                tightWhy,
                tightImplication,
                tightBottomLine,
                FormatMarketAssets(affectedAssets),
                question),
            JoinLines(
                hook,
                string.Empty,
                safeContrarian,
                string.Empty,
                ultraSummary,
                ultraWhy,
                ultraImplication,
                ultraBottomLine,
                shortestMarketLine,
                FormatMarketAssets(affectedAssets),
                question),
            JoinLines(
                hook,
                safeSummary,
                safeWhy,
                safeMarketImplication,
                question),
            JoinLines(
                hook,
                tightSummary,
                tightWhy,
                tightMarketLine,
                question)
        };

        return SelectFinalCandidate(candidates, sourceUrl, Web3NewsClassifier.Category, impactScore);
    }

    private static string BuildImpactLevel(int score)
        => score switch
        {
            <= 3 => "Low",
            <= 6 => "Medium",
            <= 8 => "High",
            _ => "Critical"
        };

    private static string BuildHumanHook(string category, string context = "")
    {
        if (ContainsAny(context, "bank", "banks", "banking", "debank", "de-banking", "financial system", "DOJ", "JPMorgan", "Bank of America"))
        {
            return PickHook(context, "banks", "crypto access");
        }

        if (ContainsAny(context, "cyber", "hack", "breach", "ransomware", "security", "North Korean"))
        {
            return PickHook(context, "cybersecurity", "enterprise trust");
        }

        if (category.Contains("Crypto", StringComparison.OrdinalIgnoreCase))
        {
            return PickHook(context, "price", "market structure");
        }

        if (category.Contains("AI", StringComparison.OrdinalIgnoreCase) || category.Contains("Big Tech", StringComparison.OrdinalIgnoreCase))
        {
            return PickHook(context, "AI", "infrastructure demand");
        }

        if (category.Contains("Stocks", StringComparison.OrdinalIgnoreCase))
        {
            return PickHook(context, "the headline", "earnings quality");
        }

        if (category.Contains("Macro", StringComparison.OrdinalIgnoreCase) || category.Contains("Market", StringComparison.OrdinalIgnoreCase))
        {
            return PickHook(context, "the macro print", "liquidity");
        }

        if (category.Contains("Global", StringComparison.OrdinalIgnoreCase) || category.Contains("Policy", StringComparison.OrdinalIgnoreCase) || category.Contains("Regulation", StringComparison.OrdinalIgnoreCase))
        {
            return PickHook(context, "policy", "market access");
        }

        if (category.Contains("Web3", StringComparison.OrdinalIgnoreCase))
        {
            return PickHook(context, "Web3", "adoption");
        }

        return PickHook(context, "the headline", "the second-order effect");
    }

    private static string PickHook(string seed, string obvious, string important)
    {
        var templates = new[]
        {
            $"The headline is about {obvious}. The real story may be {important}.",
            $"Everyone is watching {obvious}. I'm watching {important}.",
            "This looks like a small story. I don't think it is.",
            "The market may be underestimating this.",
            $"The obvious story is {obvious}. The important story is {important}.",
            "Under the surface, something important is changing.",
            "Most traders are focused on price. I'm focused on structure.",
            "This is not just a crypto story. It is an infrastructure story.",
            "Investors are reacting to the headline. The bigger signal is deeper.",
            "The market is treating this like noise. It may not be.",
            "Most people are looking at the headline. They're missing the real story.",
            "The headline is obvious. The downstream consequences are not.",
            "Consensus will probably focus on the first-order story. I think the second-order effects matter more.",
            "The market may be pricing this completely wrong.",
            "This feels bigger than most people realize."
        };

        var index = Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(seed)) % templates.Length;
        return templates[index];
    }

    private static string BuildHumanMarketLine(string marketImpact, IReadOnlyList<string> affectedAssets, string category)
    {
        var assets = FormatMarketAssets(affectedAssets);
        if (!string.IsNullOrWhiteSpace(assets))
        {
            return $"If this trend continues, it could matter for {assets}.";
        }

        var area = category.Contains("AI", StringComparison.OrdinalIgnoreCase) ? "AI equities"
            : category.Contains("Crypto", StringComparison.OrdinalIgnoreCase) ? "crypto risk appetite"
            : category.Contains("Web3", StringComparison.OrdinalIgnoreCase) ? "Web3 adoption"
            : category.Contains("Stocks", StringComparison.OrdinalIgnoreCase) ? "equity leadership"
            : category.Contains("Macro", StringComparison.OrdinalIgnoreCase) ? "rates, the dollar, and risk assets"
            : "global markets";
        return $"If this trend continues, it could shift sentiment across {area}.";
    }

    private static string BuildDiscussionQuestion(string category, string marketImpact, IReadOnlyList<string> affectedAssets)
    {
        var questionPool = new[]
        {
            "Would love to hear the bear case here.",
            "Am I missing something?",
            "Bullish, bearish, or overhyped?",
            "Could this become a bigger story?",
            "Is the market pricing this correctly?",
            "What happens if this trend continues?",
            "Does this change your view on the sector?"
        };
        var assets = FormatMarketAssets(affectedAssets);
        if (category.Contains("AI", StringComparison.OrdinalIgnoreCase) || category.Contains("Big Tech", StringComparison.OrdinalIgnoreCase))
        {
            return PickDiscussionQuestion(category + assets, "Are investors underestimating the infrastructure angle?", questionPool);
        }

        if (category.Contains("Crypto", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(assets)
                ? PickDiscussionQuestion(category, "Is the market pricing this correctly?", questionPool)
                : PickDiscussionQuestion(category + assets, $"Is the market pricing this correctly for {assets}?", questionPool);
        }

        if (category.Contains("Web3", StringComparison.OrdinalIgnoreCase))
        {
            return PickDiscussionQuestion(category + assets, "Could this become a bigger adoption story?", questionPool);
        }

        if (category.Contains("Stocks", StringComparison.OrdinalIgnoreCase))
        {
            return PickDiscussionQuestion(category + assets, "Are investors watching the right signal?", questionPool);
        }

        if (category.Contains("Macro", StringComparison.OrdinalIgnoreCase) || category.Contains("Market", StringComparison.OrdinalIgnoreCase))
        {
            return PickDiscussionQuestion(category + assets, "What happens if this trend continues?", questionPool);
        }

        if (category.Contains("Global", StringComparison.OrdinalIgnoreCase)
            || category.Contains("Policy", StringComparison.OrdinalIgnoreCase)
            || category.Contains("Regulation", StringComparison.OrdinalIgnoreCase))
        {
            return PickDiscussionQuestion(category + assets, "Could this become a bigger market story?", questionPool);
        }

        return marketImpact.Equals("Neutral", StringComparison.OrdinalIgnoreCase)
            ? PickDiscussionQuestion(category + marketImpact, "Is the market paying enough attention?", questionPool)
            : PickDiscussionQuestion(category + marketImpact, "Is the market pricing this correctly?", questionPool);
    }

    private static string PickDiscussionQuestion(string seed, string preferred, IReadOnlyList<string> fallbacks)
    {
        if (!string.IsNullOrWhiteSpace(preferred)
            && Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(seed)) % 3 != 0)
        {
            return preferred;
        }

        var index = Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(seed)) % fallbacks.Count;
        return fallbacks[index];
    }

    private static bool ContainsMechanicalXLabel(string value)
    {
        var banned = new[]
        {
            "Impact:", "Score:", "AI Confidence:", "Winners:", "Losers:", "Affected Assets:",
            "Market Impact:", "Impact Score:", "AI Score:", "Confidence Score:", "Assets:",
            "Related Assets:", "AI Take:", "Bottom Line:", "Bullish:", "Bearish:",
            "According to the report", "This article states", "This report states",
            "According to this article", "Source:", "This is bullish", "Big news",
            "Huge", "Breaking", "Most people miss"
        };

        return banned.Any(label => value.Contains(label, StringComparison.OrdinalIgnoreCase));
    }

    private static string SelectFinalCandidate(IEnumerable<string> candidates, string sourceUrl, string category, int impactScore)
    {
        var validCandidates = candidates
            .Where(candidate =>
                !string.IsNullOrWhiteSpace(sourceUrl)
                && !candidate.Contains(sourceUrl, StringComparison.OrdinalIgnoreCase)
                && !candidate.Contains("Source:", StringComparison.OrdinalIgnoreCase)
                && !candidate.Contains("...", StringComparison.Ordinal)
                && !candidate.Contains("…", StringComparison.Ordinal)
                && FinalQualityGate(candidate, sourceUrl, category, out _))
            .ToList();

        return validCandidates
            .Select(candidate => new
            {
                Text = candidate,
                Length = XPostLengthHelper.GetWeightedLength(candidate)
            })
            .OrderBy(item => IsInTargetLength(item.Length, impactScore) ? 0 : 1)
            .ThenBy(item => Math.Abs(item.Length - TargetLengthCenter(impactScore)))
            .FirstOrDefault()?.Text
            ?? string.Empty;
    }

    private static bool IsInTargetLength(int length, int impactScore)
        => impactScore >= 9
            ? length is >= 700 and <= 1200
            : impactScore >= 7
                ? length is >= 550 and <= 900
                : length is >= 450 and <= 800;

    private static int TargetLengthCenter(int impactScore)
        => impactScore >= 9 ? 850 : impactScore >= 7 ? 700 : 600;

    public static bool FinalQualityGate(string tweet, string sourceUrl, string category, out string rejectReason)
    {
        rejectReason = "none";
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            rejectReason = "source_url missing";
            return false;
        }

        if (string.IsNullOrWhiteSpace(tweet))
        {
            rejectReason = "final_x_post empty";
            return false;
        }

        if (tweet.Contains(sourceUrl, StringComparison.OrdinalIgnoreCase))
        {
            rejectReason = "raw source_url should not be printed in final_x_post";
            return false;
        }

        if (tweet.Contains("Source:", StringComparison.OrdinalIgnoreCase))
        {
            rejectReason = "Source label should not appear in final_x_post";
            return false;
        }

        if (tweet.Contains("...", StringComparison.Ordinal) || tweet.Contains("…", StringComparison.Ordinal))
        {
            rejectReason = "contains ellipsis";
            return false;
        }

        if (ContainsMechanicalXLabel(tweet))
        {
            rejectReason = "contains mechanical field label";
            return false;
        }

        var weightedLength = XPostLengthHelper.GetWeightedLength(tweet);
        if (weightedLength < 400)
        {
            rejectReason = $"too short ({weightedLength}/400)";
            return false;
        }

        if (weightedLength > 1200)
        {
            rejectReason = $"too long ({weightedLength}/1200)";
            return false;
        }

        if (CountCompleteSentences(tweet) < 5)
        {
            rejectReason = "fewer than 5 complete sentences";
            return false;
        }

        if (HasUnsafeSentenceEnding(tweet))
        {
            rejectReason = "contains incomplete or unsafe sentence ending";
            return false;
        }

        if (ContainsIncompleteBiggerStory(tweet))
        {
            rejectReason = "bigger story framing lacks explanation";
            return false;
        }

        if (!ContainsWhyItMatters(tweet))
        {
            rejectReason = "missing why it matters";
            return false;
        }

        if (!ContainsMarketImplication(tweet))
        {
            rejectReason = "missing market implication";
            return false;
        }

        if (!ContainsDiscussionQuestion(tweet))
        {
            rejectReason = "missing discussion question";
            return false;
        }

        if (IsHookCategoryMismatch(tweet, category))
        {
            rejectReason = "hook appears mismatched with category";
            return false;
        }

        return true;
    }

    private static int CountCompleteSentences(string value)
        => Regex.Matches(
            UrlRegex().Replace(value, string.Empty),
            @"[A-Za-z0-9$][^.!?\n]{8,}[.!?]",
            RegexOptions.Multiline).Count;

    private static bool ContainsIncompleteBiggerStory(string value)
    {
        var normalized = Regex.Replace(value, @"\s+", " ").Trim();
        return Regex.IsMatch(normalized, @"\b(the bigger story is|this is another sign that)\s*(\.|$)", RegexOptions.IgnoreCase)
            || Regex.IsMatch(normalized, @"\bthe bigger story is the\s*(\.|$)", RegexOptions.IgnoreCase);
    }

    private static bool ContainsWhyItMatters(string value)
        => ContainsAny(value,
            "matters because", "this matters", "why it matters", "the real issue", "the real question",
            "beyond the headline", "far beyond", "operational risk", "policy fight", "access to",
            "because", "if ", "means", "risk", "demand", "spending", "liquidity", "adoption");

    private static bool ContainsMarketImplication(string value)
        => Regex.IsMatch(value, @"\$[A-Z0-9]{1,8}", RegexOptions.IgnoreCase)
            || ContainsAny(value,
                "could matter for", "could shift", "could strengthen", "could weaken", "market", "sector",
                "industry", "adoption", "liquidity", "spending", "risk appetite", "capital flows");

    private static bool ContainsDiscussionQuestion(string value)
        => value.Contains('?', StringComparison.Ordinal);

    private static bool IsHookCategoryMismatch(string value, string category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return false;
        }

        var firstLine = value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        return firstLine.Contains("Web3 headline", StringComparison.OrdinalIgnoreCase)
            && (category.Contains("Bank", StringComparison.OrdinalIgnoreCase)
                || category.Contains("Policy", StringComparison.OrdinalIgnoreCase)
                || category.Contains("Regulation", StringComparison.OrdinalIgnoreCase)
                || category.Contains("Macro", StringComparison.OrdinalIgnoreCase));
    }

    private static string JoinLines(params string[] lines)
        => string.Join('\n', lines.Where(line => !string.IsNullOrWhiteSpace(line) || line == string.Empty))
            .Replace("\n\n\n", "\n\n", StringComparison.Ordinal)
            .Trim();

    private static string BuildAlertTitle(string category)
    {
        if (category.Contains("AI", StringComparison.OrdinalIgnoreCase) || category.Contains("Big Tech", StringComparison.OrdinalIgnoreCase))
        {
            return "🚨 AI Alert";
        }

        if (category.Contains("DeFi", StringComparison.OrdinalIgnoreCase))
        {
            return "🏦 DeFi Alert";
        }

        if (category.Contains("Meme", StringComparison.OrdinalIgnoreCase))
        {
            return "🐸 Meme Alert";
        }

        return "🚨 Market Alert";
    }

    private static string BuildAssetLabel(string category)
    {
        if (category.Contains("AI", StringComparison.OrdinalIgnoreCase) || category.Contains("Big Tech", StringComparison.OrdinalIgnoreCase))
        {
            return "🏷 Related";
        }

        if (category.Contains("DeFi", StringComparison.OrdinalIgnoreCase))
        {
            return "🎯 Protocol / Assets";
        }

        if (category.Contains("Meme", StringComparison.OrdinalIgnoreCase))
        {
            return "🎯 Tokens";
        }

        return "🎯 Assets";
    }

    private static bool HasMeaningfulAssets(IReadOnlyList<string> values)
        => values.Any(value => !value.Equals("none", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("No direct asset impact", StringComparison.OrdinalIgnoreCase));

    private static string NonEmpty(string value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string FormatHashtags(IReadOnlyList<string> hashtags)
        => hashtags.Count == 0 ? string.Empty : string.Join(' ', hashtags.Take(3));

    private static string ReduceHashtags(string hashtagLine, int maxCount)
        => string.IsNullOrWhiteSpace(hashtagLine)
            ? string.Empty
            : string.Join(' ', hashtagLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(maxCount));

    private static string EnsureAiTake(string tradeTake, string category, string marketImpact)
    {
        var cleaned = CleanFinalPostText(tradeTake);
        if (!string.IsNullOrWhiteSpace(cleaned) && cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 12)
        {
            return cleaned;
        }

        if (category.Contains("AI", StringComparison.OrdinalIgnoreCase))
        {
            return "This matters because it can shift platform demand, compute spending, and sentiment toward AI-linked equities.";
        }

        if (category.Contains("Web3", StringComparison.OrdinalIgnoreCase))
        {
            return "This matters if it improves user activity, liquidity, or developer attention across Web3 ecosystems.";
        }

        if (category.Contains("DeFi", StringComparison.OrdinalIgnoreCase))
        {
            return "This matters because protocol activity can change liquidity, fee expectations, and risk appetite across DeFi.";
        }

        if (category.Contains("Meme", StringComparison.OrdinalIgnoreCase))
        {
            return "This matters mainly as a sentiment signal, where liquidity and community momentum can move faster than fundamentals.";
        }

        return $"{marketImpact} market impact depends on whether flows, policy expectations, or asset demand change after this headline.";
    }

    private static string BuildFallbackBottomLine(NewsArticle article, string marketImpact)
        => BuildFallbackBottomLine(article.Category, marketImpact);

    private static string BuildFallbackBottomLine(string category, string marketImpact)
        => category.Contains("AI", StringComparison.OrdinalIgnoreCase)
            ? "The key signal is whether this changes AI demand or spending expectations."
            : category.Contains("Web3", StringComparison.OrdinalIgnoreCase)
                ? "The story matters most if real users and developers follow the signal."
                : category.Contains("DeFi", StringComparison.OrdinalIgnoreCase)
                    ? "The setup is worth watching, but protocol data needs confirmation."
                    : category.Contains("Meme", StringComparison.OrdinalIgnoreCase)
                        ? "Momentum can fade quickly without sustained community liquidity."
                        : $"{marketImpact} implications need confirmation from flows and follow-through.";

    private static string BuildFallbackContrarianAngle(string category, string context)
    {
        if (ContainsAny(context, "bank", "banks", "banking", "debank", "financial system", "JPMorgan", "Bank of America"))
        {
            return "Most people will focus on the banks, but the bigger question is access to the financial system.";
        }

        if (ContainsAny(context, "cyber", "hack", "breach", "ransomware", "security"))
        {
            return "Most people will see this as a security headline, but the market question is future enterprise spending.";
        }

        if (category.Contains("Web3", StringComparison.OrdinalIgnoreCase))
        {
            return "This looks like a Web3 story, but the real question is whether it changes user behavior.";
        }

        if (category.Contains("AI", StringComparison.OrdinalIgnoreCase) || category.Contains("Big Tech", StringComparison.OrdinalIgnoreCase))
        {
            return "Most people will focus on the product headline, but the market may care more about infrastructure demand.";
        }

        if (category.Contains("Macro", StringComparison.OrdinalIgnoreCase))
        {
            return "Most people will focus on the headline number, but the real signal may be liquidity expectations.";
        }

        return "Most people will focus on the headline, but the market may be missing the second-order effect.";
    }

    private static List<string> BuildHashtags(NewsArticle article, IEnumerable<string> assets)
    {
        var text = $"{article.Title} {article.Summary} {article.Category} {article.SourceName}";
        var tags = new List<string>();
        AddTagIf(text, tags, "Anthropic", "#Anthropic");
        AddTagIf(text, tags, "Claude", "#Claude");
        AddTagIf(text, tags, "OpenAI", "#OpenAI");
        AddTagIf(text, tags, "AI", "#AI");
        AddTagIf(text, tags, "cyber", "#Cybersecurity");
        AddTagIf(text, tags, "Bitcoin", "#Bitcoin");
        AddTagIf(text, tags, "BTC", "#BTC");
        AddTagIf(text, tags, "ETF", "#ETF");
        AddTagIf(text, tags, "DeFi", "#DeFi");
        AddTagIf(text, tags, "Aave", "#Aave");
        AddTagIf(text, tags, "yield", "#Yield");
        AddTagIf(text, tags, "meme", "#MemeCoin");
        AddTagIf(text, tags, "PEPE", "#PEPE");
        AddTagIf(text, tags, "DOGE", "#DOGE");
        AddTagIf(text, tags, "Web3", "#Web3");
        AddTagIf(text, tags, "NFT", "#NFT");
        AddTagIf(text, tags, "GameFi", "#GameFi");
        AddTagIf(text, tags, "airdrop", "#Airdrop");
        AddTagIf(text, tags, "Nvidia", "#AI");
        AddTagIf(text, tags, "Microsoft", "#AI");
        AddTagIf(text, tags, "stocks", "#Stocks");
        AddTagIf(text, tags, "Nasdaq", "#Stocks");
        AddTagIf(text, tags, "Fed", "#Macro");
        AddTagIf(text, tags, "CPI", "#Macro");
        AddTagIf(text, tags, "PPI", "#Macro");
        AddTagIf(text, tags, "Treasury", "#Macro");
        AddTagIf(text, tags, "China", "#China");
        AddTagIf(text, tags, "Alibaba", "#China");
        AddTagIf(text, tags, "Tencent", "#China");
        AddTagIf(text, tags, "geopolitical", "#Geopolitics");
        AddTagIf(text, tags, "trade war", "#Geopolitics");
        AddTagIf(text, tags, "chip ban", "#Geopolitics");

        if (article.Category.Contains("Crypto", StringComparison.OrdinalIgnoreCase) || assets.Any(asset => asset.StartsWith('$')))
        {
            tags.Add("#Crypto");
        }

        if (article.Category.Contains("Web3", StringComparison.OrdinalIgnoreCase))
        {
            tags.Add("#Web3");
        }

        return tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
    }

    private static void AddTagIf(string text, List<string> tags, string needle, string tag)
    {
        if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            tags.Add(tag);
        }
    }

    private static string MakeSafeCompleteSentence(string value)
    {
        var cleaned = CleanFinalPostText(value);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return string.Empty;
        }

        var sentence = cleaned.TrimEnd(',', ';', ':');
        if (!sentence.EndsWith(".", StringComparison.Ordinal)
            && !sentence.EndsWith("!", StringComparison.Ordinal)
            && !sentence.EndsWith("?", StringComparison.Ordinal))
        {
            sentence += ".";
        }

        return sentence.Length > 360 || HasUnsafeSentenceEnding(sentence) ? string.Empty : sentence;
    }

    private static bool HasUnsafeSentenceEnding(string value)
    {
        var lines = value
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => Regex.Replace(line, @"\s+", " ").Trim().ToLowerInvariant());
        var unsafeEndings = new[]
        {
            " to.", " from.", " at.", " with.", " for.", " of.", " near.", " back to.",
            " levels.", " after.", " as.", " by.", " to!", " from!", " at!", " with!", " for!",
            " of!", " near!", " back to!", " levels!", " after!", " as!", " to?",
            " from?", " at?", " with?", " for?", " of?", " near?", " back to?",
            " levels?", " after?", " as?", " by?", " by!", " but.", " because.",
            " how cyber.", " web3 is.", " german.", " actors.", " the bigger story is.",
            " this is another sign that.", " but!", " because!", " but?", " because?",
            " the.", " and.", " a.", " an.", " or.", " in.", " the!", " and!", " a!",
            " an!", " or!", " in!", " the?", " and?", " a?", " an?", " or?", " in?",
            " becoming a.", " regulatory pressure easing a.", " commercialization.",
            " across ai.", " across web3.", " across crypto.", " into one commercialization.",
            " for politically.", " and wells.", " when that infrastructure is questioned.",
            " debanking was broader.", " regulatory pressure shifting toward.",
            " crypto has blamed for.", " bigger story is the."
        };

        return lines.Any(line => unsafeEndings.Any(ending => line.EndsWith(ending, StringComparison.Ordinal)))
            || lines.Any(EndsWithIncompleteWord)
            || Regex.IsMatch(value, @"\b(one|a|an|the)\s+commercialization\.", RegexOptions.IgnoreCase)
            || Regex.IsMatch(value, @"\bacross\s+(AI|Web3|crypto)\.", RegexOptions.IgnoreCase)
            || Regex.IsMatch(value, @"\bJPMorgan Chase,\s*Bank of America,\s*and Wells\.", RegexOptions.IgnoreCase)
            || Regex.IsMatch(value, @"Most people focus on .+,\s*but\.", RegexOptions.IgnoreCase);
    }

    private static bool EndsWithIncompleteWord(string line)
    {
        var normalized = Regex.Replace(line.Trim().TrimEnd('.', '!', '?'), @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var lastWord = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        var incompleteWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "the", "and", "or", "but", "because", "with", "for", "to",
            "of", "in", "by", "as", "toward", "while", "when"
        };
        return incompleteWords.Contains(lastWord);
    }

    private static string CompressPhrase(string value, int maxLength)
    {
        var cleaned = CleanText(value).Replace("...", string.Empty).Replace("…", string.Empty).Trim();
        if (cleaned.Length <= maxLength)
        {
            return cleaned.TrimEnd(',', ';', ':');
        }

        var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var selected = new List<string>();
        foreach (var word in words)
        {
            var candidate = selected.Count == 0 ? word : $"{string.Join(' ', selected)} {word}";
            if (candidate.Length > maxLength)
            {
                break;
            }

            selected.Add(word);
        }

        return selected.Count == 0
            ? string.Empty
            : string.Join(' ', selected).TrimEnd('.', ',', ';', ':');
    }

    private static string CompressAssets(IReadOnlyList<string> values, int maxLength)
    {
        if (values.Count == 0)
        {
            return "none";
        }

        var selected = new List<string>();
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            if (value.Equals("No direct asset impact", StringComparison.OrdinalIgnoreCase))
            {
                return "No direct asset impact";
            }

            if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                return "none";
            }

            var candidate = selected.Count == 0 ? value : $"{string.Join(' ', selected)} {value}";
            if (candidate.Length > maxLength)
            {
                break;
            }

            selected.Add(value);
        }

        return selected.Count == 0 ? "none" : string.Join(' ', selected);
    }

    private static string FormatMarketAssets(IReadOnlyList<string> values)
    {
        var selected = values
            .Where(value => !string.IsNullOrWhiteSpace(value)
                && !value.Equals("none", StringComparison.OrdinalIgnoreCase)
                && !value.Equals("No direct asset impact", StringComparison.OrdinalIgnoreCase))
            .Select(value => AssetSymbolMapper.Normalize(value).Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();

        return selected.Count == 0 ? string.Empty : string.Join(' ', selected);
    }

    private static string NormalizeAssetMentions(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value;
        foreach (var (name, symbol) in AssetSymbolMapper.Mappings
            .Where(pair => pair.Value.StartsWith('$'))
            .OrderByDescending(pair => pair.Key.Length))
        {
            var pattern = $@"(?<![$A-Za-z0-9]){Regex.Escape(name)}(?![A-Za-z0-9])";
            normalized = Regex.Replace(normalized, pattern, symbol, RegexOptions.IgnoreCase);
        }

        return normalized;
    }

    private static string SanitizeEnglishBody(string value, int maxLength)
    {
        var cleaned = CleanText(value);
        cleaned = UrlRegex().Replace(cleaned, string.Empty).Trim();
        cleaned = InvestmentAdviceRegex().Replace(cleaned, "watch the implications");
        cleaned = NonEnglishUnsafeRegex().Replace(cleaned, string.Empty);
        cleaned = WhitespaceRegex().Replace(cleaned, " ").Trim();
        return TruncateAtWord(cleaned, maxLength).TrimEnd('.', ',', ';', ':');
    }

    private static string SanitizeFinalPostText(string value, int maxLength)
    {
        var cleaned = CleanFinalPostText(value).TrimEnd(',', ';', ':');
        if (string.IsNullOrWhiteSpace(cleaned) || cleaned.Length <= maxLength)
        {
            return cleaned;
        }

        var sentence = Regex.Matches(cleaned, @"[^.!?\n]+[.!?]", RegexOptions.Multiline)
            .Select(match => match.Value.Trim().TrimEnd(',', ';', ':'))
            .FirstOrDefault(candidate => candidate.Length <= maxLength && !HasUnsafeSentenceEnding(candidate));

        return sentence ?? cleaned;
    }

    private static string CleanFinalPostText(string value)
    {
        var cleaned = CleanText(value);
        cleaned = UrlRegex().Replace(cleaned, string.Empty).Trim();
        cleaned = InvestmentAdviceRegex().Replace(cleaned, "watch the implications");
        cleaned = NonEnglishUnsafeRegex().Replace(cleaned, string.Empty);
        return WhitespaceRegex().Replace(cleaned.Replace("...", string.Empty).Replace("…", string.Empty), " ").Trim();
    }

    private static string CleanText(string value)
    {
        var noHtml = HtmlTagRegex().Replace(value, " ");
        return WhitespaceRegex().Replace(System.Net.WebUtility.HtmlDecode(noHtml), " ").Trim();
    }

    private static string Truncate(string value, int maxLength)
        => TruncateAtWord(value, maxLength);

    private static string TruncateAtWord(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var trimmed = value[..maxLength].TrimEnd();
        var lastSpace = trimmed.LastIndexOf(' ');
        if (lastSpace > Math.Max(12, maxLength / 2))
        {
            trimmed = trimmed[..lastSpace].TrimEnd();
        }

        return trimmed.TrimEnd('.', ',', ';', ':') + "...";
    }

    private static string LimitWords(string value, int maxWords)
    {
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= maxWords ? value : string.Join(' ', words.Take(maxWords)).TrimEnd('.', ',', ';', ':');
    }

    private static string GenerateFallbackHeadline(string title)
    {
        var cleaned = SanitizeFinalPostText(title, 80);
        cleaned = Regex.Replace(cleaned, @"\b(?:says|said|according|report|reports|latest|update)\b", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        return LimitWords(string.IsNullOrWhiteSpace(cleaned) ? "Market News Update" : cleaned, 8);
    }

    private static int Clamp(int value) => Math.Clamp(value, 0, 20);
    private static int ClampTotal(int value) => Math.Clamp(value, 0, 100);

    private static int GetInt(string name, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    private static bool ContainsCjk(string value)
        => value.Any(ch => (ch >= '\u4e00' && ch <= '\u9fff')
            || (ch >= '\u3400' && ch <= '\u4dbf')
            || (ch >= '\uf900' && ch <= '\ufaff'));

    private static bool ShouldStopOpenAiForRun(string primary, string secondary = "")
    {
        var reason = GetOpenAiStopReason(primary) ?? GetOpenAiStopReason(secondary);
        if (reason == null)
        {
            return false;
        }

        if (reason.Contains("ServiceUnavailable", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("system_cpu_overloaded", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("overloaded", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("503", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsEnabled("OPENAI_RETRY_ON_OVERLOAD", defaultValue: false))
            {
                return true;
            }

            return IsEnabled("OPENAI_STOP_ON_SERVICE_UNAVAILABLE", defaultValue: true);
        }

        return true;
    }

    private static string? GetOpenAiStopReason(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.Contains("ServiceUnavailable", StringComparison.OrdinalIgnoreCase))
        {
            return "ServiceUnavailable";
        }

        if (value.Contains("system_cpu_overloaded", StringComparison.OrdinalIgnoreCase))
        {
            return "system_cpu_overloaded";
        }

        if (value.Contains("overloaded", StringComparison.OrdinalIgnoreCase))
        {
            return "overloaded";
        }

        if (value.Contains("503", StringComparison.OrdinalIgnoreCase))
        {
            return "503";
        }

        if (value.Contains("rate_limit", StringComparison.OrdinalIgnoreCase))
        {
            return "rate_limit";
        }

        if (value.Contains("insufficient_user_quota", StringComparison.OrdinalIgnoreCase)
            || value.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || value.Contains("余额不足", StringComparison.OrdinalIgnoreCase))
        {
            return "insufficient_user_quota";
        }

        if (value.Contains("invalid_api_key", StringComparison.OrdinalIgnoreCase))
        {
            return "invalid_api_key";
        }

        if (value.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return "Unauthorized";
        }

        if (value.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
        {
            return "Forbidden";
        }

        return null;
    }

    private static bool IsEnabled(string name, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? defaultValue
            : bool.TryParse(value, out var enabled) ? enabled : defaultValue;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\b(?:buy|sell|hold|short|long|enter|exit)\b|必涨|必跌|稳赚|确定利好|确定利空|抄底|梭哈", RegexOptions.IgnoreCase)]
    private static partial Regex InvestmentAdviceRegex();

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"[\u4e00-\u9fff\u3400-\u4dbf\uf900-\ufaff]")]
    private static partial Regex NonEnglishUnsafeRegex();
}

public sealed record OpenAiChatRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("messages")]
    public required IReadOnlyList<OpenAiChatMessage> Messages { get; init; }

    [JsonPropertyName("temperature")]
    public double Temperature { get; init; }

    [JsonPropertyName("response_format")]
    public OpenAiResponseFormat? ResponseFormat { get; init; }
}

public sealed record OpenAiChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

public sealed record OpenAiResponseFormat
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }
}

public sealed record OpenAiChatResponse
{
    [JsonPropertyName("choices")]
    public List<OpenAiChoice>? Choices { get; init; }
}

public sealed record OpenAiChoice
{
    [JsonPropertyName("message")]
    public OpenAiChatMessage? Message { get; init; }
}

public sealed record GeneratedNewsTweet
{
    [JsonPropertyName("short_headline")]
    public string? ShortHeadline { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("market_impact")]
    public string? MarketImpactSnake { get; init; }

    [JsonPropertyName("market_impact_score")]
    public int MarketImpactScore { get; init; }

    [JsonPropertyName("affected_assets")]
    public List<string>? AffectedAssetsSnake { get; init; }

    [JsonPropertyName("trade_take")]
    public string? TradeTake { get; init; }

    [JsonPropertyName("bottom_line")]
    public string? BottomLineSnake { get; init; }

    [JsonPropertyName("bottomLine")]
    public string? BottomLineCamel { get; init; }

    [JsonIgnore]
    public string? BottomLine => BottomLineSnake ?? BottomLineCamel;

    [JsonPropertyName("contrarian_angle")]
    public string? ContrarianAngleSnake { get; init; }

    [JsonPropertyName("contrarianAngle")]
    public string? ContrarianAngleCamel { get; init; }

    [JsonIgnore]
    public string? ContrarianAngle => ContrarianAngleSnake ?? ContrarianAngleCamel;

    [JsonPropertyName("why_it_matters")]
    public string? WhyItMattersSnake { get; init; }

    [JsonPropertyName("whyItMatters")]
    public string? WhyItMattersCamel { get; init; }

    [JsonIgnore]
    public string? WhyItMatters => WhyItMattersSnake ?? WhyItMattersCamel;

    [JsonPropertyName("market_implication")]
    public string? MarketImplicationSnake { get; init; }

    [JsonPropertyName("marketImplication")]
    public string? MarketImplicationCamel { get; init; }

    [JsonIgnore]
    public string? MarketImplication => MarketImplicationSnake ?? MarketImplicationCamel;

    [JsonPropertyName("market_context")]
    public string? MarketContextSnake { get; init; }

    [JsonPropertyName("marketContext")]
    public string? MarketContextCamel { get; init; }

    [JsonIgnore]
    public string? MarketContext => MarketContextSnake ?? MarketContextCamel;

    [JsonPropertyName("web3_segment")]
    public string? Web3SegmentSnake { get; init; }

    [JsonPropertyName("web3Segment")]
    public string? Web3SegmentCamel { get; init; }

    [JsonIgnore]
    public string? Web3Segment => Web3SegmentSnake ?? Web3SegmentCamel;

    [JsonPropertyName("confidence_score")]
    public int ConfidenceScore { get; init; }

    [JsonPropertyName("final_x_post")]
    public string? FinalXPost { get; init; }

    [JsonPropertyName("tweet")]
    public string? Tweet { get; init; }

    [JsonPropertyName("englishTweetBody")]
    public string? EnglishTweetBody { get; init; }

    [JsonPropertyName("chineseBrief")]
    public string? ChineseBrief { get; init; }

    [JsonPropertyName("aiSummary")]
    public string? AiSummary { get; init; }

    [JsonPropertyName("marketImpact")]
    public string? MarketImpactCamel { get; init; }

    [JsonIgnore]
    public string? MarketImpact => MarketImpactSnake ?? MarketImpactCamel;

    [JsonPropertyName("affectedAssets")]
    public List<string>? AffectedAssetsCamel { get; init; }

    [JsonIgnore]
    public List<string>? AffectedAssets => AffectedAssetsSnake ?? AffectedAssetsCamel;

    [JsonPropertyName("winners")]
    public List<string>? Winners { get; init; }

    [JsonPropertyName("losers")]
    public List<string>? Losers { get; init; }

    [JsonPropertyName("aiConfidenceScore")]
    public int AiConfidenceScore { get; init; }

    [JsonPropertyName("oneLineImpact")]
    public string? OneLineImpact { get; init; }

    [JsonPropertyName("timeliness")]
    public int Timeliness { get; init; }

    [JsonPropertyName("investmentImpact")]
    public int InvestmentImpact { get; init; }

    [JsonPropertyName("controversy")]
    public int Controversy { get; init; }

    [JsonPropertyName("globalAttention")]
    public int GlobalAttention { get; init; }

    [JsonPropertyName("virality")]
    public int Virality { get; init; }

    [JsonPropertyName("totalScore")]
    public int TotalScore { get; init; }

    [JsonPropertyName("isBreaking")]
    public bool IsBreaking { get; init; }

    [JsonPropertyName("rationale")]
    public string? Rationale { get; init; }
}
