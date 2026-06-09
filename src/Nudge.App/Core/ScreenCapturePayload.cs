namespace Nudge.Core;

public sealed record ScreenCapturePayload(
    string Label,
    string MediaType,
    string Base64Data,
    string? VisualAtlasBase64Data,
    string? CoordinateGuideBase64Data,
    bool IsCursorScreen,
    int ScreenNumber,
    int DisplayWidthInPixels,
    int DisplayHeightInPixels,
    int ScreenshotWidthInPixels,
    int ScreenshotHeightInPixels,
    int DesktopLeft,
    int DesktopTop,
    double DpiScaleX = 1,
    double DpiScaleY = 1,
    double DesktopLeftInDips = 0,
    double DesktopTopInDips = 0,
    double DisplayWidthInDips = 0,
    double DisplayHeightInDips = 0,
    IReadOnlyList<VisualTargetPayload>? VisualTargets = null,
    IReadOnlyList<ScreenElementPayload>? Elements = null);

public sealed record VisualTargetPayload(
    string Id,
    string Kind,
    double X,
    double Y,
    double Width,
    double Height,
    double CenterX,
    double CenterY,
    double Confidence,
    string? LabelHint = null);

public sealed record ScreenElementPayload(
    string Id,
    string Name,
    string ControlType,
    double X,
    double Y,
    double Width,
    double Height,
    double CenterX,
    double CenterY,
    string? WindowTitle = null,
    bool IsClickable = false,
    double Score = 0);
