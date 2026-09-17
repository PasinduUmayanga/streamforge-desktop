using System.Collections.Concurrent;
using System.Text.Json;
using StreamForge.Core.Models;

namespace StreamForge.Infrastructure.Extraction;

internal sealed class ExtractionDiagnosticCollector
{
    private const int MaximumObservations = 20;
    private const int MaximumFieldsPerObservation = 80;
    private const int MaximumDepth = 8;
    private readonly ConcurrentDictionary<int, RawResponseObservation> _responses = new();
    private readonly ConcurrentDictionary<string, string> _hostAliases = new(StringComparer.OrdinalIgnoreCase);
    private int _nextObservationId;
    private int _nextHostAliasId;
    private int _authenticationResponseObserved;
    private int _expiredAuthorizationObserved;
    private int _challengeObserved;
    private int _drmObserved;

    public void ObserveResponse(Uri responseUri, int statusCode, string resourceType, IReadOnlyDictionary<string, string> headers)
    {
        if (!IsRelevantResource(resourceType))
        {
            return;
        }

        if (statusCode == 401)
        {
            Interlocked.Exchange(ref _authenticationResponseObserved, 1);
        }
        else if (statusCode == 403)
        {
            Interlocked.Exchange(ref _expiredAuthorizationObserved, 1);
        }

        if (statusCode is 403 or 429 or 503
            && (HasHeader(headers, "cf-ray")
                || HeaderContains(headers, "server", "cloudflare")
                || HeaderContains(headers, "x-captcha", "required")))
        {
            Interlocked.Exchange(ref _challengeObserved, 1);
        }

        _ = responseUri;
    }

