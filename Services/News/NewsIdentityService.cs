using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoTweetRss.Services;

public static partial class NewsIdentityService
{
    public static string BuildStableHash(NewsArticle article)
    {
        var key = string.IsNullOrWhiteSpace(article.Link)
            ? $"{article.SourceName}|{Normalize(article.Title)}"
            : NormalizeUrl(article.Link);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return Normalize(url);
        }

        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty,
            Query = string.Empty
        };

        return builder.Uri.ToString().TrimEnd('/').ToLowerInvariant();
    }

    private static string Normalize(string value)
    {
        var normalized = WhitespaceRegex().Replace(value.Trim().ToLowerInvariant(), " ");
        return normalized;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
