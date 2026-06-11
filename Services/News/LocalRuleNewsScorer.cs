namespace AutoTweetRss.Services;

public static class LocalRuleNewsScorer
{
    private static readonly string[] PriorityKeywords =
    [
        "OpenAI", "Anthropic", "Nvidia", "Apple", "Microsoft", "Google", "Meta", "Tesla",
        "Bitcoin", "Ethereum", "Solana", "Ripple", "XRP", "ETF", "SEC", "Fed", "Trump", "China", "EU", "AI",
        "AMD", "Broadcom", "Palantir", "Oracle", "SMCI", "Coinbase", "Strategy", "Robinhood",
        "Binance", "BlackRock", "MicroStrategy",
        "Alibaba", "Tencent", "Baidu", "PDD", "BYD", "Xiaomi", "Nasdaq", "S&P 500",
        "Aave", "Maker", "Pendle", "Ethena", "Uniswap", "Curve", "Balancer",
        "DOGE", "Dogecoin", "PEPE", "WIF", "BONK", "FLOKI", "meme coin", "memecoin",
        "CPI", "PPI", "nonfarm", "payroll", "rate cut", "rate hike", "Treasury yield",
        "dollar index", "gold", "oil", "chip ban", "export controls", "trade war",
        "geopolitical", "military conflict", "energy security", "supply chain",
        "US election", "Congress", "DOJ", "EU regulation", "China policy",
        "football", "World Cup", "Champions League", "Premier League",
        "新能源", "互联网", "地产", "消费", "制造业", "军事", "冲突", "能源", "贸易", "外交", "世界杯", "欧冠", "英超",
        "web3", "nft", "dao", "gamefi", "socialfi", "wallet", "airdrop", "on-chain",
        "layer2", "zk", "rollup", "identity", "opensea", "blur", "magic eden",
        "immutable", "ronin", "polygon", "base", "arbitrum", "optimism", "starknet", "zksync", "blast",
        "芯片", "制裁", "监管", "诉讼", "禁令", "漏洞", "黑客", "勒索软件",
        "ransomware", "hack", "breach", "vulnerability", "lawsuit", "ban", "sanction",
        "regulation", "regulatory", "antitrust", "tariff", "crypto", "chip"
    ];

    private static readonly string[] PolicyKeywords =
    [
        "SEC", "Fed", "Trump", "China", "EU", "Congress", "DOJ", "US election", "election", "regulation", "regulatory", "policy",
        "ban", "sanction", "lawsuit", "antitrust", "tariff", "chip ban", "export controls",
        "trade war", "geopolitical", "military conflict", "energy security", "supply chain",
        "监管", "政策", "制裁", "禁令", "诉讼"
    ];

    private static readonly string[] AiTechKeywords =
    [
        "OpenAI", "Anthropic", "Nvidia", "Microsoft", "Google", "Meta", "Apple", "Tesla",
        "AMD", "Broadcom", "Palantir", "Oracle", "SMCI", "Tesla AI",
        "AI", "artificial intelligence", "chip", "semiconductor", "芯片", "人工智能"
    ];

    private static readonly string[] CryptoKeywords =
    [
        "Bitcoin", "Ethereum", "Solana", "Ripple", "XRP", "ETF", "crypto", "stablecoin", "token", "Coinbase", "Binance",
        "BlackRock", "MicroStrategy", "加密", "比特币", "以太坊"
    ];

    private static readonly string[] Web3Keywords = Web3NewsClassifier.Keywords;

    private static readonly string[] CyberKeywords =
    [
        "vulnerability", "ransomware", "hack", "breach", "cyber", "CISA", "漏洞", "黑客", "勒索软件", "网络安全"
    ];

    private static readonly string[] DiscountKeywords =
    [
        "deal", "sale", "discount", "coupon", "review", "hands-on", "trailer", "movie", "game",
        "minor update", "small feature", "feature update", "patch notes", "折扣", "促销", "优惠", "测评", "评测", "娱乐"
    ];

    private static readonly string[] TrustedSources =
    [
        "Reuters", "Bloomberg", "CNBC", "The Verge", "TechCrunch", "CoinDesk", "Cointelegraph",
        "Federal Reserve", "SEC", "IMF", "MIT Technology Review", "VentureBeat",
        "Decrypt", "Crypto Briefing", "Consensys", "AirdropAlert", "The Defiant", "DL News"
    ];

