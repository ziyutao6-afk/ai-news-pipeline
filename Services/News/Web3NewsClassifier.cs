namespace AutoTweetRss.Services;

public static class Web3NewsClassifier
{
    public const string Category = "Web3 News";

    public static readonly string[] Keywords =
    [
        "web3", "nft", "dao", "gamefi", "socialfi", "creator economy", "wallet",
        "airdrop", "on-chain", "layer2", "layer 2", "zk", "rollup", "identity",
        "decentralized social", "farcaster", "lens protocol", "friend.tech",
        "opensea", "blur", "magic eden", "immutable", "ronin", "polygon",
        "base", "arbitrum", "optimism", "starknet", "zksync", "blast"
    ];

    public static bool IsWeb3(NewsArticle article)
        => IsWeb3($"{article.Title} {article.Summary} {article.Category} {article.SourceName}");

    public static bool IsWeb3(string text)
        => Keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    public static string Segment(string text)
    {
        if (ContainsAny(text, "airdrop")) return "Airdrop";
        if (ContainsAny(text, "gamefi", "gaming", "immutable", "ronin")) return "GameFi";
        if (ContainsAny(text, "dao", "governance")) return "DAO";
        if (ContainsAny(text, "socialfi", "decentralized social", "farcaster", "lens protocol", "friend.tech")) return "SocialFi";
        if (ContainsAny(text, "wallet", "metamask")) return "Wallet";
        if (ContainsAny(text, "layer2", "layer 2", "rollup", "zk", "polygon", "base", "arbitrum", "optimism", "starknet", "zksync", "blast")) return "L2";
        if (ContainsAny(text, "identity", "did")) return "Identity";
        if (ContainsAny(text, "nft", "opensea", "blur", "magic eden")) return "NFT";
        return "Web3";
    }

    public static int AlphaBoost(string text)
    {
        var boost = 0;
        if (ContainsAny(text, "airdrop")) boost += 3;
        if (ContainsAny(text, "token launch", "launches token", "new token")) boost += 3;
        if (ContainsAny(text, "opensea", "blur", "magic eden", "nft marketplace")) boost += 2;
        if (ContainsAny(text, "wallet integration", "wallet support", "wallet")) boost += 2;
        if (ContainsAny(text, "layer2", "layer 2", "l2 ecosystem", "rollup", "base", "arbitrum", "optimism", "starknet", "zksync")) boost += 2;
        if (ContainsAny(text, "gamefi user", "gaming users", "user growth")) boost += 2;
        if (ContainsAny(text, "dao funding", "dao governance", "governance vote", "treasury")) boost += 1;
        if (ContainsAny(text, "partnership", "partners with", "major brand")) boost += 2;
        return Math.Min(boost, 10);
    }

    public static string BuildContext(string segment)
        => segment switch
        {
            "NFT" => "The update affects NFT marketplace liquidity, creator royalties, or collector demand.",
            "DAO" => "The news may influence DAO treasury, governance, or community coordination.",
            "GameFi" => "The story touches Web3 gaming adoption, token utility, or player growth.",
            "SocialFi" => "The update affects decentralized social graphs, creator networks, or community distribution.",
            "Wallet" => "Wallet changes can shift onboarding, custody, and retail access across Web3.",
            "Airdrop" => "Airdrops can drive short-term user activity and ecosystem speculation.",
            "L2" => "Layer2 ecosystem updates can affect transaction activity, app migration, and fee demand.",
            "Identity" => "Identity infrastructure can shape trust, access, and reputation across Web3 apps.",
            _ => "The story may affect Web3 user adoption, community activity, or ecosystem sentiment."
        };

    private static bool ContainsAny(string text, params string[] needles)
        => needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));
}
