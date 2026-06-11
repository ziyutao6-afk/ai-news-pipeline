namespace AutoTweetRss.Services;

public sealed record NewsFeedSource(
    string Category,
    string Name,
    string Url);

public sealed record NewsArticle
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required string Link { get; init; }
    public required string Category { get; init; }
    public required string SourceName { get; init; }
    public string? ImageUrl { get; init; }
    public DateTimeOffset PublishedAt { get; init; }
}

public sealed record NewsScore
{
    public int Timeliness { get; init; }
    public int InvestmentImpact { get; init; }
    public int Controversy { get; init; }
    public int GlobalAttention { get; init; }
    public int Virality { get; init; }
    public int Total { get; init; }
    public bool IsBreaking { get; init; }
    public string Rationale { get; init; } = string.Empty;
}

public sealed record NewsTweetPlan
{
    public required NewsArticle Article { get; init; }
    public required string Tweet { get; init; }
    public required NewsScore Score { get; init; }
    public string EnglishTweetBody { get; init; } = string.Empty;
    public string ChineseBrief { get; init; } = string.Empty;
    public NewsMarketAnalysis MarketAnalysis { get; init; } = new();
    public string FinalXPost { get; init; } = string.Empty;
    public string? ImageSource { get; init; }
    public string? ImageUrl { get; init; }
    public string? ImagePathOrUrl { get; init; }
    public string? ImageStatus { get; init; }
    public string? XDraftIntentUrl { get; init; }
    public string? PendingPostId { get; init; }
    public bool IsTemplateTweet { get; init; }
}

public sealed record NewsMarketAnalysis
{
    public string ShortHeadline { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string AiSummary { get; init; } = string.Empty;
    public string MarketImpact { get; init; } = "Neutral";
    public int MarketImpactScore { get; init; } = 5;
    public List<string> AffectedAssets { get; init; } = [];
    public List<string> Winners { get; init; } = [];
    public List<string> Losers { get; init; } = [];
    public List<string> Hashtags { get; init; } = [];
    public string TradeTake { get; init; } = string.Empty;
    public string BottomLine { get; init; } = string.Empty;
    public string ContrarianAngle { get; init; } = string.Empty;
    public string WhyItMatters { get; init; } = string.Empty;
    public string MarketImplication { get; init; } = string.Empty;
    public string MarketContext { get; init; } = string.Empty;
    public string Web3Segment { get; init; } = string.Empty;
    public int ConfidenceScore { get; init; } = 5;
    public int AiConfidenceScore { get; init; } = 5;
    public string OneLineImpact { get; init; } = string.Empty;
}

public sealed record PendingNewsXPost
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string ShortHeadline { get; init; }
    public required string Source { get; init; }
    public required string SourceUrl { get; init; }
    public string? ImageUrl { get; init; }
    public string? LocalImagePath { get; init; }
    public int LocalScore { get; init; }
    public required string Summary { get; init; }
    public required string MarketImpact { get; init; }
    public int MarketImpactScore { get; init; }
    public List<string> AffectedAssets { get; init; } = [];
    public List<string> Winners { get; init; } = [];
    public List<string> Losers { get; init; } = [];
    public required string TradeTake { get; init; }
    public int ConfidenceScore { get; init; }
    public required NewsMarketAnalysis AiAnalysis { get; init; }
    public required string FinalXPost { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string Status { get; init; } = PendingNewsXPostStatus.Pending;
    public string? ErrorMessage { get; init; }
    public string? FailureReason { get; init; }
    public DateTimeOffset? PostedAt { get; init; }
}

public static class PendingNewsXPostStatus
{
    public const string Pending = "pending";
    public const string Posted = "posted";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}

public sealed record PendingNewsXPostState
{
    public List<PendingNewsXPost> Items { get; init; } = [];
}

public sealed record NewsTweetGenerationResult
{
    public NewsTweetPlan? Plan { get; init; }
    public bool OpenAiAttempted { get; init; }
    public int OpenAiCallCount { get; init; }
    public int OpenAiSuccessfulCallCount { get; init; }
    public int OpenAiFailedCallCount { get; init; }
    public bool StopOpenAiForRun { get; init; }
    public bool RetryableLanguageFailure { get; init; }
    public string? FailureReason { get; init; }
    public string? StopReason { get; init; }
}

public sealed record OpenAiRunStats
{
    public int AttemptedCalls { get; init; }
    public int SuccessfulCalls { get; init; }
    public int FailedCalls { get; init; }
    public string StoppedReason { get; init; } = string.Empty;
    public int SkippedCandidatesDueToFailure { get; init; }
    public bool PartialSuccess { get; init; }
}

public sealed record NewsAutomationState
{
    public string DateKey { get; init; } = string.Empty;
    public int StandardPostsToday { get; init; }
    public int BreakingExtraPostsToday { get; init; }
    public List<string> EvaluatedArticleHashes { get; init; } = [];
    public List<string> PostedArticleHashes { get; init; } = [];
}
