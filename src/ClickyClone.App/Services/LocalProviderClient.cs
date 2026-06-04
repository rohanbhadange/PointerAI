using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ClickyClone.Core;

namespace ClickyClone.Services;

public sealed class LocalProviderClient : IBackendClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private static readonly Regex MarkerRegex = new(@"\b[CR]\d{2}\b", RegexOptions.Compiled);
    private readonly HttpClient httpClient;
    private readonly string envPath;

    public LocalProviderClient(string envPath)
    {
        this.envPath = envPath;
        httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120)
        };
    }

    public Task CheckHealthAsync(CancellationToken cancellationToken)
    {
        var env = LoadEnv();
        EnsureConfigured(env);
        AppLogger.Info("Local backend health check passed.");
        return Task.CompletedTask;
    }

    public Task<WorkerDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var env = LoadEnv();
        return Task.FromResult(new WorkerDiagnostics(
            true,
            "clickyclone-local",
            AppConfig.AppVersion,
            new WorkerSecretDiagnostics(
                !string.IsNullOrWhiteSpace(env.OpenAIKey),
                !string.IsNullOrWhiteSpace(env.AssemblyAIKey),
                !string.IsNullOrWhiteSpace(env.ElevenLabsKey),
                !string.IsNullOrWhiteSpace(env.ElevenLabsVoiceId)),
            new WorkerLocatorDiagnostics("openai-computer-use", env.OpenAIComputerModel),
            new WorkerChatDiagnostics(env.OpenAIModel)));
    }

    public Task<bool> CheckPointingSelfTestAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(true);
    }

    public async Task<string> GetAssemblyAITokenAsync(CancellationToken cancellationToken)
    {
        var env = LoadEnv();
        Require(env.AssemblyAIKey, LocalEnv.AssemblyAIKeyName);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://streaming.assemblyai.com/v3/token?expires_in_seconds=480");
        request.Headers.TryAddWithoutValidation("authorization", env.AssemblyAIKey);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("token", out var tokenElement)
            ? tokenElement.GetString() ?? throw new InvalidOperationException("AssemblyAI token was empty.")
            : throw new InvalidOperationException("AssemblyAI token response did not include token.");
    }

    public async Task<ChatResponse> SendChatAsync(
        string transcript,
        IReadOnlyList<ScreenCapturePayload> images,
        IReadOnlyList<ConversationTurn> conversationHistory,
        CancellationToken cancellationToken)
    {
        var env = LoadEnv();
        Require(env.OpenAIKey, LocalEnv.OpenAIKeyName);
        AppLogger.Info($"Local /chat started. TranscriptLength={transcript.Length} Images={images.Count} History={conversationHistory.Count}");

        var targetLookup = BuildVisualTargetLookup(images);
        var content = new List<object>
        {
            new
            {
                type = "input_text",
                text = $"{SystemPrompt}\n\nRecent conversation:\n{FormatHistory(conversationHistory)}\n\nThe user said:\n{transcript}\n\nVisible reticle targets:\n{FormatVisualTargetManifest(images) ?? "none"}\n\nReturn only valid JSON matching the requested schema."
            }
        };

        foreach (var image in images)
        {
            if (!string.IsNullOrWhiteSpace(image.Base64Data))
            {
                content.Add(new
                {
                    type = "input_image",
                    image_url = $"data:{image.MediaType};base64,{image.Base64Data}",
                    detail = "high"
                });
                content.Add(new { type = "input_text", text = $"{image.Label} clean screenshot" });
            }

            if (!string.IsNullOrWhiteSpace(image.VisualAtlasBase64Data))
            {
                content.Add(new
                {
                    type = "input_image",
                    image_url = $"data:{image.MediaType};base64,{image.VisualAtlasBase64Data}",
                    detail = "high"
                });
                content.Add(new { type = "input_text", text = $"{image.Label} annotated reticle atlas. Choose one visible marker id from this image." });
            }
        }

        var requestBody = new Dictionary<string, object?>
        {
            ["model"] = env.OpenAIModel,
            ["input"] = new[] { new { role = "user", content } },
            ["max_output_tokens"] = 4096
        };
        if (env.OpenAIModel.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase))
        {
            requestBody["reasoning"] = new { effort = "low" };
        }

        using var request = CreateOpenAIRequest(env.OpenAIKey!, requestBody);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI chat failed: {responseText}");
        }

        using var responseDocument = JsonDocument.Parse(responseText);
        var text = ExtractOpenAIText(responseDocument.RootElement).Trim();
        var parsed = ParseTargetSelection(text, targetLookup);
        return new ChatResponse(parsed.SpokenText, parsed.SpokenText, parsed.Point);
    }

    public async Task<LocateResponse> LocateAsync(
        string goal,
        ScreenCapturePayload capture,
        string provider,
        CancellationToken cancellationToken)
    {
        var env = LoadEnv();
        Require(env.OpenAIKey, LocalEnv.OpenAIKeyName);
        AppLogger.Info($"Local /locate started. GoalLength={goal.Length} Screen={capture.ScreenNumber} Size={capture.ScreenshotWidthInPixels}x{capture.ScreenshotHeightInPixels}");
        using var locateTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        locateTimeout.CancelAfter(TimeSpan.FromSeconds(45));

        JsonDocument firstData;
        try
        {
            firstData = await SendOpenAIComputerLocateAsync(
                env,
                BuildOpenAIComputerPrompt(goal, capture.ScreenshotWidthInPixels, capture.ScreenshotHeightInPixels),
                capture,
                previousResponseId: null,
                callId: null,
                locateTimeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AppLogger.Info("Local /locate timed out during first OpenAI computer-use call.");
            return new LocateResponse(false, provider, null, null, null, capture.ScreenNumber, null, "Local locator timed out");
        }

        using (firstData)
        {
            var firstPoint = ExtractOpenAIComputerPoint(firstData.RootElement);
            if (firstPoint is not null)
            {
                return NormalizeLocatePoint(provider, goal, firstPoint.Value, capture);
            }

            var screenshotCall = FindOpenAIComputerCall(firstData.RootElement, action =>
                string.Equals(ReadString(action, "type"), "screenshot", StringComparison.OrdinalIgnoreCase));
            if (screenshotCall.CallId is null ||
                !firstData.RootElement.TryGetProperty("id", out var responseIdElement) ||
                responseIdElement.GetString() is not { Length: > 0 } responseId)
            {
                return new LocateResponse(false, provider, null, null, null, capture.ScreenNumber, null, "No OpenAI computer-use coordinate or screenshot request returned");
            }

            JsonDocument secondData;
            try
            {
                secondData = await SendOpenAIComputerLocateAsync(
                    env,
                    "",
                    capture,
                    responseId,
                    screenshotCall.CallId,
                    locateTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                AppLogger.Info("Local /locate timed out during second OpenAI computer-use call.");
                return new LocateResponse(false, provider, null, null, null, capture.ScreenNumber, null, "Local locator timed out");
            }

            using (secondData)
            {
                var secondPoint = ExtractOpenAIComputerPoint(secondData.RootElement);
                return secondPoint is null
                    ? new LocateResponse(false, provider, null, null, null, capture.ScreenNumber, null, "No OpenAI computer-use coordinate returned after screenshot")
                    : NormalizeLocatePoint(provider, goal, secondPoint.Value, capture);
            }
        }
    }

    public async Task<string> TranscribeAudioAsync(byte[] wavBytes, CancellationToken cancellationToken)
    {
        var env = LoadEnv();
        Require(env.AssemblyAIKey, LocalEnv.AssemblyAIKeyName);
        AppLogger.Info($"Local transcription started. Bytes={wavBytes.Length}");

        using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.assemblyai.com/v2/upload")
        {
            Content = new ByteArrayContent(wavBytes)
        };
        uploadRequest.Headers.TryAddWithoutValidation("authorization", env.AssemblyAIKey);
        uploadRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        using var uploadResponse = await httpClient.SendAsync(uploadRequest, cancellationToken);
        var uploadText = await uploadResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!uploadResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"AssemblyAI upload failed: {uploadText}");
        }

        using var uploadDocument = JsonDocument.Parse(uploadText);
        var uploadUrl = uploadDocument.RootElement.GetProperty("upload_url").GetString();
        if (string.IsNullOrWhiteSpace(uploadUrl))
        {
            throw new InvalidOperationException("AssemblyAI upload response did not include upload_url.");
        }

        using var submitRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.assemblyai.com/v2/transcript")
        {
            Content = JsonContent.Create(new
            {
                audio_url = uploadUrl,
                speech_models = new[] { "universal-3-pro", "universal-2" },
                language_code = "en_us",
                punctuate = true,
                format_text = true
            }, options: JsonOptions)
        };
        submitRequest.Headers.TryAddWithoutValidation("authorization", env.AssemblyAIKey);

        using var submitResponse = await httpClient.SendAsync(submitRequest, cancellationToken);
        var submitText = await submitResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!submitResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"AssemblyAI transcript submit failed: {submitText}");
        }

        using var submitDocument = JsonDocument.Parse(submitText);
        var transcriptId = submitDocument.RootElement.GetProperty("id").GetString();
        if (string.IsNullOrWhiteSpace(transcriptId))
        {
            throw new InvalidOperationException("AssemblyAI transcript response did not include id.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - startedAt < TimeSpan.FromSeconds(45))
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            using var pollRequest = new HttpRequestMessage(HttpMethod.Get, $"https://api.assemblyai.com/v2/transcript/{transcriptId}");
            pollRequest.Headers.TryAddWithoutValidation("authorization", env.AssemblyAIKey);
            using var pollResponse = await httpClient.SendAsync(pollRequest, cancellationToken);
            var pollText = await pollResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!pollResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"AssemblyAI transcript poll failed: {pollText}");
            }

            using var pollDocument = JsonDocument.Parse(pollText);
            var pollRoot = pollDocument.RootElement;
            var status = ReadString(pollRoot, "status");
            if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                return ReadString(pollRoot, "text") ?? "";
            }

            if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"AssemblyAI transcription failed: {ReadString(pollRoot, "error") ?? pollText}");
            }
        }

        throw new TimeoutException("AssemblyAI transcription timed out.");
    }

    public async Task<byte[]> TextToSpeechAsync(string text, CancellationToken cancellationToken)
    {
        var env = LoadEnv();
        Require(env.ElevenLabsKey, LocalEnv.ElevenLabsKeyName);
        Require(env.ElevenLabsVoiceId, LocalEnv.ElevenLabsVoiceIdName);
        AppLogger.Info($"Local TTS started. TextLength={text.Length}");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.elevenlabs.io/v1/text-to-speech/{Uri.EscapeDataString(env.ElevenLabsVoiceId!)}")
        {
            Content = JsonContent.Create(new
            {
                text,
                model_id = env.ElevenLabsModel,
                voice_settings = new { stability = 0.5, similarity_boost = 0.75 }
            }, options: JsonOptions)
        };
        request.Headers.TryAddWithoutValidation("xi-api-key", env.ElevenLabsKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"ElevenLabs request failed: {errorText}");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public static ChatPoint? ExtractComputerUsePointForTest(JsonElement response, string provider, string goal, ScreenCapturePayload capture)
    {
        var point = ExtractOpenAIComputerPoint(response);
        return point is null
            ? null
            : new ChatPoint(
                Clamp(Math.Round(point.Value.X), 0, capture.ScreenshotWidthInPixels),
                Clamp(Math.Round(point.Value.Y), 0, capture.ScreenshotHeightInPixels),
                goal,
                capture.ScreenNumber,
                "computer-use");
    }

    private LocalEnvSettings LoadEnv()
    {
        return LocalEnv.Load(envPath);
    }

    private static void EnsureConfigured(LocalEnvSettings env)
    {
        Require(env.OpenAIKey, LocalEnv.OpenAIKeyName);
        Require(env.AssemblyAIKey, LocalEnv.AssemblyAIKeyName);
        Require(env.ElevenLabsKey, LocalEnv.ElevenLabsKeyName);
        Require(env.ElevenLabsVoiceId, LocalEnv.ElevenLabsVoiceIdName);
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Missing local setting: {name}");
        }
    }

    private static HttpRequestMessage CreateOpenAIRequest(string apiKey, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses")
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    private async Task<JsonDocument> SendOpenAIComputerLocateAsync(
        LocalEnvSettings env,
        string prompt,
        ScreenCapturePayload capture,
        string? previousResponseId,
        string? callId,
        CancellationToken cancellationToken)
    {
        object body = previousResponseId is null
            ? new
            {
                model = env.OpenAIComputerModel,
                tools = new[] { new { type = "computer" } },
                input = new[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "input_text", text = prompt },
                            new
                            {
                                type = "input_image",
                                image_url = $"data:{capture.MediaType};base64,{capture.Base64Data}",
                                detail = "original"
                            }
                        }
                    }
                }
            }
            : new
            {
                model = env.OpenAIComputerModel,
                tools = new[] { new { type = "computer" } },
                previous_response_id = previousResponseId,
                input = new object[]
                {
                    new
                    {
                        type = "computer_call_output",
                        call_id = callId,
                        output = new
                        {
                            type = "computer_screenshot",
                            image_url = $"data:{capture.MediaType};base64,{capture.Base64Data}",
                            detail = "original"
                        }
                    }
                }
            };

        using var request = CreateOpenAIRequest(env.OpenAIKey!, body);
        var stopwatch = Stopwatch.StartNew();
        AppLogger.Info($"Local OpenAI computer-use request started. FollowUp={previousResponseId is not null}");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        AppLogger.Info($"Local OpenAI computer-use response. FollowUp={previousResponseId is not null} Status={(int)response.StatusCode} BodyLength={responseText.Length} Ms={stopwatch.ElapsedMilliseconds}");
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI computer-use failed: {responseText}");
        }

        return JsonDocument.Parse(responseText);
    }

    private static LocateResponse NormalizeLocatePoint(
        string provider,
        string goal,
        (double X, double Y, JsonElement RawAction) point,
        ScreenCapturePayload capture)
    {
        return new LocateResponse(
            true,
            provider,
            Clamp(Math.Round(point.X), 0, capture.ScreenshotWidthInPixels),
            Clamp(Math.Round(point.Y), 0, capture.ScreenshotHeightInPixels),
            goal,
            capture.ScreenNumber,
            "screenshot_pixels_top_left",
            null,
            point.RawAction.Clone());
    }

    private static (double X, double Y, JsonElement RawAction)? ExtractOpenAIComputerPoint(JsonElement data)
    {
        var call = FindOpenAIComputerCall(data, action =>
        {
            var type = ReadString(action, "type") ?? "";
            return type is "click" or "double_click" or "move";
        });

        if (call.Action is null || HasOpenAISafetyChecks(call.Item))
        {
            return null;
        }

        if (!TryReadDouble(call.Action.Value, "x", out var x) ||
            !TryReadDouble(call.Action.Value, "y", out var y))
        {
            return null;
        }

        return (x, y, call.Action.Value.Clone());
    }

    private static (JsonElement Item, JsonElement? Action, string? CallId) FindOpenAIComputerCall(
        JsonElement data,
        Func<JsonElement, bool> predicate)
    {
        if (!data.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return default;
        }

        foreach (var item in output.EnumerateArray())
        {
            if (!string.Equals(ReadString(item, "type"), "computer_call", StringComparison.Ordinal))
            {
                continue;
            }

            var callId = ReadString(item, "call_id");
            if (item.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
            {
                foreach (var action in actions.EnumerateArray())
                {
                    if (predicate(action))
                    {
                        return (item, action, callId);
                    }
                }
            }
            else if (item.TryGetProperty("action", out var action) && predicate(action))
            {
                return (item, action, callId);
            }
        }

        return default;
    }

    private static bool HasOpenAISafetyChecks(JsonElement item)
    {
        return item.ValueKind == JsonValueKind.Object &&
               item.TryGetProperty("pending_safety_checks", out var checks) &&
               checks.ValueKind == JsonValueKind.Array &&
               checks.GetArrayLength() > 0;
    }

    private static string BuildOpenAIComputerPrompt(string goal, int width, int height)
    {
        return $"""
        You are a precise GUI locator for a Windows desktop application.

        The screenshot is exactly {width} pixels wide and {height} pixels tall.

        Coordinate system:
        - origin is the top-left corner of the screenshot
        - x increases right
        - y increases down
        - coordinates must be screenshot image pixels

        Goal:
        "{goal}"

        Use the computer tool to return the UI action you would use to click the visual center of the single UI control that best satisfies the goal.

        Rules:
        - Return one click target only.
        - Prefer the actual clickable control, not nearby explanatory text.
        - If the target is a labeled button, click the center of the button area.
        - If the target is a tree item, click the center of the row text.
        - If the target is an icon-only control, click the visual center of the icon's clickable area.
        - If no relevant target is visible, do not return a click action.
        - Do not explain.
        """;
    }

    private static string ExtractOpenAIText(JsonElement response)
    {
        if (response.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
        {
            return outputText.GetString() ?? "";
        }

        var builder = new StringBuilder();
        if (!response.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        foreach (var outputItem in output.EnumerateArray())
        {
            if (!outputItem.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var contentItem in content.EnumerateArray())
            {
                if (contentItem.TryGetProperty("text", out var textElement))
                {
                    builder.Append(textElement.GetString());
                }
            }
        }

        return builder.ToString();
    }

    private static (string SpokenText, ChatPoint? Point) ParseTargetSelection(
        string text,
        IReadOnlyDictionary<string, VisualTargetInfo> targetLookup)
    {
        var spokenText = "";
        var targetId = "";
        try
        {
            using var document = JsonDocument.Parse(ExtractJsonObject(text));
            spokenText = SanitizeSpokenText(ReadString(document.RootElement, "spokenText") ?? "");
            targetId = ReadString(document.RootElement, "targetId")?.Trim() ?? "";
        }
        catch
        {
            spokenText = SanitizeSpokenText(text);
        }

        if (string.IsNullOrWhiteSpace(targetId) || !targetLookup.TryGetValue(targetId, out var target))
        {
            return (spokenText, null);
        }

        return (spokenText, new ChatPoint(
            target.X,
            target.Y,
            target.Label,
            target.ScreenNumber,
            "visual-target",
            target.Bounds,
            null,
            target.Id));
    }

    private static Dictionary<string, VisualTargetInfo> BuildVisualTargetLookup(IEnumerable<ScreenCapturePayload> images)
    {
        var lookup = new Dictionary<string, VisualTargetInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in images)
        {
            foreach (var target in image.VisualTargets ?? [])
            {
                var bounds = target.Width > 0 && target.Height > 0
                    ? new ChatBounds(target.X, target.Y, target.Width, target.Height)
                    : null;
                lookup[target.Id] = new VisualTargetInfo(
                    target.Id,
                    target.CenterX,
                    target.CenterY,
                    string.IsNullOrWhiteSpace(target.LabelHint) ? target.Kind : target.LabelHint,
                    image.ScreenNumber,
                    bounds);
            }
        }

        return lookup;
    }

    private static string? FormatVisualTargetManifest(IEnumerable<ScreenCapturePayload> images)
    {
        var rows = images
            .SelectMany(image => (image.VisualTargets ?? []).Select(target => (image, target)))
            .Take(80)
            .Select(item =>
                $"{item.target.Id} | {item.target.Kind} | screen={item.image.ScreenNumber} | center={Math.Round(item.target.CenterX)},{Math.Round(item.target.CenterY)} | size={Math.Round(item.target.Width)}x{Math.Round(item.target.Height)} | confidence={item.target.Confidence:0.00}")
            .ToArray();

        return rows.Length == 0 ? null : string.Join("\n", rows);
    }

    private static string FormatHistory(IReadOnlyList<ConversationTurn> conversationHistory)
    {
        var rows = conversationHistory
            .TakeLast(10)
            .Select(turn => $"user: {turn.UserTranscript}\nclicky: {turn.AssistantResponse}");
        var text = string.Join("\n\n", rows);
        return string.IsNullOrWhiteSpace(text) ? "none" : text;
    }

    private static string ExtractJsonObject(string text)
    {
        var trimmed = text.Trim();
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start >= 0 && end > start ? trimmed[start..(end + 1)] : trimmed;
    }

    private static string SanitizeSpokenText(string text)
    {
        return MarkerRegex
            .Replace(text, "that spot")
            .Replace("[POINT:", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool TryReadDouble(JsonElement element, string propertyName, out double value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var property) &&
               property.TryGetDouble(out value);
    }

    private static double Clamp(double value, double min, double max)
    {
        return Math.Max(min, Math.Min(max, value));
    }

    private sealed record VisualTargetInfo(
        string Id,
        double X,
        double Y,
        string Label,
        int? ScreenNumber,
        ChatBounds? Bounds);

    private const string SystemPrompt = """
    you are clicky, a friendly always-on windows desktop companion. the user speaks to you with push-to-talk, and you can see screenshots of their monitors.

    rules:
    - default to one or two sentences. be direct and useful.
    - write spokenText for speech. no markdown, no bullets, no code blocks.
    - if the user's request is to find a visible thing, choose only from the visible reticle marker ids in the annotated image.
    - if no marker clearly matches the requested thing, set targetId to null.
    - if the user's question is not about finding a screen target, answer directly and set targetId to null.
    - do not read code verbatim. explain what it does or what to change.
    - never say "simply" or "just".
    - never mention marker ids, screen ids, coordinates, JSON, or targeting syntax in spokenText.

    reticle targeting:
    - choose the visible marker whose ring is on or closest to the requested target.
    - prefer C markers for controls and R markers for large rendered regions.
    - never invent marker ids.
    - never return coordinates.
    - never return bounding boxes.

    response schema:
    {"spokenText":"short user-facing response","targetId":"C01 or R02 or null","needsZoom":false}
    """;
}
