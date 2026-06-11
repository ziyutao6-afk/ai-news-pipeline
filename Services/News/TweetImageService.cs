using System.Net;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Services;

public sealed partial class TweetImageService
{
    private const long MaxImageBytes = 5 * 1024 * 1024;
    private readonly ILogger<TweetImageService> _logger;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, Lazy<Task<TweetImageResult>>> _downloadCache = new(StringComparer.OrdinalIgnoreCase);

    public TweetImageService(ILogger<TweetImageService> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
    }

    public async Task<TweetImageResult> PrepareImageAsync(NewsArticle article, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled("ENABLE_TWEET_IMAGE", defaultValue: true))
        {
            return new TweetImageResult(null, "disabled", "tweet image disabled");
        }

        var imageUrl = article.ImageUrl;
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            imageUrl = await TryFindMetaImageAsync(article.Link, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            var downloaded = await DownloadImageOnceAsync(imageUrl, "rss-or-page-image", cancellationToken);
            if (downloaded.PathOrUrl != null)
            {
                return downloaded;
            }
        }

        var defaultPath = Environment.GetEnvironmentVariable("DEFAULT_TWEET_IMAGE_PATH") ?? "assets/default-news.png";
        var fullDefaultPath = Path.IsPathRooted(defaultPath)
            ? defaultPath
            : Path.Combine(AppContext.BaseDirectory, defaultPath);

        if (File.Exists(fullDefaultPath))
        {
            return new TweetImageResult(fullDefaultPath, "default-image", "using default image");
        }

        _logger.LogInformation("No tweet image available for {Title}; default image missing at {Path}", article.Title, fullDefaultPath);
        return new TweetImageResult(null, "none", "no image available");
    }

    private async Task<string?> TryFindMetaImageAsync(string articleUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(articleUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var match = MetaImageRegex().Match(html);
            if (!match.Success)
            {
                match = MetaImageContentFirstRegex().Match(html);
            }

            if (!match.Success)
            {
                return null;
            }

            var rawUrl = WebUtility.HtmlDecode(match.Groups["url"].Value);
            if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var absolute))
            {
                return absolute.ToString();
            }

            if (Uri.TryCreate(articleUrl, UriKind.Absolute, out var baseUri)
                && Uri.TryCreate(baseUri, rawUrl, out var resolved))
            {
                return resolved.ToString();
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Failed to discover meta image for {ArticleUrl}", articleUrl);
        }

        return null;
    }

    private Task<TweetImageResult> DownloadImageOnceAsync(string imageUrl, string source, CancellationToken cancellationToken)
    {
        var lazy = _downloadCache.GetOrAdd(imageUrl, url => new Lazy<Task<TweetImageResult>>(
            () => TryDownloadImageAsync(url, source, cancellationToken)));
        return lazy.Value;
    }

    private async Task<TweetImageResult> TryDownloadImageAsync(string imageUrl, string source, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(imageUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new TweetImageResult(null, source, $"image download failed: {response.StatusCode}", imageUrl);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0)
            {
                return new TweetImageResult(null, source, "image download was empty", imageUrl);
            }

            if (bytes.LongLength > MaxImageBytes)
            {
                bytes = bytes.Take((int)MaxImageBytes).ToArray();
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? GuessContentType(imageUrl);
            var extension = contentType.ToLowerInvariant() switch
            {
                "image/jpeg" => ".jpg",
                "image/png" => ".png",
                "image/webp" => ".webp",
                _ => Path.GetExtension(imageUrl.Split('?', '#')[0])
            };

            if (!IsSupportedExtension(extension))
            {
                return new TweetImageResult(null, source, $"unsupported image type: {contentType}", imageUrl);
            }

            var filePath = Path.Combine(Path.GetTempPath(), $"auto-tweet-rss-{Guid.NewGuid():N}{extension}");
            await File.WriteAllBytesAsync(filePath, bytes, cancellationToken);
            return new TweetImageResult(filePath, source, $"downloaded from {imageUrl}", imageUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download tweet image from {ImageUrl}", imageUrl);
            return new TweetImageResult(null, source, ex.Message, imageUrl);
        }
    }

    private static string GuessContentType(string url)
    {
        var ext = Path.GetExtension(url.Split('?', '#')[0]);
        return ext.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
    }

    private static bool IsSupportedExtension(string extension)
        => extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);

    private static bool IsEnabled(string name, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? defaultValue
            : bool.TryParse(value, out var enabled) ? enabled : defaultValue;
    }

    [GeneratedRegex("<meta[^>]+(?:property|name)=[\"'](?:og:image|twitter:image)[\"'][^>]+content=[\"'](?<url>[^\"']+)[\"'][^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaImageRegex();

    [GeneratedRegex("<meta[^>]+content=[\"'](?<url>[^\"']+)[\"'][^>]+(?:property|name)=[\"'](?:og:image|twitter:image)[\"'][^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaImageContentFirstRegex();
}

public sealed record TweetImageResult(string? PathOrUrl, string Source, string Status, string? ImageUrl = null);
