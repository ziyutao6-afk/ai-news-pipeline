using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Services;

public sealed class TelegramNotificationService
{
    private const int MaxTelegramMessageLength = 4096;
    private readonly ILogger<TelegramNotificationService> _logger;
    private readonly HttpClient _httpClient;

    public TelegramNotificationService(
        ILogger<TelegramNotificationService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
    }

    public bool IsEnabled => IsEnabledValue("TELEGRAM_ENABLED", defaultValue: false);
    public bool IsReviewMode => IsEnabledValue("TELEGRAM_REVIEW_MODE", defaultValue: true);
    public bool IsConfigured => !string.IsNullOrWhiteSpace(GetToken()) && !string.IsNullOrWhiteSpace(GetChatId());
    public string BotUsername => Environment.GetEnvironmentVariable("TELEGRAM_BOT_USERNAME") ?? string.Empty;

    public async Task<TelegramSendResult> SendReviewAsync(
        IReadOnlyList<NewsTweetPlan> topLocalPlans,
        IReadOnlyList<NewsTweetPlan> generatedPlans,
        OpenAiRunStats openAiRunStats,
        bool skippedOpenAiScoring,
        bool dryRun,
        string? openAiFailureReason,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return new TelegramSendResult(false, "Telegram disabled");
        }

        var token = GetToken();
        var chatId = GetChatId();
        if (string.IsNullOrWhiteSpace(token))
        {
            return new TelegramSendResult(false, "TELEGRAM_BOT_TOKEN missing");
        }

        if (string.IsNullOrWhiteSpace(chatId))
        {
            return new TelegramSendResult(false, "TELEGRAM_CHAT_ID missing. Send a message to the bot, then read getUpdates to find chat.id.");
        }

        var message = BuildReviewSummaryMessage(topLocalPlans, generatedPlans, openAiRunStats, skippedOpenAiScoring, dryRun, openAiFailureReason);
        var summaryResult = await SendTextAsync(token, chatId, message, disableWebPagePreview: true, cancellationToken);
        if (!summaryResult.Sent)
        {
            return summaryResult;
        }

        var reviewCount = 0;
        foreach (var plan in generatedPlans.Take(3))
        {
            var sent = await SendReviewItemAsync(token, chatId, plan, cancellationToken);
            if (sent)
            {
                reviewCount++;
            }
        }

