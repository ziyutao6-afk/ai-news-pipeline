using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoTweetRss.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Functions;

public sealed class TelegramXPostCallbackFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ILogger<TelegramXPostCallbackFunction> _logger;
    private readonly NewsPendingPostStore _pendingPostStore;
    private readonly TwitterApiClient _twitterApiClient;
    private readonly TelegramNotificationService _telegramNotificationService;

    public TelegramXPostCallbackFunction(
        ILogger<TelegramXPostCallbackFunction> logger,
        NewsPendingPostStore pendingPostStore,
        TwitterApiClient twitterApiClient,
        TelegramNotificationService telegramNotificationService)
    {
        _logger = logger;
        _pendingPostStore = pendingPostStore;
        _twitterApiClient = twitterApiClient;
        _telegramNotificationService = telegramNotificationService;
    }

    [Function("TelegramXPostCallback")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "telegram/x-callback")] HttpRequestData req)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");

        try
        {
            var update = await JsonSerializer.DeserializeAsync<TelegramCallbackUpdate>(req.Body, JsonOptions);
            var callback = update?.CallbackQuery;
            var callbackId = callback?.Id ?? string.Empty;
            var chatId = callback?.Message?.Chat?.Id?.ToString() ?? string.Empty;
            var data = callback?.Data ?? string.Empty;
            _logger.LogInformation(
                "Telegram callback_query received. callback_data={CallbackData}, pending post storage path={StoragePath}, button mode=callback_query",
                data,
                NewsPendingPostStore.StateStoragePath);

            if (!string.IsNullOrWhiteSpace(callbackId))
            {
                await _telegramNotificationService.AnswerCallbackAsync(callbackId, "Processing...");
            }

            if (TryParseSkipId(data, out var skipPostId))
            {
                await HandleSkipAsync(response, callbackId, chatId, skipPostId);
                return response;
            }

            if (!TryParsePostId(data, out var postId))
            {
                await _telegramNotificationService.AnswerCallbackAsync(callbackId, "Invalid action");
                await response.WriteStringAsync(JsonSerializer.Serialize(new { ok = false, error = "Invalid callback_data" }, JsonOptions));
                return response;
            }

            var post = await _pendingPostStore.GetAsync(postId);
            if (post == null)
            {
                await _telegramNotificationService.AnswerCallbackAsync(callbackId, "Post not found");
                await NotifyChatAsync(chatId, $"Post not found: {postId}");
                await response.WriteStringAsync(JsonSerializer.Serialize(new { ok = false, error = "Post not found", postId }, JsonOptions));
                return response;
            }

            if (!string.Equals(post.Status, PendingNewsXPostStatus.Pending, StringComparison.OrdinalIgnoreCase))
            {
                var message = $"This news is already {post.Status}; duplicate publishing is blocked.";
                await _telegramNotificationService.AnswerCallbackAsync(callbackId, message);
                await NotifyChatAsync(chatId, message);
                await response.WriteStringAsync(JsonSerializer.Serialize(new { ok = true, skipped = true, status = post.Status, postId }, JsonOptions));
                return response;
            }

            var dryRun = IsEnabled("DRY_RUN", defaultValue: true);
            if (dryRun)
            {
                await _pendingPostStore.UpdateStatusAsync(postId, PendingNewsXPostStatus.Posted);
                var message = $"DRY_RUN: simulated X publish for {post.ShortHeadline}";
                await _telegramNotificationService.AnswerCallbackAsync(callbackId, "DRY_RUN simulated");
                await NotifyChatAsync(chatId, message);
                await response.WriteStringAsync(JsonSerializer.Serialize(new { ok = true, dryRun = true, status = PendingNewsXPostStatus.Posted, postId }, JsonOptions));
                return response;
            }

            var published = await PublishPostOrThreadAsync(post);
            if (published)
            {
                await _pendingPostStore.UpdateStatusAsync(postId, PendingNewsXPostStatus.Posted);
                var message = $"Posted to X: {post.ShortHeadline}";
                await _telegramNotificationService.AnswerCallbackAsync(callbackId, "Posted to X");
                await NotifyChatAsync(chatId, message);
                await response.WriteStringAsync(JsonSerializer.Serialize(new { ok = true, status = PendingNewsXPostStatus.Posted, postId }, JsonOptions));
                return response;
            }

            const string failureReason = "X publish failed. Check X_API_ENABLED and Twitter credentials.";
            await _pendingPostStore.UpdateStatusAsync(postId, PendingNewsXPostStatus.Failed, failureReason);
            await _telegramNotificationService.AnswerCallbackAsync(callbackId, "X publish failed");
            await NotifyChatAsync(chatId, $"Failed to post to X: {post.ShortHeadline}\n{failureReason}");
            await response.WriteStringAsync(JsonSerializer.Serialize(new { ok = false, status = PendingNewsXPostStatus.Failed, postId, error = failureReason }, JsonOptions));
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telegram X post callback failed");
            response.StatusCode = HttpStatusCode.InternalServerError;
            await response.WriteStringAsync(JsonSerializer.Serialize(new
            {
                ok = false,
                error = ex.Message
            }, JsonOptions));
            return response;
        }
    }

    [Function("TelegramPostXHttp")]
    public async Task<HttpResponseData> PostFromHttp(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "telegram/post-x/{postId}")] HttpRequestData req,
        string postId)
    {
        _logger.LogInformation(
            "Telegram HTTP button received. action=post_x, pending post id={PostId}, pending post storage path={StoragePath}, button mode=http_endpoint",
            postId,
            NewsPendingPostStore.StateStoragePath);

        var result = await HandlePostActionAsync(postId, chatId: string.Empty);
        return await WriteBrowserResultAsync(req, result);
    }

    [Function("TelegramSkipXHttp")]
    public async Task<HttpResponseData> SkipFromHttp(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "telegram/skip-x/{postId}")] HttpRequestData req,
        string postId)
    {
        _logger.LogInformation(
            "Telegram HTTP button received. action=skip_x, pending post id={PostId}, pending post storage path={StoragePath}, button mode=http_endpoint",
            postId,
            NewsPendingPostStore.StateStoragePath);

        var result = await HandleSkipActionAsync(postId, chatId: string.Empty);
        return await WriteBrowserResultAsync(req, result);
    }

    private async Task NotifyChatAsync(string chatId, string message)
    {
        if (!string.IsNullOrWhiteSpace(chatId))
        {
            await _telegramNotificationService.SendTextAsync(chatId, message);
        }
    }

    private async Task<ActionResult> HandlePostActionAsync(string postId, string chatId)
    {
        var post = await _pendingPostStore.GetAsync(postId);
        if (post == null)
        {
            var message = $"Post not found: {postId}";
            await NotifyChatAsync(chatId, message);
            return new ActionResult(false, message, postId, null);
        }

        if (!string.Equals(post.Status, PendingNewsXPostStatus.Pending, StringComparison.OrdinalIgnoreCase))
        {
            var message = $"Already handled: {post.Status}";
            await NotifyChatAsync(chatId, message);
            return new ActionResult(true, message, postId, post.Status);
        }

        var dryRun = IsEnabled("DRY_RUN", defaultValue: true);
        if (dryRun)
        {
            await _pendingPostStore.UpdateStatusAsync(postId, PendingNewsXPostStatus.Posted);
            var message = $"DRY_RUN: simulated X publish for {post.ShortHeadline}";
            await NotifyChatAsync(chatId, message);
            return new ActionResult(true, message, postId, PendingNewsXPostStatus.Posted);
        }

        var published = await PublishPostOrThreadAsync(post);
        if (published)
        {
            await _pendingPostStore.UpdateStatusAsync(postId, PendingNewsXPostStatus.Posted);
            var message = $"Posted to X: {post.ShortHeadline}";
            await NotifyChatAsync(chatId, message);
            return new ActionResult(true, message, postId, PendingNewsXPostStatus.Posted);
        }

        const string failureReason = "X publish failed. Check X_API_ENABLED and Twitter credentials.";
        await _pendingPostStore.UpdateStatusAsync(postId, PendingNewsXPostStatus.Failed, failureReason);
        var failedMessage = $"Failed to post to X: {post.ShortHeadline}\n{failureReason}";
        await NotifyChatAsync(chatId, failedMessage);
        return new ActionResult(false, failedMessage, postId, PendingNewsXPostStatus.Failed);
    }

    private async Task<bool> PublishPostOrThreadAsync(PendingNewsXPost post)
    {
        var media = BuildMediaList(post);
        if (XPostLengthHelper.FitsWithinLimit(post.FinalXPost, 280))
        {
            return await _twitterApiClient.PostTweetAsync(post.FinalXPost, media);
        }

        var threadParts = SplitIntoThreadParts(post.FinalXPost);
        if (threadParts.Count == 0)
        {
            return false;
        }

        var socialPosts = threadParts
            .Select((text, index) => new SocialMediaPost(index == 0 ? text : text, index == 0 ? media : null))
            .ToList();
        return await _twitterApiClient.PostTweetThreadAsync(socialPosts);
    }

    private async Task<ActionResult> HandleSkipActionAsync(string postId, string chatId)
    {
        var post = await _pendingPostStore.GetAsync(postId);
        if (post == null)
        {
            var message = $"Post not found: {postId}";
            await NotifyChatAsync(chatId, message);
            return new ActionResult(false, message, postId, null);
        }

        if (!string.Equals(post.Status, PendingNewsXPostStatus.Pending, StringComparison.OrdinalIgnoreCase))
        {
            var message = $"Already handled: {post.Status}";
            await NotifyChatAsync(chatId, message);
            return new ActionResult(true, message, postId, post.Status);
        }

        await _pendingPostStore.UpdateStatusAsync(postId, PendingNewsXPostStatus.Skipped);
        var skippedMessage = $"Skipped: {post.ShortHeadline}";
        await NotifyChatAsync(chatId, skippedMessage);
        return new ActionResult(true, skippedMessage, postId, PendingNewsXPostStatus.Skipped);
    }

    private static async Task<HttpResponseData> WriteBrowserResultAsync(HttpRequestData req, ActionResult result)
    {
        var response = req.CreateResponse(result.Ok ? HttpStatusCode.OK : HttpStatusCode.NotFound);
        response.Headers.Add("Content-Type", "text/html; charset=utf-8");
        await response.WriteStringAsync($"""
            <!doctype html>
            <html>
            <head><meta charset="utf-8"><title>Telegram X Action</title></head>
            <body style="font-family:-apple-system,BlinkMacSystemFont,Segoe UI,sans-serif;line-height:1.5;padding:24px;">
            <h2>{WebUtility.HtmlEncode(result.Message)}</h2>
            <p><strong>Post ID:</strong> {WebUtility.HtmlEncode(result.PostId)}</p>
            <p><strong>Status:</strong> {WebUtility.HtmlEncode(result.Status ?? "none")}</p>
            </body>
            </html>
            """);
        return response;
    }

    private async Task HandleSkipAsync(HttpResponseData response, string callbackId, string chatId, string postId)
    {
        var result = await HandleSkipActionAsync(postId, chatId);
        await _telegramNotificationService.AnswerCallbackAsync(callbackId, result.Message);
        await response.WriteStringAsync(JsonSerializer.Serialize(new { ok = result.Ok, status = result.Status, postId, message = result.Message }, JsonOptions));
    }

    private static IReadOnlyList<string>? BuildMediaList(PendingNewsXPost post)
    {
        if (!string.IsNullOrWhiteSpace(post.LocalImagePath) && File.Exists(post.LocalImagePath))
        {
            return [post.LocalImagePath];
        }

        if (!string.IsNullOrWhiteSpace(post.ImageUrl))
        {
            return [post.ImageUrl];
        }

        return null;
    }

    private static List<string> SplitIntoThreadParts(string finalPost)
    {
        var sentences = System.Text.RegularExpressions.Regex
            .Matches(finalPost, @"[^.!?\n]+[.!?](?:\s+#\w+|\s+\$[A-Z0-9]{1,8})*|(?:#[A-Za-z0-9_]+|\$[A-Z0-9]{1,8})(?:\s+(?:#[A-Za-z0-9_]+|\$[A-Z0-9]{1,8}))*", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(match => match.Value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        if (sentences.Count == 0)
        {
            sentences = finalPost
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
        }

        var parts = new List<string>();
        var current = string.Empty;
        foreach (var sentence in sentences)
        {
            var candidate = string.IsNullOrWhiteSpace(current)
                ? sentence
                : $"{current}\n\n{sentence}";
            if (XPostLengthHelper.FitsWithinLimit(candidate, 280))
            {
                current = candidate;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(current))
            {
                parts.Add(current);
                current = sentence;
            }
            else
            {
                return [];
            }
        }

        if (!string.IsNullOrWhiteSpace(current))
        {
            parts.Add(current);
        }

        return parts.Count <= 8 ? parts : [];
    }

    private static bool TryParsePostId(string data, out string postId)
    {
        const string prefix = "post_x:";
        if (data.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            postId = data[prefix.Length..].Trim();
            return postId.Length > 0;
        }

        postId = string.Empty;
        return false;
    }

    private static bool TryParseSkipId(string data, out string postId)
    {
        const string prefix = "skip_x:";
        if (data.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            postId = data[prefix.Length..].Trim();
            return postId.Length > 0;
        }

        postId = string.Empty;
        return false;
    }

    private static bool IsEnabled(string name, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? defaultValue
            : bool.TryParse(value, out var enabled) ? enabled : defaultValue;
    }
}

public sealed record ActionResult(bool Ok, string Message, string PostId, string? Status);

public sealed record TelegramCallbackUpdate
{
    [JsonPropertyName("callback_query")]
    public TelegramCallbackQuery? CallbackQuery { get; init; }
}

public sealed record TelegramCallbackQuery
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("data")]
    public string? Data { get; init; }

    [JsonPropertyName("message")]
    public TelegramCallbackMessage? Message { get; init; }
}

public sealed record TelegramCallbackMessage
{
    [JsonPropertyName("chat")]
    public TelegramCallbackChat? Chat { get; init; }
}

public sealed record TelegramCallbackChat
{
    [JsonPropertyName("id")]
    public long? Id { get; init; }
}
