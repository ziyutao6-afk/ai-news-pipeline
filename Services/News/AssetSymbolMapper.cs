using System.Text.RegularExpressions;

namespace AutoTweetRss.Services;

public static partial class AssetSymbolMapper
{
    private static readonly Dictionary<string, string> SymbolMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bitcoin"] = "$BTC",
        ["BTC"] = "$BTC",
        ["Ethereum"] = "$ETH",
        ["Ether"] = "$ETH",
        ["ETH"] = "$ETH",
        ["Solana"] = "$SOL",
        ["SOL"] = "$SOL",
        ["Polygon"] = "$POL",
        ["MATIC"] = "$POL",
        ["POL"] = "$POL",
        ["Arbitrum"] = "$ARB",
        ["ARB"] = "$ARB",
        ["Optimism"] = "$OP",
        ["OP"] = "$OP",
        ["Immutable"] = "$IMX",
        ["IMX"] = "$IMX",
        ["Ronin"] = "$RON",
        ["RON"] = "$RON",
        ["ApeCoin"] = "$APE",
        ["APE"] = "$APE",
        ["XRP"] = "$XRP",
        ["Ripple"] = "$XRP",
        ["Dogecoin"] = "$DOGE",
        ["DOGE"] = "$DOGE",
        ["BNB"] = "$BNB",
        ["Aave"] = "$AAVE",
        ["AAVE"] = "$AAVE",
        ["Maker"] = "$MKR",
        ["MKR"] = "$MKR",
        ["Pendle"] = "$PENDLE",
        ["PENDLE"] = "$PENDLE",
        ["Ethena"] = "$ENA",
        ["ENA"] = "$ENA",
        ["Uniswap"] = "$UNI",
        ["UNI"] = "$UNI",
        ["Curve"] = "$CRV",
        ["CRV"] = "$CRV",
        ["Balancer"] = "$BAL",
        ["BAL"] = "$BAL",
        ["PEPE"] = "$PEPE",
        ["WIF"] = "$WIF",
        ["BONK"] = "$BONK",
        ["FLOKI"] = "$FLOKI",
        ["Blur"] = "$BLUR",
        ["LooksRare"] = "$LOOKS",
        ["LOOKS"] = "$LOOKS",
        ["Magic Eden"] = "$ME",
        ["ME"] = "$ME",
        ["Starknet"] = "$STRK",
        ["STRK"] = "$STRK",
        ["zkSync"] = "$ZK",
        ["ZK"] = "$ZK",
        ["LayerZero"] = "$ZRO",
        ["ZRO"] = "$ZRO",
        ["Wormhole"] = "$W",
        ["W"] = "$W",
        ["Base"] = "Base ecosystem",
        ["OpenSea"] = "NFT marketplace",
        ["Lens Protocol"] = "Lens Protocol",
        ["Farcaster"] = "Farcaster",

        ["Coinbase"] = "$COIN",
        ["COIN"] = "$COIN",
        ["MicroStrategy"] = "$MSTR",
        ["Strategy"] = "$MSTR",
        ["MSTR"] = "$MSTR",
        ["Robinhood"] = "$HOOD",
        ["HOOD"] = "$HOOD",
        ["Block"] = "$SQ",
        ["SQ"] = "$SQ",
        ["PayPal"] = "$PYPL",
        ["PYPL"] = "$PYPL",

