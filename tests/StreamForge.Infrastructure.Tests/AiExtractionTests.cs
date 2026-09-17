using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using StreamForge.AiWorker.Advisors;
using StreamForge.Core.Models;
using StreamForge.Infrastructure.Extraction;

namespace StreamForge.Infrastructure.Tests;

public sealed class AiExtractionTests
{
    [Fact]
    public void DiagnosticCollector_ExposesOnlySanitizedStructureButRetainsResolvableUrlInternally()
    {
        var collector = new ExtractionDiagnosticCollector();
        collector.ObserveInspectableResponse(
            new Uri("https://private-player.example/api/source?session=top-secret"),
            "POST",
            200,
            "xhr",
            "application/json",
            """
                {
                  "title": "private title",
                  "payload": {
                    "playback": "https://signed-cdn.example/playback?id=42&token=very-secret"
                  }
                }
                """,
            new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer secret",
                ["Cookie"] = "session=secret",
                ["Referer"] = "https://private-player.example/watch"
            });

        var diagnostics = collector.CreateDiagnostics(blobPlayerObserved: false, timedOut: false);
        var serialized = JsonSerializer.Serialize(diagnostics);
        var observation = Assert.Single(diagnostics.Responses);
        var urlField = Assert.Single(observation.Fields, field => field.ValueType == "url");

        Assert.DoesNotContain("private-player.example", serialized);
        Assert.DoesNotContain("signed-cdn.example", serialized);
        Assert.DoesNotContain("top-secret", serialized);
        Assert.DoesNotContain("very-secret", serialized);
        Assert.DoesNotContain("private title", serialized);
        Assert.Contains("session=<redacted>", observation.Endpoint);
        Assert.Contains("token=<redacted>", urlField.UrlShape);

        Assert.True(collector.TryResolveUrl(
            observation.Id,
            urlField.JsonPointer,
            out var resolved,
            out var headers,
            out _));
        Assert.Equal("https://signed-cdn.example/playback?id=42&token=very-secret", resolved!.AbsoluteUri);
        Assert.DoesNotContain("Authorization", headers.Keys);
        Assert.DoesNotContain("Cookie", headers.Keys);
        Assert.Equal("https://private-player.example/watch", headers["Referer"]);
    }

    [Fact]
    public void DiagnosticCollector_PrefersChallengeClassificationOverGenericForbiddenResponse()
    {
        var collector = new ExtractionDiagnosticCollector();

        collector.ObserveResponse(
            new Uri("https://challenge.example/api"),
            403,
            "document",
            new Dictionary<string, string> { ["server"] = "cloudflare" });

        var diagnostics = collector.CreateDiagnostics(blobPlayerObserved: false, timedOut: false);

        Assert.Equal(AnalysisFailureReason.BotChallenge, diagnostics.FailureReason);
    }

    [Fact]
    public async Task QwenCoderAdvisor_ParsesStructuredSuggestionWithoutSendingPrivateValues()
    {
        string? requestBody = null;
        var responseContent = JsonSerializer.Serialize(new
        {
            message = new
            {
                content = JsonSerializer.Serialize(new
                {
                    observationId = 7,
                    jsonPointer = "/payload/playback",
                    expectedType = "Hls",
                    confidence = 0.91,
                    failureReason = "NoSupportedMedia",
                    explanation = "The playback field resembles an HLS endpoint."
                })
            }
        });
        var handler = new RecordingHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseContent, Encoding.UTF8, "application/json")
            };
        });
        var advisor = new QwenCoderExtractionAdvisor(
            new HttpClient(handler),
            NullLogger<QwenCoderExtractionAdvisor>.Instance);

        var result = await advisor.AdviseAsync(
            CreateDiagnostics(),
            new StreamExtractionOptions
            {
                EnableLocalAiAdvisor = true,
                LocalAiEndpoint = new Uri("http://localhost:11434"),
                LocalAiModel = "test-model"
            },
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(7, result.ObservationId);
        Assert.Equal(MediaSourceType.Hls, result.ExpectedType);
        Assert.Equal(0.91, result.Confidence, 2);
        Assert.NotNull(requestBody);
        Assert.Contains("host-1", requestBody);
        Assert.DoesNotContain("private.example", requestBody);
        Assert.DoesNotContain("secret", requestBody);
    }

    [Fact]
    public async Task QwenCoderAdvisor_ReturnsNullForMalformedModelJson()
    {
        var responseContent = JsonSerializer.Serialize(new
        {
            message = new { content = "not-json" }
        });
        var advisor = new QwenCoderExtractionAdvisor(
            new HttpClient(new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseContent, Encoding.UTF8, "application/json")
            }))),
            NullLogger<QwenCoderExtractionAdvisor>.Instance);

        var result = await advisor.AdviseAsync(
            CreateDiagnostics(),
            new StreamExtractionOptions { EnableLocalAiAdvisor = true },
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task QwenCoderAdvisor_RejectsRemoteEndpointsWithoutMakingARequest()
    {
        var requests = 0;
        var advisor = new QwenCoderExtractionAdvisor(
            new HttpClient(new RecordingHandler(_ =>
            {
                requests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            })),
            NullLogger<QwenCoderExtractionAdvisor>.Instance);

        var result = await advisor.AdviseAsync(
            CreateDiagnostics(),
            new StreamExtractionOptions
            {
                EnableLocalAiAdvisor = true,
                LocalAiEndpoint = new Uri("https://ai.example.com")
            },
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, requests);
    }

    private static ExtractionDiagnostics CreateDiagnostics() => new()
    {
        FailureReason = AnalysisFailureReason.NoSupportedMedia,
        Responses =
        [
            new SanitizedResponseObservation
            {
                Id = 7,
                Endpoint = "https://host-1/api/source?token=<redacted>",
                Method = "GET",
                StatusCode = 200,
                ResourceType = "xhr",
                ContentType = "application/json",
                Fields =
                [
                    new SanitizedResponseField
                    {
                        JsonPointer = "/payload/playback",
                        ValueType = "url",
                        UrlShape = "https://host-2/playback?id=<redacted>"
                    }
                ]
            }
        ]
    };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }
}
