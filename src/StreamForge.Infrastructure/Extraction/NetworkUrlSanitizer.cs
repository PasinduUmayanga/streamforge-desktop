namespace StreamForge.Infrastructure.Extraction;

internal static class NetworkUrlSanitizer
{
    public static string ForDisplay(Uri uri)
    {
        if (string.IsNullOrEmpty(uri.Query))
        {
            return uri.GetLeftPart(UriPartial.Path);
        }

        var queryNames = uri.Query[1..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)[0])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => Uri.UnescapeDataString(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => $"{name}=<redacted>");

        var sanitizedQuery = string.Join('&', queryNames);
        return string.IsNullOrEmpty(sanitizedQuery)
            ? uri.GetLeftPart(UriPartial.Path)
            : $"{uri.GetLeftPart(UriPartial.Path)}?{sanitizedQuery}";
    }
}
