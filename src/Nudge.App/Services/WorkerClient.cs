using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nudge.Core;

namespace Nudge.Services;

public sealed class WorkerClient : IBackendClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient httpClient;
    private readonly Uri baseUri;

    public WorkerClient(Uri baseUri)
    {
        this.baseUri = baseUri;
        httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120)
        };
    }

    public async Task CheckHealthAsync(CancellationToken cancellationToken)
    {
        AppLogger.Info("Worker /health request started.");
        using var response = await httpClient.GetAsync(new Uri(baseUri, "/health"), cancellationToken);
        AppLogger.Info($"Worker /health response. Status={(int)response.StatusCode}");
        response.EnsureSuccessStatusCode();
    }

    public async Task<WorkerDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        AppLogger.Info("Worker /diagnostics request started.");
        using var response = await httpClient.GetAsync(new Uri(baseUri, "/diagnostics"), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        AppLogger.Info($"Worker /diagnostics response. Status={(int)response.StatusCode} BodyLength={body.Length}");
        response.EnsureSuccessStatusCode();

        return JsonSerializer.Deserialize<WorkerDiagnostics>(body, JsonOptions)
               ?? throw new InvalidOperationException("Worker diagnostics response could not be parsed.");
    }

    public async Task<bool> CheckPointingSelfTestAsync(CancellationToken cancellationToken)
    {
        AppLogger.Info("Worker /pointing-self-test request started.");
        using var response = await httpClient.GetAsync(new Uri(baseUri, "/pointing-self-test"), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        AppLogger.Info($"Worker /pointing-self-test response. Status={(int)response.StatusCode} BodyLength={body.Length}");

        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        using var document = JsonDocument.Parse(body);
        var ok = document.RootElement.TryGetProperty("ok", out var okElement) && okElement.GetBoolean();
        var source = document.RootElement.TryGetProperty("point", out var pointElement) &&
                     pointElement.TryGetProperty("source", out var sourceElement)
            ? sourceElement.GetString()
            : "";
        AppLogger.Info($"Worker exact pointing support. Ok={ok} Source={source}");
        return ok &&
               (string.Equals(source, "element", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "visual-target", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "computer-use", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<string> GetAssemblyAITokenAsync(CancellationToken cancellationToken)
    {
        AppLogger.Info("Worker /transcribe-token request started.");
        using var response = await httpClient.PostAsync(new Uri(baseUri, "/transcribe-token"), null, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        AppLogger.Info($"Worker /transcribe-token response. Status={(int)response.StatusCode} BodyLength={body.Length}");
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("token", out var tokenElement))
        {
            throw new InvalidOperationException("AssemblyAI token response did not include token.");
        }

        return tokenElement.GetString() ?? throw new InvalidOperationException("AssemblyAI token was empty.");
    }

    public async Task<ChatResponse> SendChatAsync(
        string transcript,
        IReadOnlyList<ScreenCapturePayload> images,
        IReadOnlyList<ConversationTurn> conversationHistory,
        CancellationToken cancellationToken)
    {
        AppLogger.Info($"Worker /chat request started. TranscriptLength={transcript.Length} Images={images.Count} History={conversationHistory.Count}");
        var visualTargetCount = images.Sum(image => image.VisualTargets?.Count ?? 0);
        AppLogger.Info($"Worker /chat payload plan. Mode=visual-reticle Targets={visualTargetCount}");
        var payload = new
        {
            transcript,
            images = images.Select(image => new
            {
                label = image.Label,
                mediaType = image.MediaType,
                data = image.Base64Data,
                visualAtlasData = image.VisualAtlasBase64Data,
                screenNumber = image.ScreenNumber,
                screenshotWidthInPixels = image.ScreenshotWidthInPixels,
                screenshotHeightInPixels = image.ScreenshotHeightInPixels,
                displayWidthInPixels = image.DisplayWidthInPixels,
                displayHeightInPixels = image.DisplayHeightInPixels,
                visualTargets = (image.VisualTargets ?? []).Select(target => new
                {
                    id = target.Id,
                    kind = target.Kind,
                    x = target.X,
                    y = target.Y,
                    width = target.Width,
                    height = target.Height,
                    centerX = target.CenterX,
                    centerY = target.CenterY,
                    confidence = target.Confidence,
                    labelHint = target.LabelHint
                })
            }),
            conversationHistory = conversationHistory.Select(turn => new
            {
                userTranscript = turn.UserTranscript,
                assistantResponse = turn.AssistantResponse
            })
        };

        using var response = await httpClient.PostAsJsonAsync(
            new Uri(baseUri, "/chat"),
            payload,
            JsonOptions,
            cancellationToken);

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        AppLogger.Info($"Worker /chat response. Status={(int)response.StatusCode} BodyLength={responseText.Length}");
        response.EnsureSuccessStatusCode();

        return JsonSerializer.Deserialize<ChatResponse>(responseText, JsonOptions)
               ?? throw new InvalidOperationException("Worker chat response could not be parsed.");
    }

    public async Task<LocateResponse> LocateAsync(
        string goal,
        ScreenCapturePayload capture,
        string provider,
        CancellationToken cancellationToken)
    {
        AppLogger.Info(
            $"Worker /locate request started. Provider={provider} GoalLength={goal.Length} " +
            $"Screen={capture.ScreenNumber} Size={capture.ScreenshotWidthInPixels}x{capture.ScreenshotHeightInPixels}");

        using var response = await httpClient.PostAsJsonAsync(
            new Uri(baseUri, "/locate"),
            new
            {
                provider,
                goal,
                screenshotBase64 = capture.Base64Data,
                mimeType = capture.MediaType,
                width = capture.ScreenshotWidthInPixels,
                height = capture.ScreenshotHeightInPixels,
                screenNumber = capture.ScreenNumber
            },
            JsonOptions,
            cancellationToken);

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        AppLogger.Info($"Worker /locate response. Status={(int)response.StatusCode} BodyLength={responseText.Length}");
        response.EnsureSuccessStatusCode();

        var locateResponse = JsonSerializer.Deserialize<LocateResponse>(responseText, JsonOptions)
                             ?? throw new InvalidOperationException("Worker locate response could not be parsed.");

        AppLogger.Info(
            $"Worker /locate parsed. Ok={locateResponse.Ok} Provider={locateResponse.Provider ?? ""} " +
            $"RawX={locateResponse.X?.ToString("0.0") ?? "none"} RawY={locateResponse.Y?.ToString("0.0") ?? "none"} " +
            $"Screen={locateResponse.ScreenNumber?.ToString() ?? "none"} CoordinateSpace={locateResponse.CoordinateSpace ?? ""} " +
            $"HasRawAction={locateResponse.RawAction.HasValue} Error={locateResponse.Error ?? ""}");

        return locateResponse;
    }

    public async Task<string> TranscribeAudioAsync(byte[] wavBytes, CancellationToken cancellationToken)
    {
        AppLogger.Info($"Worker /transcribe request started. Bytes={wavBytes.Length}");
        using var response = await httpClient.PostAsJsonAsync(
            new Uri(baseUri, "/transcribe"),
            new
            {
                mediaType = "audio/wav",
                data = Convert.ToBase64String(wavBytes)
            },
            JsonOptions,
            cancellationToken);

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        AppLogger.Info($"Worker /transcribe response. Status={(int)response.StatusCode} BodyLength={responseText.Length}");
        if (!response.IsSuccessStatusCode)
        {
            AppLogger.Info($"Worker /transcribe error body: {responseText}");
            response.EnsureSuccessStatusCode();
        }

        using var document = JsonDocument.Parse(responseText);
        return document.RootElement.TryGetProperty("text", out var textElement)
            ? textElement.GetString() ?? ""
            : "";
    }

    public async Task<byte[]> TextToSpeechAsync(string text, CancellationToken cancellationToken)
    {
        AppLogger.Info($"Worker /tts request started. TextLength={text.Length}");
        using var response = await httpClient.PostAsJsonAsync(
            new Uri(baseUri, "/tts"),
            new { text },
            JsonOptions,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        var audioBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        AppLogger.Info($"Worker /tts response. Status={(int)response.StatusCode} Bytes={audioBytes.Length}");
        return audioBytes;
    }
}
