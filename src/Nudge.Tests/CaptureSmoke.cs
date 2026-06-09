using Nudge.Services;

namespace Nudge.Tests;

internal static class CaptureSmoke
{
    public static async Task RunAsync()
    {
        var captureService = new ScreenCaptureService();
        IReadOnlyList<Nudge.Core.ScreenCapturePayload> captures;
        try
        {
            captures = await captureService.CaptureAllScreensAsync(CancellationToken.None);
        }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 6)
        {
            Console.WriteLine("capture smoke skipped: screen capture is unavailable in this non-interactive shell.");
            return;
        }
        if (captures.Count == 0)
        {
            throw new InvalidOperationException("expected at least one screen capture");
        }

        foreach (var capture in captures)
        {
            if (string.IsNullOrWhiteSpace(capture.Base64Data))
            {
                throw new InvalidOperationException($"screen {capture.ScreenNumber}: missing screenshot data");
            }

            if (capture.ScreenshotWidthInPixels <= 0 || capture.ScreenshotHeightInPixels <= 0)
            {
                throw new InvalidOperationException($"screen {capture.ScreenNumber}: invalid dimensions");
            }

            Console.WriteLine(
                $"capture smoke screen {capture.ScreenNumber}: {capture.ScreenshotWidthInPixels}x{capture.ScreenshotHeightInPixels}, " +
                $"elements={capture.Elements?.Count ?? 0}, imageChars={capture.Base64Data.Length}, guideChars={capture.CoordinateGuideBase64Data?.Length ?? 0}");
        }
    }
}