        return new TelegramSendResult(true, $"Telegram review notification sent with {reviewCount} review item(s)");
    }

    public async Task<TelegramSendResult> SendTextAsync(
        string chatId,
        string text,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return new TelegramSendResult(false, "Telegram disabled");
        }

        var token = GetToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return new TelegramSendResult(false, "TELEGRAM_BOT_TOKEN missing");
        }

        return await SendTextAsync(token, chatId, text, disableWebPagePreview: true, cancellationToken);
    }

    public async Task AnswerCallbackAsync(string callbackQueryId, string text, CancellationToken cancellationToken = default)
    {
        var token = GetToken();
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(callbackQueryId))
        {
            return;
        }

        var url = $"https://api.telegram.org/bot{token}/answerCallbackQuery";
        await _httpClient.PostAsJsonAsync(url, new TelegramAnswerCallbackQueryRequest
        {
            CallbackQueryId = callbackQueryId,
            Text = text,
            ShowAlert = false
        }, cancellationToken);
    }

    private static string BuildReviewSummaryMessage(
        IReadOnlyList<NewsTweetPlan> topLocalPlans,
        IReadOnlyList<NewsTweetPlan> generatedPlans,
        OpenAiRunStats openAiRunStats,
        bool skippedOpenAiScoring,
        bool dryRun,
        string? openAiFailureReason)
    {
        var lines = new List<string>
        {
            "AI/Crypto/Policy/Market Risk News Review",
            $"Mode: {BuildModeLabel(dryRun)}",
            $"OpenAI attempted calls: {openAiRunStats.AttemptedCalls}",
            $"OpenAI successful calls: {openAiRunStats.SuccessfulCalls}",
            $"OpenAI failed calls: {openAiRunStats.FailedCalls}",
            $"OpenAI stopped reason: {(string.IsNullOrWhiteSpace(openAiRunStats.StoppedReason) ? "none" : openAiRunStats.StoppedReason)}",
            $"Skipped candidates due to OpenAI failure: {openAiRunStats.SkippedCandidatesDueToFailure}",
            $"Partial success: {openAiRunStats.PartialSuccess}",
            $"Skipped OpenAI scoring: {skippedOpenAiScoring}",
            $"Generated tweets: {generatedPlans.Count}",
            string.Empty,
            "Top local-score news:"
        };

        foreach (var plan in topLocalPlans.Take(3))
        {
            lines.Add($"- {plan.Score.Total}/100 | {plan.Article.Category} | {plan.Article.SourceName} | {plan.Article.Title}");
        }

        if (!string.IsNullOrWhiteSpace(openAiFailureReason))
        {
            lines.Add(string.Empty);
            lines.Add($"OpenAI failure: {openAiFailureReason}");
        }

        var message = string.Join('\n', lines);
        return message.Length <= MaxTelegramMessageLength
            ? message
            : message[..(MaxTelegramMessageLength - 20)].TrimEnd() + "\n...[truncated]";
    }

    private async Task<bool> SendReviewItemAsync(
        string token,
        string chatId,
        NewsTweetPlan plan,
        CancellationToken cancellationToken)
    {
        if (IsEnabledValue("ENABLE_TWEET_IMAGE", defaultValue: true)
            && !string.IsNullOrWhiteSpace(plan.ImagePathOrUrl))
        {
            await SendPhotoAsync(token, chatId, plan, cancellationToken);
        }

        var draftUrl = string.IsNullOrWhiteSpace(plan.XDraftIntentUrl)
            ? BuildXDraftIntentUrl(plan.FinalXPost)
            : plan.XDraftIntentUrl;
        var keyboard = BuildReviewKeyboard(plan.PendingPostId, plan.Article.Link, draftUrl);
        if (!string.IsNullOrWhiteSpace(plan.PendingPostId))
        {
            _logger.LogInformation(
                "Telegram review buttons prepared. button mode=callback_query, pending post id={PendingPostId}, pending post storage path={StoragePath}, callback_data={CallbackData}",
                plan.PendingPostId,
                NewsPendingPostStore.StateStoragePath,
                $"post_x:{plan.PendingPostId} / skip_x:{plan.PendingPostId}");
        }

        var result = await SendTextWithKeyboardFallbackAsync(
            token,
            chatId,
            BuildReviewItemMessage(plan),
            disableWebPagePreview: false,
            cancellationToken,
            keyboard);
        return result.Sent;
    }

    private async Task<bool> SendPhotoAsync(
        string token,
        string chatId,
        NewsTweetPlan plan,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.telegram.org/bot{token}/sendPhoto";
        var caption = BuildPhotoCaption(plan);

        try
        {
            HttpResponseMessage response;
            if (Uri.TryCreate(plan.ImagePathOrUrl, UriKind.Absolute, out var imageUri)
                && (imageUri.Scheme == Uri.UriSchemeHttp || imageUri.Scheme == Uri.UriSchemeHttps))
            {
                response = await _httpClient.PostAsJsonAsync(url, new TelegramSendPhotoRequest
                {
                    ChatId = chatId,
                    Photo = plan.ImagePathOrUrl,
                    Caption = caption
                }, cancellationToken);
            }
            else if (File.Exists(plan.ImagePathOrUrl))
            {
                await using var stream = File.OpenRead(plan.ImagePathOrUrl);
                using var content = new MultipartFormDataContent
                {
                    { new StringContent(chatId), "chat_id" },
                    { new StringContent(caption), "caption" }
                };
                using var imageContent = new StreamContent(stream);
                content.Add(imageContent, "photo", Path.GetFileName(plan.ImagePathOrUrl));
                response = await _httpClient.PostAsync(url, content, cancellationToken);
            }
            else
            {
                _logger.LogInformation("Skipping Telegram image preview because image path is not accessible: {ImagePath}", plan.ImagePathOrUrl);
                return false;
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            _logger.LogWarning("Telegram image preview failed. Status={StatusCode}, Response={Response}", response.StatusCode, payload);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram image preview failed for {Title}", plan.Article.Title);
            return false;
        }
    }

    private async Task<TelegramSendResult> SendTextWithKeyboardFallbackAsync(
        string token,
        string chatId,
        string text,
        bool disableWebPagePreview,
        CancellationToken cancellationToken,
        TelegramInlineKeyboardMarkup? replyMarkup)
    {
        var result = await SendTextAsync(token, chatId, text, disableWebPagePreview, cancellationToken, replyMarkup);
        if (result.Sent || replyMarkup == null)
        {
            return result;
        }

        _logger.LogWarning("Telegram send with inline keyboard failed; falling back to plain text. Error={Error}", result.Message);
        var fallback = await SendTextAsync(token, chatId, text, disableWebPagePreview, cancellationToken);
        return fallback.Sent
            ? new TelegramSendResult(true, $"Telegram message sent without inline keyboard after fallback. Original error: {result.Message}")
            : fallback;
    }

    private async Task<TelegramSendResult> SendTextAsync(
        string token,
        string chatId,
        string text,
        bool disableWebPagePreview,
        CancellationToken cancellationToken,
        TelegramInlineKeyboardMarkup? replyMarkup = null)
    {
        var url = $"https://api.telegram.org/bot{token}/sendMessage";
        var response = await _httpClient.PostAsJsonAsync(url, new TelegramSendMessageRequest
        {
            ChatId = chatId,
            Text = text,
            DisableWebPagePreview = disableWebPagePreview,
            ReplyMarkup = replyMarkup
        }, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Telegram send failed. Status={StatusCode}, Response={Response}", response.StatusCode, payload);
            return new TelegramSendResult(false, $"Telegram send failed: {response.StatusCode}. {payload}");
        }

        return new TelegramSendResult(true, "Telegram message sent");
    }

    private static string BuildModeLabel(bool dryRun)
    {
        if (IsEnabledValue("MANUAL_X_POST_MODE", defaultValue: false))
        {
            return "MANUAL_X_POST_MODE";
        }

        return dryRun ? "DRY_RUN" : "LIVE";
    }

    private static string BuildEnglishTweetText(NewsTweetPlan plan)
    {
        if (!string.IsNullOrWhiteSpace(plan.EnglishTweetBody))
        {
            return $"{plan.EnglishTweetBody.Trim()} {plan.Article.Link}".Trim();
        }

        return plan.Tweet;
    }

    private static string BuildReviewItemMessage(NewsTweetPlan plan)
    {
        var lines = new List<string>
        {
            $"{plan.Score.Total}/100 | {plan.Article.Category} | {plan.Article.SourceName}",
            plan.Article.Title,
            string.Empty,
            "Generated Tweet:",
            plan.FinalXPost,
            "Chinese Brief:",
            plan.ChineseBrief,
            "Original URL:",
            plan.Article.Link,
            "Image:",
            BuildImageLine(plan),
            "Open X Draft:",
            string.IsNullOrWhiteSpace(plan.XDraftIntentUrl) ? BuildXDraftIntentUrl(plan.FinalXPost) : plan.XDraftIntentUrl
        };

        var message = string.Join('\n', lines);
        return message.Length <= MaxTelegramMessageLength
            ? message
            : message[..(MaxTelegramMessageLength - 20)].TrimEnd() + "\n...[truncated]";
    }

    private static string BuildPhotoCaption(NewsTweetPlan plan)
    {
        var caption = $"🖼 {plan.Article.Title}\nSource: {plan.Article.SourceName}";
        return caption.Length <= 1024
            ? caption
            : caption[..1000].TrimEnd() + "\n...[truncated]";
    }

    private static string BuildImageLine(NewsTweetPlan plan)
    {
        var image = string.IsNullOrWhiteSpace(plan.ImagePathOrUrl)
            ? "none"
            : plan.ImagePathOrUrl;
        var sourceUrl = string.IsNullOrWhiteSpace(plan.ImageUrl) ? "no image_url" : plan.ImageUrl;
        return $"{image} ({sourceUrl}; {plan.ImageSource ?? "none"} | {plan.ImageStatus ?? "not prepared"})";
    }

    private static TelegramInlineKeyboardMarkup? BuildReviewKeyboard(string? pendingPostId, string sourceUrl, string draftUrl)
        => string.IsNullOrWhiteSpace(pendingPostId)
            ? null
            : new TelegramInlineKeyboardMarkup
            {
                InlineKeyboard =
                [
                    [
                        new TelegramInlineKeyboardButton
                        {
                            Text = "🚀 Post to X",
                            CallbackData = $"post_x:{pendingPostId}"
                        }
                    ],
                    [
                        new TelegramInlineKeyboardButton
                        {
                            Text = "📝 Open X Draft",
                            Url = draftUrl
                        },
                        new TelegramInlineKeyboardButton
                        {
                            Text = "❌ Skip",
                            CallbackData = $"skip_x:{pendingPostId}"
                        },
                        new TelegramInlineKeyboardButton
                        {
                            Text = "🔗 Open Source",
                            Url = sourceUrl
                        }
                    ]
                ]
            };

    private static string BuildXDraftIntentUrl(string tweetText)
        => $"https://twitter.com/intent/tweet?text={Uri.EscapeDataString(tweetText.Trim())}";

    private static string BuildLocalActionUrl(string action, string pendingPostId)
    {
        var baseUrl = Environment.GetEnvironmentVariable("TELEGRAM_BUTTON_BASE_URL")?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = "http://127.0.0.1:7071/api";
        }

        return $"{baseUrl.TrimEnd('/')}/telegram/{action}/{Uri.EscapeDataString(pendingPostId)}";
    }

    private static string GetToken()
        => Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")?.Trim() ?? string.Empty;

    private static string GetChatId()
        => Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID")?.Trim() ?? string.Empty;

    private static bool IsEnabledValue(string name, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? defaultValue
            : bool.TryParse(value, out var enabled) ? enabled : defaultValue;
    }

    private static int GetInt(string name, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    private static bool IsReviewModeValue()
        => IsEnabledValue("TELEGRAM_REVIEW_MODE", defaultValue: true);
}

