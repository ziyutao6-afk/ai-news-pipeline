namespace AutoTweetRss.Functions;

public static class LegacyFunctionGate
{
    public static bool IsEnabled()
    {
        var value = Environment.GetEnvironmentVariable("ENABLE_LEGACY_RELEASE_FUNCTIONS");
        return bool.TryParse(value, out var enabled) && enabled;
    }
}
