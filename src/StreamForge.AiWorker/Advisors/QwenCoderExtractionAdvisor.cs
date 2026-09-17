using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StreamForge.Core.Interfaces;
using StreamForge.Core.Models;

namespace StreamForge.AiWorker.Advisors;

public sealed class QwenCoderExtractionAdvisor(
    HttpClient httpClient,
    ILogger<QwenCoderExtractionAdvisor> logger) : IAiExtractionAdvisor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AiExtractionSuggestion?> AdviseAsync(
        ExtractionDiagnostics diagnostics,
        StreamExtractionOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.EnableLocalAiAdvisor || diagnostics.Responses.Count == 0)
        {
            return null;
        }

        if (!options.LocalAiEndpoint.IsLoopback)
        {
            logger.LogWarning("The AI advisor endpoint was rejected because it is not a loopback address.");
            return null;
        }

        var endpoint = new Uri(options.LocalAiEndpoint, "/api/chat");
        var payload = new
        {
            model = options.LocalAiModel,
            stream = false,
            format = CreateOutputSchema(),
            options = new { temperature = 0 },
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = """
                        You are Qwen3-Coder-Next acting as a bounded classifier for sanitized web-player
                        response metadata. Select only an observationId and jsonPointer that appear exactly
                        in the supplied data. Never invent a URL, header, token, script, command, decryption
                        instruction, authentication bypass, or CAPTCHA bypass. A candidate is useful only
                        when its field is likely to contain an authorized HLS, DASH, or direct MP4 source.
                        Use observationId 0 and an empty jsonPointer when no candidate exists.
                        """
                },
                new
                {
                    role = "user",
                    content = JsonSerializer.Serialize(new
                    {
                        deterministicFailure = diagnostics.FailureReason.ToString(),
                        blobPlayerObserved = diagnostics.BlobPlayerObserved,
                        timedOut = diagnostics.TimedOut,
                        responses = diagnostics.Responses
                    }, JsonOptions)
                }
            }
        };

        try
        {
            using var response = await httpClient.PostAsJsonAsync(endpoint, payload, JsonOptions, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Local AI advisor returned HTTP {StatusCode}.", (int)response.StatusCode);
                return null;
            }

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("message", out var message)
                || !message.TryGetProperty("content", out var contentElement)
                || contentElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return ParseSuggestion(contentElement.GetString());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Local AI advisor timed out.");
            return null;
        }
        catch (HttpRequestException exception)
        {
            logger.LogInformation(exception, "Local AI advisor is unavailable.");
            return null;
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Local AI advisor returned invalid JSON.");
            return null;
        }
    }

    internal static AiExtractionSuggestion? ParseSuggestion(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        var observationId = GetInt32(root, "observationId");
        var pointer = GetString(root, "jsonPointer") ?? string.Empty;
        var confidence = Math.Clamp(GetDouble(root, "confidence"), 0, 1);

        Enum.TryParse(GetString(root, "expectedType"), ignoreCase: true, out MediaSourceType expectedType);
        Enum.TryParse(GetString(root, "failureReason"), ignoreCase: true, out AnalysisFailureReason failureReason);

        return new AiExtractionSuggestion
        {
            ObservationId = observationId,
            JsonPointer = pointer,
            ExpectedType = expectedType,
            Confidence = confidence,
            FailureReason = failureReason,
            Explanation = (GetString(root, "explanation") ?? string.Empty).Trim()
        };
    }

    private static object CreateOutputSchema() => new
    {
        type = "object",
        properties = new
        {
            observationId = new { type = "integer" },
            jsonPointer = new { type = "string" },
            expectedType = new { type = "string", @enum = new[] { "Unknown", "Hls", "Dash", "Mp4" } },
            confidence = new { type = "number", minimum = 0, maximum = 1 },
            failureReason = new
            {
                type = "string",
                @enum = Enum.GetNames<AnalysisFailureReason>()
            },
            explanation = new { type = "string" }
        },
        required = new[]
        {
            "observationId", "jsonPointer", "expectedType", "confidence", "failureReason", "explanation"
        },
        additionalProperties = false
    };

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int GetInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : 0;

    private static double GetDouble(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetDouble(out var value)
            ? value
            : 0;
}
