using System.ServiceModel.Syndication;
using System.Net;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Services;

public class RssFeedService
{
    private readonly ILogger<RssFeedService> _logger;
    private readonly HttpClient _httpClient;

    public RssFeedService(ILogger<RssFeedService> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
    }

    public async Task<List<ReleaseEntry>> GetNonPreReleaseEntriesAsync(string feedUrl, bool isSdkFeed = false)
    {
        var entries = new List<ReleaseEntry>();

        try
        {
            _logger.LogInformation("Fetching RSS feed from {FeedUrl}", feedUrl);

            using var stream = await _httpClient.GetStreamAsync(feedUrl);
            using var reader = XmlReader.Create(stream);
            var feed = SyndicationFeed.Load(reader);

            foreach (var item in feed.Items)
            {
                var title = item.Title?.Text ?? string.Empty;
                var content = (item.Content as TextSyndicationContent)?.Text ?? string.Empty;
                var link = item.Links.FirstOrDefault()?.Uri?.ToString() ?? string.Empty;
                var id = item.Id ?? string.Empty;
                var updated = item.LastUpdatedTime;

                // SDK posts should only use stable mainline releases (vX.Y.Z).
                // This excludes beta, preview, and package/submodule titles such as go/*, rust/*, and Java releases.
                if (isSdkFeed)
                {
                    if (!IsMainlineStableRelease(title) || IsPreRelease(title, content))
                    {
                        _logger.LogDebug("Skipping non-mainline SDK release: {Title}", title);
                        continue;
                    }
                }
                else
                {
                    // Original CLI filtering
                    if (IsPreRelease(title, content))
                    {
                        _logger.LogDebug("Skipping pre-release: {Title}", title);
                        continue;
                    }
                }

                entries.Add(new ReleaseEntry
                {
                    Id = id,
                    Title = title,
                    Content = content,
                    Link = link,
                    Updated = updated
                });

                _logger.LogDebug("Found stable release: {Title}", title);
            }

            _logger.LogInformation("Found {Count} non-pre-release entries", entries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching RSS feed from {FeedUrl}", feedUrl);
        }

        return entries;
    }

    public async Task<List<NewsArticle>> GetNewsArticlesAsync(IReadOnlyList<NewsFeedSource> sources)
    {
        var articles = new List<NewsArticle>();

        foreach (var source in sources)
        {
            try
            {
                _logger.LogInformation("Fetching {Category} RSS feed from {SourceName}: {FeedUrl}",
                    source.Category, source.Name, source.Url);

                using var stream = await _httpClient.GetStreamAsync(source.Url);
                using var reader = XmlReader.Create(stream);
                var feed = SyndicationFeed.Load(reader);

                foreach (var item in feed.Items)
                {
                    var title = CleanText(item.Title?.Text ?? string.Empty);
                    var summary = CleanText(
                        (item.Summary as TextSyndicationContent)?.Text
                        ?? (item.Content as TextSyndicationContent)?.Text
                        ?? string.Empty);
                    var link = item.Links.FirstOrDefault(link => link.RelationshipType == "alternate")?.Uri?.ToString()
                        ?? item.Links.FirstOrDefault()?.Uri?.ToString()
                        ?? string.Empty;
                    var publishedAt = item.PublishDate != DateTimeOffset.MinValue
                        ? item.PublishDate
                        : item.LastUpdatedTime;

                    if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link))
                    {
                        continue;
                    }

                    if (publishedAt == DateTimeOffset.MinValue)
                    {
                        publishedAt = DateTimeOffset.UtcNow;
                    }

                    var detectedCategory = DetectNewsCategory(title, summary, source);

                    var article = new NewsArticle
                    {
                        Id = string.IsNullOrWhiteSpace(item.Id) ? link : item.Id,
                        Title = title,
                        Summary = summary,
                        Link = link,
                        Category = detectedCategory,
                        SourceName = source.Name,
                        ImageUrl = ExtractImageUrl(item),
                        PublishedAt = publishedAt.ToUniversalTime()
                    };

                    articles.Add(article);
                    if (string.Equals(detectedCategory, Web3NewsClassifier.Category, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("Category: Web3 News | {Title}", title);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error fetching news RSS feed from {FeedUrl}", source.Url);
            }
        }

        var distinct = articles
            .GroupBy(NewsIdentityService.BuildStableHash)
            .Select(group => group.OrderByDescending(article => article.PublishedAt).First())
            .OrderByDescending(article => article.PublishedAt)
            .ToList();

        _logger.LogInformation("Fetched {ArticleCount} unique news article(s) from {FeedCount} feed(s)",
            distinct.Count, sources.Count);

        return distinct;
    }

    private static bool IsMainlineStableRelease(string title)
    {
        return Regex.IsMatch(title, @"^v\d+\.\d+\.\d+$", RegexOptions.IgnoreCase);
    }

    private static bool IsPreRelease(string title, string content)
    {
        // Check if title has pre-release suffix like "-0", "-1", etc.
        if (Regex.IsMatch(title, @"-\d+$"))
        {
            return true;
        }

        // Check if content starts with "Pre-release" (not just mentions it as a feature)
        // Actual pre-releases have content like "<p>Pre-release 0.0.396-0</p>"
        if (content.TrimStart().StartsWith("<p>Pre-release", StringComparison.OrdinalIgnoreCase) ||
            content.TrimStart().StartsWith("Pre-release ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string CleanText(string value)
    {
        var withoutTags = Regex.Replace(value, "<[^>]+>", " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    private static string? ExtractImageUrl(SyndicationItem item)
    {
        foreach (var link in item.Links)
        {
            var mediaType = link.MediaType ?? string.Empty;
            var uri = link.Uri?.ToString();
            if (!string.IsNullOrWhiteSpace(uri) && mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return uri;
            }
        }

        foreach (var extension in item.ElementExtensions)
        {
            try
            {
                var element = extension.GetObject<XElement>();
                var name = element.Name.LocalName;
                if (!string.Equals(name, "content", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, "thumbnail", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, "enclosure", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var url = element.Attribute("url")?.Value;
                var type = element.Attribute("type")?.Value ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(url)
                    && (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || LooksLikeImageUrl(url)))
                {
                    return url;
                }
            }
            catch
            {
                // Some RSS extensions cannot be materialized as XElement; ignore them.
            }
        }

        return null;
    }

    private static bool LooksLikeImageUrl(string value)
    {
        var path = value.Split('?', '#')[0];
        var ext = Path.GetExtension(path);
        return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static string DetectNewsCategory(string title, string summary, NewsFeedSource source)
    {
        var text = $"{title} {summary} {source.Category} {source.Name}";
        if (Web3NewsClassifier.IsWeb3(text))
        {
            return Web3NewsClassifier.Category;
        }

        if (ContainsAny(text, "Aave", "Maker", "Pendle", "Ethena", "Uniswap", "Curve", "Balancer", "DeFi", "yield protocol"))
        {
            return "DeFi News";
        }

        if (ContainsAny(text, "DOGE", "Dogecoin", "PEPE", "WIF", "BONK", "FLOKI", "meme coin", "memecoin"))
        {
            return "Meme News";
        }

        if (ContainsAny(text, "Fed", "Federal Reserve", "CPI", "PPI", "nonfarm", "payroll", "rate cut",
            "rate hike", "Treasury yield", "bond yield", "DXY", "dollar index", "gold", "oil", "inflation"))
        {
            return "Macro";
        }

        if (ContainsAny(text, "chip ban", "AI regulation", "China-US", "U.S.-China", "trade war", "geopolitical",
            "military conflict", "energy security", "supply chain", "sanctions", "export controls",
            "US election", "Congress", "DOJ", "EU regulation", "China policy", "military", "conflict", "diplomacy"))
        {
            return "Global Impact";
        }

        if (ContainsAny(text, "World Cup", "Champions League", "Premier League", "football", "soccer"))
        {
            return "Sports";
        }

        if (ContainsAny(text, "Nvidia", "Microsoft", "Apple", "Meta", "Amazon", "Google", "Tesla", "AMD",
            "Broadcom", "Palantir", "Oracle", "SMCI", "Coinbase", "Strategy", "Robinhood", "Alibaba",
            "Tencent", "Baidu", "PDD", "BYD", "Xiaomi", "Nasdaq", "S&P 500"))
        {
            return "Stocks";
        }

        return source.Category;
    }

    private static bool ContainsAny(string text, params string[] needles)
        => needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));
}

public class ReleaseEntry
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public required string Content { get; set; }
    public required string Link { get; set; }
    public DateTimeOffset Updated { get; set; }
}
