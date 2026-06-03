using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

const string WorkerBaseUrl = "https://clickyclone.rohanbhadange18.workers.dev";

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true
};

using var httpClient = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(120)
};

Console.WriteLine("ClickyClone production diagnostics");
Console.WriteLine($"Worker: {WorkerBaseUrl}");

await CheckHealthAsync();
await CheckTranscribeTokenAsync();
await CheckTextToSpeechAsync();
await CheckChatWithRealScreenshotAsync();

Console.WriteLine("All diagnostics passed.");

async Task CheckHealthAsync()
{
    using var response = await httpClient.GetAsync($"{WorkerBaseUrl}/health");
    var text = await response.Content.ReadAsStringAsync();
    EnsureSuccess(response, text, "health");
    Console.WriteLine("health: ok");
}

async Task CheckTranscribeTokenAsync()
{
    using var response = await httpClient.PostAsync($"{WorkerBaseUrl}/transcribe-token", null);
    var text = await response.Content.ReadAsStringAsync();
    EnsureSuccess(response, text, "transcribe-token");

    using var document = JsonDocument.Parse(text);
    if (!document.RootElement.TryGetProperty("token", out var token) ||
        string.IsNullOrWhiteSpace(token.GetString()))
    {
        throw new InvalidOperationException("transcribe-token: response did not include a token.");
    }

    Console.WriteLine("transcribe-token: ok");
}

async Task CheckTextToSpeechAsync()
{
    using var response = await httpClient.PostAsJsonAsync(
        $"{WorkerBaseUrl}/tts",
        new { text = "diagnostics test" },
        jsonOptions);

    var bytes = await response.Content.ReadAsByteArrayAsync();
    if (!response.IsSuccessStatusCode)
    {
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        EnsureSuccess(response, text, "tts");
    }

    if (bytes.Length < 1024)
    {
        throw new InvalidOperationException($"tts: expected audio bytes, received only {bytes.Length} bytes.");
    }

    Console.WriteLine($"tts: ok ({bytes.Length} bytes)");
}

async Task CheckChatWithRealScreenshotAsync()
{
    var screenshot = CapturePrimaryScreen();
    var payload = new
    {
        transcript = "This is a diagnostics check. Briefly describe the screen and do not point unless useful.",
        images = new[]
        {
            new
            {
                label = $"user's screen (cursor is here) (image dimensions: {screenshot.Width}x{screenshot.Height} pixels)",
                mediaType = "image/jpeg",
                data = Convert.ToBase64String(screenshot.JpegBytes),
                screenNumber = 1,
                screenshotWidthInPixels = screenshot.Width,
                screenshotHeightInPixels = screenshot.Height
            }
        },
        conversationHistory = Array.Empty<object>()
    };

    using var response = await httpClient.PostAsJsonAsync($"{WorkerBaseUrl}/chat", payload, jsonOptions);
    var text = await response.Content.ReadAsStringAsync();
    EnsureSuccess(response, text, "chat");

    var chat = JsonSerializer.Deserialize<ChatResponse>(text, jsonOptions)
        ?? throw new InvalidOperationException("chat: response JSON could not be parsed.");

    if (string.IsNullOrWhiteSpace(chat.Text))
    {
        throw new InvalidOperationException("chat: text was empty.");
    }

    if (!ContainsPointTag(chat.Text))
    {
        throw new InvalidOperationException("chat: response did not include a point tag.");
    }

    if (string.IsNullOrWhiteSpace(chat.SpokenText))
    {
        throw new InvalidOperationException("chat: spokenText was empty.");
    }

    Console.WriteLine("chat: ok");
    Console.WriteLine($"chat spokenText: {chat.SpokenText}");
}

static ScreenCapture CapturePrimaryScreen()
{
    var screen = Screen.PrimaryScreen ?? throw new InvalidOperationException("No primary screen found.");
    var bounds = screen.Bounds;

    using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
    using (var graphics = Graphics.FromImage(bitmap))
    {
        graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
    }

    using var memoryStream = new MemoryStream();
    var jpegEncoder = ImageCodecInfo.GetImageEncoders().First(codec => codec.MimeType == "image/jpeg");
    using var encoderParameters = new EncoderParameters(1);
    encoderParameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 75L);
    bitmap.Save(memoryStream, jpegEncoder, encoderParameters);

    return new ScreenCapture(bounds.Width, bounds.Height, memoryStream.ToArray());
}

static void EnsureSuccess(HttpResponseMessage response, string body, string label)
{
    if (response.IsSuccessStatusCode)
    {
        return;
    }

    throw new InvalidOperationException(
        $"{label}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
}

static bool ContainsPointTag(string text)
{
    return text.Contains("[POINT:", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("[POINT-ELEMENT:", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("[BOX:", StringComparison.OrdinalIgnoreCase);
}

internal sealed record ScreenCapture(int Width, int Height, byte[] JpegBytes);

internal sealed record ChatResponse(string Text, string SpokenText, ChatPoint? Point);

internal sealed record ChatPoint(double X, double Y, string? Label, int? ScreenNumber);
