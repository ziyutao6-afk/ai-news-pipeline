namespace AutoTweetRss.Services;

public static class NewsFeedOptions
{
    public static IReadOnlyList<NewsFeedSource> FromEnvironment()
    {
        var configured = Environment.GetEnvironmentVariable("NEWS_RSS_FEEDS");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultFeeds;
        }

        var feeds = new List<NewsFeedSource>();
        var rows = configured.Split(
            ['\n', '\r', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var row in rows)
        {
            var parts = row.Split('|', 3, StringSplitOptions.TrimEntries);
            if (parts.Length != 3 ||
                string.IsNullOrWhiteSpace(parts[0]) ||
                string.IsNullOrWhiteSpace(parts[1]) ||
                string.IsNullOrWhiteSpace(parts[2]))
            {
                continue;
            }

            feeds.Add(new NewsFeedSource(parts[0], parts[1], parts[2]));
        }

        return feeds.Count > 0 ? feeds : DefaultFeeds;
    }

    private static readonly IReadOnlyList<NewsFeedSource> DefaultFeeds =
    [
        new("AI News", "MIT Technology Review AI", "https://www.technologyreview.com/topic/artificial-intelligence/feed"),
        new("AI News", "VentureBeat AI", "https://venturebeat.com/category/ai/feed"),
        new("Crypto News", "CoinDesk", "https://www.coindesk.com/arc/outboundfeeds/rss/"),
        new("Crypto News", "Cointelegraph", "https://cointelegraph.com/rss"),
        new("Web3 News", "Cointelegraph Web3", "https://cointelegraph.com/rss"),
        new("Web3 News", "Decrypt", "https://decrypt.co/feed"),
        new("Web3 News", "Crypto Briefing", "https://cryptobriefing.com/feed/"),
        new("Web3 News", "Consensys", "https://consensys.io/rssfeeds"),
        new("Web3 News", "AirdropAlert", "https://airdropalert.com/feed/rssfeed"),
        new("Web3 News", "The Defiant", "https://thedefiant.io/feed"),
        new("Web3 News", "DL News", "https://www.dlnews.com/"),
        new("Policy / Regulation", "Reuters Regulatory News", "https://www.reutersagency.com/feed/?best-topics=regulatory-news&post_type=best"),
        new("Policy / Regulation", "SEC Press Releases", "https://www.sec.gov/news/pressreleases.rss"),
        new("Policy / Regulation", "U.S. Treasury Press Releases", "https://home.treasury.gov/news/press-releases/rss"),
        new("Market Risk", "Federal Reserve Press Releases", "https://www.federalreserve.gov/feeds/press_all.xml")
    ];
}
