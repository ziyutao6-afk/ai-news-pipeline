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
                Plan = CreateTemplatePlan(article, score),
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

        try
        {
            var result = await SendOpenAiRequestAsync(article, includeScoring: false, cancellationToken);
            return result.Plan == null ? result : result with { Plan = result.Plan with { Score = score } };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenAI tweet generation failed for {Title}", article.Title);
            return new NewsTweetGenerationResult
            {
                OpenAiAttempted = true,
                OpenAiCallCount = 1,
                OpenAiFailedCallCount = 1,
                StopOpenAiForRun = GetOpenAiStopReason(ex.Message) != null,
                FailureReason = ex.Message,
                StopReason = GetOpenAiStopReason(ex.Message)
            };
        }
    }

    public async Task<NewsTweetPlan> CreateTweetPlanAsync(NewsArticle article, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
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
                new OpenAiChatMessage("system", BuildSystemPrompt(includeScoring)),
                new OpenAiChatMessage("user", BuildUserPrompt(article))
            ]
        });

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var reason = $"OpenAI request failed: {response.StatusCode}. {Truncate(CleanText(payload), 500)}";
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
                FailureReason = $"OpenAI returned JSON without englishTweetBody. Content: {Truncate(CleanText(json), 500)}"
            };
        }

        var bodyMax = GetInt("ENGLISH_TWEET_BODY_MAX_CHARS", 220);
        var briefMax = GetInt("CHINESE_BRIEF_MAX_CHARS", 160);
        var englishBody = SanitizeEnglishBody(generated.EnglishTweetBody, bodyMax);
        if (string.IsNullOrWhiteSpace(englishBody) || ContainsCjk(englishBody))
        {
            return new NewsTweetGenerationResult
            {
                OpenAiAttempted = true,
                OpenAiCallCount = 1,
                OpenAiFailedCallCount = 1,
                RetryableLanguageFailure = true,
                FailureReason = "OpenAI returned invalid English tweet body"
            };
        }

        var analysis = BuildMarketAnalysis(generated, article);
        var finalTweet = BuildFinalTweet(englishBody, article.Link);
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
                Tweet = finalTweet,
                EnglishTweetBody = englishBody,
                ChineseBrief = TruncateAtWord(CleanText(generated.ChineseBrief ?? string.Empty), briefMax),
                MarketAnalysis = analysis,
                FinalXPost = finalTweet,
                Score = score
            },
            OpenAiAttempted = true,
            OpenAiCallCount = 1,
            OpenAiSuccessfulCallCount = 1
        };
    }

    private static string BuildSystemPrompt(bool includeScoring)
    {
        var scoring = includeScoring
            ? """
              Score the story using five 0-20 fields: timeliness, investmentImpact, controversy, globalAttention, virality.
              totalScore must be 0-100.
              isBreaking should be true only for unusually urgent, high-impact news.
              """
            : """
              Do not rescore the story. The app already selected it with local rules.
              """;

        return $$"""
        You write simple English X post drafts for an AI, crypto, policy, and market-risk news review workflow.
        Return only valid JSON.
        Use only the provided title, summary, source, timestamp, category, and link.
        Do not imitate any person, account, influencer, trader, or publication style.
        Do not write debate hooks or multi-post drafts.
        Do not give investment advice or tell readers to buy, sell, hold, short, or enter trades.
        englishTweetBody is for X publishing. It must be plain English, no URL, 220 characters or fewer.
        chineseBrief is for Telegram review only. It must be Simplified Chinese, 160 Chinese characters or fewer.
        short_headline must be 8 English words or fewer.
        summary must be one neutral English sentence.
        market_impact must be exactly Bullish, Bearish, or Neutral.
        market_impact_score must be 1-10.
        affected_assets should include only obvious assets or companies from the article; use ["No direct asset impact"] if none.
        {{scoring}}
        JSON schema:
        {
          "short_headline": "short neutral headline",
          "summary": "one neutral sentence",
          "market_impact": "Bullish|Bearish|Neutral",
          "market_impact_score": 1,
          "affected_assets": ["BTC"],
          "englishTweetBody": "short English tweet text without URL",
          "chineseBrief": "中文摘要",
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
    }

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
        var score = BuildLocalFallbackScore(article, reason);
        return CreateTemplatePlan(article, score) with { IsTemplateTweet = false };
    }

    private static NewsTweetPlan CreateTemplatePlan(NewsArticle article, NewsScore score)
    {
        var englishBody = SanitizeEnglishBody($"{article.Category}: {CleanText(article.Title)}", GetInt("ENGLISH_TWEET_BODY_MAX_CHARS", 220));
        var finalTweet = BuildFinalTweet(englishBody, article.Link);
        return new NewsTweetPlan
        {
            Article = article,
            Tweet = finalTweet,
            EnglishTweetBody = englishBody,
            ChineseBrief = $"本地模板推文，仅用于测试审核流程：{CleanText(article.Title)}",
            MarketAnalysis = CreateFallbackMarketAnalysis(article),
            FinalXPost = finalTweet,
            Score = score,
            IsTemplateTweet = true
        };
    }

    private static NewsScore BuildLocalFallbackScore(NewsArticle article, string reason)
    {
        var ageHours = Math.Max(0, (DateTimeOffset.UtcNow - article.PublishedAt).TotalHours);
        var timeliness = ageHours <= 3 ? 18 : ageHours <= 12 ? 14 : ageHours <= 24 ? 10 : 6;
        var text = $"{article.Title} {article.Summary}";
        var impact = KeywordScore(text, ["Fed", "SEC", "regulation", "policy", "AI", "Bitcoin", "Ethereum", "OpenAI", "Nvidia"]);
        var controversy = KeywordScore(text, ["probe", "lawsuit", "hack", "breach", "fraud", "ban", "sanction"]);
        var global = KeywordScore(text, ["US", "EU", "China", "global", "Treasury", "Federal Reserve"]);
        var virality = KeywordScore(text, ["surge", "plunge", "record", "warning", "launches", "emergency"]);
        var total = ClampTotal(timeliness + impact + controversy + global + virality);
        return new NewsScore
        {
            Timeliness = timeliness,
            InvestmentImpact = impact,
            Controversy = controversy,
            GlobalAttention = global,
            Virality = virality,
            Total = total,
            IsBreaking = total >= 90,
            Rationale = $"Fallback scoring used: {reason}"
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

    private static NewsMarketAnalysis BuildMarketAnalysis(GeneratedNewsTweet generated, NewsArticle article)
    {
        var affectedAssets = CleanAssetList((generated.AffectedAssets ?? []).Concat(AssetSymbolMapper.ExtractFromText($"{article.Title} {article.Summary}")));
        var summary = string.IsNullOrWhiteSpace(generated.Summary) ? CleanText(article.Summary) : generated.Summary;
        return new NewsMarketAnalysis
        {
            ShortHeadline = LimitWords(CleanText(generated.ShortHeadline ?? article.Title), 8),
            Summary = TruncateAtWord(CleanText(summary), 180),
            AiSummary = TruncateAtWord(CleanText(summary), 180),
            MarketImpact = NormalizeMarketImpact(generated.MarketImpact),
            MarketImpactScore = Math.Clamp(generated.MarketImpactScore == 0 ? 5 : generated.MarketImpactScore, 1, 10),
            AffectedAssets = affectedAssets,
            Winners = ["none"],
            Losers = ["none"],
            Hashtags = [],
            ConfidenceScore = 5,
            AiConfidenceScore = 5
        };
    }

    private static NewsMarketAnalysis CreateFallbackMarketAnalysis(NewsArticle article)
    {
        var text = $"{article.Title} {article.Summary}";
        var assets = AssetSymbolMapper.ExtractFromText(text);
        return new NewsMarketAnalysis
        {
            ShortHeadline = LimitWords(CleanText(article.Title), 8),
            Summary = TruncateAtWord(CleanText(article.Summary), 180),
            AiSummary = TruncateAtWord(CleanText(article.Summary), 180),
            MarketImpact = "Neutral",
            MarketImpactScore = 5,
            AffectedAssets = CleanAssetList(assets),
            Winners = ["none"],
            Losers = ["none"],
            Hashtags = [],
            ConfidenceScore = 5,
            AiConfidenceScore = 5
        };
    }

    public static string BuildFinalTweet(string englishTweetBody, string originalUrl)
    {
        var body = SanitizeEnglishBody(englishTweetBody, GetInt("ENGLISH_TWEET_BODY_MAX_CHARS", 220));
        return SanitizeTweet($"{body} {originalUrl}".Trim(), originalUrl);
    }

    public static string BuildFinalXPost(string title, string englishTweetBody, NewsMarketAnalysis analysis, string sourceUrl, string category = "")
        => BuildFinalTweet(englishTweetBody, sourceUrl);

    public static bool IsSafeEnglishTweet(string tweet, string originalUrl)
    {
        return !string.IsNullOrWhiteSpace(tweet)
            && !string.IsNullOrWhiteSpace(originalUrl)
            && !ContainsCjk(tweet)
            && !ContainsMechanicalXLabel(tweet)
            && tweet.Contains(originalUrl, StringComparison.OrdinalIgnoreCase)
            && XPostLengthHelper.GetWeightedLength(tweet) <= MaxTweetLength;
    }

    public static bool FinalQualityGate(string tweet, string sourceUrl, string category, out string rejectReason)
    {
        rejectReason = "none";
        if (string.IsNullOrWhiteSpace(tweet))
        {
            rejectReason = "tweet empty";
            return false;
        }

        if (string.IsNullOrWhiteSpace(sourceUrl) || !tweet.Contains(sourceUrl, StringComparison.OrdinalIgnoreCase))
        {
            rejectReason = "source_url missing from tweet";
            return false;
        }

        if (ContainsCjk(tweet))
        {
            rejectReason = "tweet contains non-English characters";
            return false;
        }

        if (ContainsMechanicalXLabel(tweet))
        {
            rejectReason = "tweet contains mechanical field label";
            return false;
        }

        var weightedLength = XPostLengthHelper.GetWeightedLength(tweet);
        if (weightedLength > MaxTweetLength)
        {
            rejectReason = $"tweet too long ({weightedLength}/{MaxTweetLength})";
            return false;
        }

        return true;
    }

    private static string SanitizeTweet(string tweet, string link)
    {
        var cleaned = CleanText(tweet);
        cleaned = InvestmentAdviceRegex().Replace(cleaned, "watch the implications");

        if (!string.IsNullOrWhiteSpace(link) && !cleaned.Contains(link, StringComparison.OrdinalIgnoreCase))
        {
            cleaned = $"{cleaned} {link}";
        }

        if (XPostLengthHelper.GetWeightedLength(cleaned) <= MaxTweetLength)
        {
            return cleaned;
        }

        var linkBudget = string.IsNullOrWhiteSpace(link) ? 0 : XPostLengthHelper.GetWeightedLength(link) + 1;
        var textBudget = Math.Max(40, MaxTweetLength - linkBudget);
        var withoutLink = cleaned.Replace(link, string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        var shortened = TruncateAtWord(withoutLink, textBudget).TrimEnd('.', ',', ';', ':');
        return string.IsNullOrWhiteSpace(link) ? shortened : $"{shortened} {link}";
    }

    private static bool ContainsMechanicalXLabel(string value)
    {
        var banned = new[]
        {
            "Impact:", "Score:", "Confidence:", "Assets:", "Source:"
        };
        return banned.Any(label => value.Contains(label, StringComparison.OrdinalIgnoreCase));
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

    private static List<string> CleanAssetList(IEnumerable<string>? values)
    {
        var cleaned = values?
            .Select(value => AssetSymbolMapper.Normalize(value))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList() ?? [];
        return cleaned.Count == 0 ? ["No direct asset impact"] : cleaned;
    }

    private static int KeywordScore(string text, IReadOnlyList<string> keywords)
    {
        var matches = keywords.Count(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        return Math.Clamp(8 + (matches * 3), 0, 20);
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

        return trimmed.TrimEnd('.', ',', ';', ':');
    }

    private static string LimitWords(string value, int maxWords)
    {
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= maxWords ? value : string.Join(' ', words.Take(maxWords)).TrimEnd('.', ',', ';', ':');
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

        if (value.Contains("quota", StringComparison.OrdinalIgnoreCase))
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

    [JsonPropertyName("marketImpact")]
    public string? MarketImpactCamel { get; init; }

    [JsonIgnore]
    public string? MarketImpact => MarketImpactSnake ?? MarketImpactCamel;

    [JsonPropertyName("market_impact_score")]
    public int MarketImpactScore { get; init; }

    [JsonPropertyName("affected_assets")]
    public List<string>? AffectedAssetsSnake { get; init; }

    [JsonPropertyName("affectedAssets")]
    public List<string>? AffectedAssetsCamel { get; init; }

    [JsonIgnore]
    public List<string>? AffectedAssets => AffectedAssetsSnake ?? AffectedAssetsCamel;

    [JsonPropertyName("englishTweetBody")]
    public string? EnglishTweetBody { get; init; }

    [JsonPropertyName("chineseBrief")]
    public string? ChineseBrief { get; init; }

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
