using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClickyClone.Core;

namespace ClickyClone.Services;

public sealed class WorkerClient
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
        return ok && string.Equals(source, "element", StringComparison.OrdinalIgnoreCase);
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
        var elementCount = images.Sum(image => image.Elements?.Count ?? 0);
        AppLogger.Info($"Worker /chat payload plan. Mode=uia-only Elements={elementCount}");
        var payload = new
        {
            transcript,
            images = images.Select(image => new
            {
                label = image.Label,
                mediaType = image.MediaType,
                screenNumber = image.ScreenNumber,
                screenshotWidthInPixels = image.ScreenshotWidthInPixels,
                screenshotHeightInPixels = image.ScreenshotHeightInPixels,
                displayWidthInPixels = image.DisplayWidthInPixels,
                displayHeightInPixels = image.DisplayHeightInPixels,
                elements = (image.Elements ?? []).Select(element => new
                {
                    id = element.Id,
                    name = element.Name,
                    controlType = element.ControlType,
                    x = element.X,
                    y = element.Y,
                    width = element.Width,
                    height = element.Height,
                    centerX = element.CenterX,
                    centerY = element.CenterY,
                    windowTitle = element.WindowTitle,
                    isClickable = element.IsClickable,
                    score = element.Score
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
