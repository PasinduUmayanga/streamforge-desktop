namespace StreamForge.Infrastructure.Extraction;

public static class HeaderSanitizer
{
    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Cookie",
        "Set-Cookie",
        "Proxy-Authorization"
    };

    public static IReadOnlyDictionary<string, string> Filter(IReadOnlyDictionary<string, string> headers)
    {
        return headers
            .Where(pair => !SensitiveHeaders.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }
}