        ["Meta"] = "$META",
        ["META"] = "$META",
        ["Facebook"] = "$META",
        ["WhatsApp"] = "$META",
        ["Instagram"] = "$META",
        ["Nvidia"] = "$NVDA",
        ["NVDA"] = "$NVDA",
        ["Microsoft"] = "$MSFT",
        ["MSFT"] = "$MSFT",
        ["OpenAI partner"] = "$MSFT",
        ["Google"] = "$GOOGL",
        ["GOOGL"] = "$GOOGL",
        ["Alphabet"] = "$GOOGL",
        ["DeepMind"] = "$GOOGL",
        ["Amazon"] = "$AMZN",
        ["AMZN"] = "$AMZN",
        ["AWS"] = "$AMZN",
        ["Apple"] = "$AAPL",
        ["AAPL"] = "$AAPL",
        ["Tesla"] = "$TSLA",
        ["TSLA"] = "$TSLA",
        ["xAI"] = "$TSLA",
        ["AMD"] = "$AMD",
        ["Advanced Micro Devices"] = "$AMD",
        ["Broadcom"] = "$AVGO",
        ["AVGO"] = "$AVGO",
        ["Palantir"] = "$PLTR",
        ["PLTR"] = "$PLTR",
        ["Oracle"] = "$ORCL",
        ["ORCL"] = "$ORCL",
        ["Salesforce"] = "$CRM",
        ["CRM"] = "$CRM",
        ["SMCI"] = "$SMCI",
        ["Super Micro"] = "$SMCI",
        ["Supermicro"] = "$SMCI",
        ["CrowdStrike"] = "$CRWD",
        ["Palo Alto Networks"] = "$PANW",
        ["Zscaler"] = "$ZS",
        ["Alibaba"] = "$BABA",
        ["Tencent"] = "$TCEHY",
        ["Baidu"] = "$BIDU",
        ["PDD"] = "$PDD",
        ["Pinduoduo"] = "$PDD",
        ["BYD"] = "$BYDDY",
        ["Xiaomi"] = "$XIACY",
        ["Nasdaq"] = "$QQQ",
        ["S&P500"] = "$SPY",
        ["S&P 500"] = "$SPY",
        ["SP500"] = "$SPY",
        ["SPY"] = "$SPY",
        ["XLF"] = "$XLF",
        ["JPMorgan"] = "$JPM",
        ["JPMorgan Chase"] = "$JPM",
        ["JPM"] = "$JPM",
        ["Bank of America"] = "$BAC",
        ["BAC"] = "$BAC",
        ["Wells Fargo"] = "$WFC",
        ["WFC"] = "$WFC",

        ["IBIT"] = "$IBIT",
        ["BlackRock Bitcoin ETF"] = "$IBIT",
        ["FBTC"] = "$FBTC",
        ["Fidelity Bitcoin ETF"] = "$FBTC",
        ["ARKB"] = "$ARKB",
        ["GBTC"] = "$GBTC",
        ["BITB"] = "$BITB",

        ["Gold"] = "$GLD",
        ["Oil"] = "$USO",
        ["US Dollar Index"] = "$DXY",
        ["DXY"] = "$DXY"
    };

    public static IReadOnlyDictionary<string, string> Mappings => SymbolMap;

    public static string Normalize(string value)
    {
        var cleaned = CleanSymbolInput(value);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return string.Empty;
        }

        if (IsCashtag(cleaned))
        {
            return cleaned.ToUpperInvariant();
        }

        var withoutPrefix = cleaned.TrimStart('$', '#');
        return SymbolMap.TryGetValue(withoutPrefix, out var mapped)
            ? mapped
            : cleaned;
    }

    public static List<string> NormalizeList(IEnumerable<string>? values, string emptyValue = "none")
    {
        var normalized = values?
            .Select(Normalize)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList() ?? [];

        if (normalized.Count == 0)
        {
            return [emptyValue];
        }

        return normalized;
    }

    public static List<string> ExtractFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var found = new List<string>();
        foreach (var (name, symbol) in SymbolMap)
        {
            if (ContainsAssetName(text, name))
            {
                found.Add(symbol);
            }
        }

        return found.Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
    }

    private static bool ContainsAssetName(string text, string name)
    {
        var pattern = $@"(?<![A-Za-z0-9]){Regex.Escape(name)}(?![A-Za-z0-9])";
        return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase);
    }

    private static string CleanSymbolInput(string value)
    {
        var cleaned = WhitespacePattern().Replace(value.Trim(), " ");
        cleaned = cleaned.Trim(' ', '.', ',', ';', ':', '(', ')', '[', ']', '{', '}', '"', '\'');
        if (cleaned.Equals("No direct asset impact", StringComparison.OrdinalIgnoreCase)
            || cleaned.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return cleaned.Equals("none", StringComparison.OrdinalIgnoreCase) ? "none" : "No direct asset impact";
        }

        return cleaned;
    }

    private static bool IsCashtag(string value)
        => CashtagPattern().IsMatch(value);

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"^\$[A-Z0-9]{1,8}$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex CashtagPattern();
}
