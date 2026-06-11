using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Services;

public sealed class NewsPendingPostStore
{
    private const string StateFileName = "news-pending-x-posts.json";
    private const int MaxStoredItems = 200;
    public const string StateStoragePath = "Azure Blob state: news-pending-x-posts.json";

    private readonly ILogger<NewsPendingPostStore> _logger;
    private readonly StateTrackingService _stateTrackingService;

    public NewsPendingPostStore(
        ILogger<NewsPendingPostStore> logger,
        StateTrackingService stateTrackingService)
    {
        _logger = logger;
        _stateTrackingService = stateTrackingService;
    }

    public static string BuildId(NewsArticle article)
    {
        var raw = $"{article.Link}|{article.Title}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    public async Task SavePendingAsync(IReadOnlyList<PendingNewsXPost> pendingPosts)
    {
        if (pendingPosts.Count == 0)
        {
            return;
        }

        var state = await LoadStateAsync();
        var byId = state.Items.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var post in pendingPosts)
        {
            if (byId.TryGetValue(post.Id, out var existing)
                && string.Equals(existing.Status, PendingNewsXPostStatus.Posted, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Pending X post {PostId} is already posted; keeping posted status.", post.Id);
                continue;
            }

            byId[post.Id] = post;
        }

        await SaveStateAsync(new PendingNewsXPostState
        {
            Items = byId.Values
                .OrderByDescending(item => item.CreatedAt)
                .Take(MaxStoredItems)
                .ToList()
        });
    }

    public async Task<PendingNewsXPost?> GetAsync(string id)
    {
        var state = await LoadStateAsync();
        return state.Items.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<PendingNewsXPost?> UpdateStatusAsync(string id, string status, string? failureReason = null)
    {
        var state = await LoadStateAsync();
        var updated = default(PendingNewsXPost);
        var items = state.Items.Select(item =>
        {
            if (!string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }

            updated = item with
            {
                Status = status,
                ErrorMessage = failureReason,
                FailureReason = failureReason,
                PostedAt = string.Equals(status, PendingNewsXPostStatus.Posted, StringComparison.OrdinalIgnoreCase)
                    ? DateTimeOffset.UtcNow
                    : item.PostedAt
            };
            return updated;
        }).ToList();

        if (updated == null)
        {
            return null;
        }

        await SaveStateAsync(state with { Items = items });
        return updated;
    }

    private async Task<PendingNewsXPostState> LoadStateAsync()
        => await _stateTrackingService.GetStateAsync<PendingNewsXPostState>(StateFileName)
            ?? new PendingNewsXPostState();

    private async Task SaveStateAsync(PendingNewsXPostState state)
        => await _stateTrackingService.SetStateAsync(state, StateFileName);
}
