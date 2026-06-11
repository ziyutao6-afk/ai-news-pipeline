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
        new("Policy / Regulation", "White House Briefing Room", "https://www.whitehouse.gov/briefing-room/feed/"),
        new("Policy / Regulation", "ECB Press Releases", "https://www.ecb.europa.eu/rss/press.html"),
        new("Policy / Regulation", "Bank of Japan", "https://www.boj.or.jp/en/rss/whatsnew.xml"),
        new("Macro", "Federal Reserve Press Releases", "https://www.federalreserve.gov/feeds/press_all.xml"),
        new("Macro", "IMF News", "https://www.imf.org/en/News/RSS"),
        new("AI News", "OpenAI News", "https://openai.com/news/rss.xml"),
        new("Global Impact", "NATO News", "https://www.nato.int/cps/en/natohq/rss.xml"),
        new("Stocks", "CNBC Top News", "https://www.cnbc.com/id/100003114/device/rss/rss.html"),
        new("Stocks", "Reuters Business", "https://www.reutersagency.com/feed/?best-topics=business-finance&post_type=best"),
        new("Global Impact", "Reuters World", "https://www.reutersagency.com/feed/?best-topics=world&post_type=best"),
        new("Big Tech", "The Verge", "https://www.theverge.com/rss/index.xml"),
        new("Big Tech", "TechCrunch", "https://techcrunch.com/feed/"),
        new("Sports", "BBC Football", "https://feeds.bbci.co.uk/sport/football/rss.xml")
    ];
}