    public void ObserveInspectableResponse(
        Uri responseUri,
        string method,
        int statusCode,
        string resourceType,
        string? contentType,
        string responseBody,
        IReadOnlyDictionary<string, string> requestHeaders)
    {
        if (_responses.Count >= MaximumObservations || string.IsNullOrWhiteSpace(responseBody))
        {
            return;
        }

        DetectProtectedContent(responseBody);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(responseBody);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            var fields = new List<SanitizedResponseField>();
            var resolvedUrls = new Dictionary<string, Uri>(StringComparer.Ordinal);
            FlattenJson(document.RootElement, string.Empty, responseUri, fields, resolvedUrls, 0);
            if (resolvedUrls.Count == 0)
            {
                return;
            }

            var id = Interlocked.Increment(ref _nextObservationId);
            var observation = new RawResponseObservation
            {
                Sanitized = new SanitizedResponseObservation
                {
                    Id = id,
                    Endpoint = SanitizeUrlShape(responseUri),
                    Method = method,
                    StatusCode = statusCode,
                    ResourceType = resourceType,
                    ContentType = contentType,
                    Fields = fields
                },
                ResolvedUrls = resolvedUrls,
                RequestHeaders = HeaderSanitizer.Filter(requestHeaders),
                ResponseUrl = responseUri
            };

            _responses.TryAdd(id, observation);
        }
    }

    public ExtractionDiagnostics CreateDiagnostics(bool blobPlayerObserved, bool timedOut)
    {
        return new ExtractionDiagnostics
        {
            Responses = _responses.Values
                .OrderBy(response => response.Sanitized.Id)
                .Select(response => response.Sanitized)
                .ToArray(),
            FailureReason = DetermineFailureReason(blobPlayerObserved, timedOut),
            BlobPlayerObserved = blobPlayerObserved,
            TimedOut = timedOut
        };
    }

    public bool TryResolveUrl(
        int observationId,
        string jsonPointer,
        out Uri? uri,
        out IReadOnlyDictionary<string, string> headers,
        out Uri? responseUrl)
    {
        uri = null;
        headers = new Dictionary<string, string>();
        responseUrl = null;

        if (!_responses.TryGetValue(observationId, out var observation)
            || !observation.ResolvedUrls.TryGetValue(jsonPointer, out uri))
        {
            return false;
        }

        headers = observation.RequestHeaders;
        responseUrl = observation.ResponseUrl;
        return true;
    }

    private AnalysisFailureReason DetermineFailureReason(bool blobPlayerObserved, bool timedOut)
    {
        if (Volatile.Read(ref _drmObserved) == 1)
        {
            return AnalysisFailureReason.DrmProtected;
        }

        if (Volatile.Read(ref _challengeObserved) == 1)
        {
            return AnalysisFailureReason.BotChallenge;
        }

        if (Volatile.Read(ref _authenticationResponseObserved) == 1)
        {
            return AnalysisFailureReason.AuthenticationRequired;
        }

        if (Volatile.Read(ref _expiredAuthorizationObserved) == 1)
        {
            return AnalysisFailureReason.AuthorizationExpired;
        }

        if (blobPlayerObserved)
        {
            return AnalysisFailureReason.UnsupportedProtocol;
        }

        return timedOut ? AnalysisFailureReason.Timeout : AnalysisFailureReason.NoSupportedMedia;
    }

    private void FlattenJson(
        JsonElement element,
        string pointer,
        Uri responseUri,
        ICollection<SanitizedResponseField> fields,
        IDictionary<string, Uri> resolvedUrls,
        int depth)
    {
        if (fields.Count >= MaximumFieldsPerObservation || depth > MaximumDepth)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    FlattenJson(
                        property.Value,
                        $"{pointer}/{EscapePointerSegment(property.Name)}",
                        responseUri,
                        fields,
                        resolvedUrls,
                        depth + 1);
                    if (fields.Count >= MaximumFieldsPerObservation)
                    {
                        break;
                    }
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray().Take(5))
                {
                    FlattenJson(item, $"{pointer}/{index}", responseUri, fields, resolvedUrls, depth + 1);
                    index++;
                }

                break;

            case JsonValueKind.String:
                var value = element.GetString();
                if (TryResolveHttpUrl(responseUri, value, out var resolved))
                {
                    fields.Add(new SanitizedResponseField
                    {
                        JsonPointer = pointer,
                        ValueType = "url",
                        UrlShape = SanitizeUrlShape(resolved)
                    });
                    resolvedUrls[pointer] = resolved;
                }
                else
                {
                    fields.Add(new SanitizedResponseField { JsonPointer = pointer, ValueType = "string" });
                }

                break;

            default:
                fields.Add(new SanitizedResponseField
                {
                    JsonPointer = pointer,
                    ValueType = element.ValueKind.ToString().ToLowerInvariant()
                });
                break;
        }
    }

    private string SanitizeUrlShape(Uri uri)
    {
        var alias = _hostAliases.GetOrAdd(
            uri.Host,
            _ => $"host-{Interlocked.Increment(ref _nextHostAliasId)}");
        var path = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
        var query = NetworkUrlSanitizer.RedactedQuery(uri);
        return $"{uri.Scheme}://{alias}{path}{query}";
    }

    private static bool TryResolveHttpUrl(Uri responseUri, string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value)
            || !(value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                 || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                 || value.StartsWith("//", StringComparison.Ordinal)
                 || value.StartsWith("/", StringComparison.Ordinal)))
        {
            return false;
        }

        if (!Uri.TryCreate(responseUri, value, out var resolved)
            || (!resolved.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !resolved.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        uri = resolved;
        return true;
    }

    private void DetectProtectedContent(string text)
    {
        string[] indicators =
        [
            "com.widevine.alpha",
            "com.microsoft.playready",
            "skd://",
            "urn:mpeg:dash:mp4protection",
            "SAMPLE-AES"
        ];

        if (indicators.Any(indicator => text.Contains(indicator, StringComparison.OrdinalIgnoreCase)))
        {
            Interlocked.Exchange(ref _drmObserved, 1);
        }
    }

    private static bool IsRelevantResource(string resourceType) =>
        resourceType.Equals("document", StringComparison.OrdinalIgnoreCase)
        || resourceType.Equals("xhr", StringComparison.OrdinalIgnoreCase)
        || resourceType.Equals("fetch", StringComparison.OrdinalIgnoreCase)
        || resourceType.Equals("media", StringComparison.OrdinalIgnoreCase);

    private static bool HasHeader(IReadOnlyDictionary<string, string> headers, string name) =>
        headers.Keys.Any(key => key.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool HeaderContains(
        IReadOnlyDictionary<string, string> headers,
        string name,
        string value) =>
        headers.Any(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)
            && pair.Value.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static string EscapePointerSegment(string value) => value.Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);

    private sealed class RawResponseObservation
    {
        public required SanitizedResponseObservation Sanitized { get; init; }

        public required IReadOnlyDictionary<string, Uri> ResolvedUrls { get; init; }

        public required IReadOnlyDictionary<string, string> RequestHeaders { get; init; }

        public required Uri ResponseUrl { get; init; }
    }
}