    public static bool ShouldConsider(NewsArticle article)
    {
        var text = BuildSearchText(article);
        return MatchesAny(text, PriorityKeywords)
            || MatchesAny(article.SourceName, TrustedSources)
            || IsStrategicCategory(article.Category);
    }

    public static NewsScore Score(NewsArticle article, DateTimeOffset now)
    {
        var text = BuildSearchText(article);
        var ageHours = Math.Max(0, (now - article.PublishedAt).TotalHours);

        var timeliness = ageHours <= 2 ? 20
            : ageHours <= 6 ? 18
            : ageHours <= 12 ? 15
            : ageHours <= 24 ? 12
            : ageHours <= 48 ? 8
            : 4;

        var sourceBoost = MatchesAny(article.SourceName, TrustedSources) ? 8 : 0;
        var priorityHits = CountMatches(text, PriorityKeywords);
        var policyHits = CountMatches(text, PolicyKeywords);
        var aiTechHits = CountMatches(text, AiTechKeywords);
        var cryptoHits = CountMatches(text, CryptoKeywords);
        var cyberHits = CountMatches(text, CyberKeywords);
        var web3Hits = CountMatches(text, Web3Keywords);
        var discountHits = CountMatches(text, DiscountKeywords);
        var web3AlphaBoost = Web3NewsClassifier.AlphaBoost(text);

        var categoryBoost = IsStrategicCategory(article.Category) ? 8 : 0;
        var impact = Math.Clamp(7 + sourceBoost + categoryBoost + (priorityHits * 3) + (policyHits * 2) + (cryptoHits * 2) + (web3Hits * 2) + web3AlphaBoost, 0, 20);
        var controversy = Math.Clamp(5 + (policyHits * 4) + (cyberHits * 4), 0, 20);
        var global = Math.Clamp(6 + sourceBoost + (priorityHits * 2) + (policyHits * 2) + (aiTechHits * 2) + web3Hits, 0, 20);
        var virality = Math.Clamp(5 + (priorityHits * 2) + (cyberHits * 3) + (cryptoHits * 2) + (web3Hits * 2) + web3AlphaBoost, 0, 20);

        var penalty = discountHits * 10;
        var total = Math.Clamp(timeliness + impact + controversy + global + virality - penalty, 0, 100);
        var rationale = $"LocalRuleScore: keywordHits={priorityHits}, web3Hits={web3Hits}, web3AlphaBoost={web3AlphaBoost}, sourceBoost={sourceBoost}, categoryBoost={categoryBoost}, discountPenalty={penalty}";

        return new NewsScore
        {
            Timeliness = timeliness,
            InvestmentImpact = impact,
            Controversy = controversy,
            GlobalAttention = global,
            Virality = virality,
            Total = total,
            IsBreaking = total >= 90 && (policyHits > 0 || cyberHits > 0 || cryptoHits > 0 || web3Hits > 0),
            Rationale = rationale
        };
    }

    private static string BuildSearchText(NewsArticle article)
        => $"{article.Title} {article.Summary} {article.Category} {article.SourceName}";

    private static bool IsStrategicCategory(string category)
        => category.Contains("AI", StringComparison.OrdinalIgnoreCase)
            || category.Contains("Crypto", StringComparison.OrdinalIgnoreCase)
            || category.Contains("Policy", StringComparison.OrdinalIgnoreCase)
            || category.Contains("Regulation", StringComparison.OrdinalIgnoreCase)
            || category.Contains("Market", StringComparison.OrdinalIgnoreCase)
            || category.Contains("Stocks", StringComparison.OrdinalIgnoreCase)
            || category.Contains("Macro", StringComparison.OrdinalIgnoreCase)
            || category.Contains("Global Impact", StringComparison.OrdinalIgnoreCase)
            || category.Contains("Big Tech", StringComparison.OrdinalIgnoreCase)
            || category.Contains("Web3", StringComparison.OrdinalIgnoreCase)
            || category.Contains("DeFi", StringComparison.OrdinalIgnoreCase)
            || category.Contains("Meme", StringComparison.OrdinalIgnoreCase)
            || category.Contains("Sports", StringComparison.OrdinalIgnoreCase)
            || category.Contains("China", StringComparison.OrdinalIgnoreCase)
            || category.Contains("World", StringComparison.OrdinalIgnoreCase);

    private static bool MatchesAny(string text, IReadOnlyList<string> keywords)
        => keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static int CountMatches(string text, IReadOnlyList<string> keywords)
        => keywords.Count(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
}