public sealed record TelegramSendResult(bool Sent, string Message);

public sealed record TelegramSendMessageRequest
{
    [JsonPropertyName("chat_id")]
    public required string ChatId { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("disable_web_page_preview")]
    public bool DisableWebPagePreview { get; init; }

    [JsonPropertyName("reply_markup")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TelegramInlineKeyboardMarkup? ReplyMarkup { get; init; }
}

public sealed record TelegramSendPhotoRequest
{
    [JsonPropertyName("chat_id")]
    public required string ChatId { get; init; }

    [JsonPropertyName("photo")]
    public required string Photo { get; init; }

    [JsonPropertyName("caption")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Caption { get; init; }

    [JsonPropertyName("reply_markup")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TelegramInlineKeyboardMarkup? ReplyMarkup { get; init; }
}

public sealed record TelegramInlineKeyboardMarkup
{
    [JsonPropertyName("inline_keyboard")]
    public required List<List<TelegramInlineKeyboardButton>> InlineKeyboard { get; init; }
}

public sealed record TelegramInlineKeyboardButton
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("callback_data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallbackData { get; init; }

    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; init; }
}

public sealed record TelegramAnswerCallbackQueryRequest
{
    [JsonPropertyName("callback_query_id")]
    public required string CallbackQueryId { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("show_alert")]
    public bool ShowAlert { get; init; }
}
