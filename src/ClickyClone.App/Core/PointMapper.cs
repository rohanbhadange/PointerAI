using WpfPoint = System.Windows.Point;

namespace ClickyClone.Core;

public static class PointMapper
{
    public static bool TryMapPoint(
        ChatPoint? chatPoint,
        IReadOnlyList<ScreenCapturePayload> captures,
        out PointTarget pointTarget)
    {
        pointTarget = new PointTarget(new WpfPoint(), null, null);
        if (chatPoint is null)
        {
            return false;
        }

        if (string.Equals(chatPoint.Source, "element", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(chatPoint.ElementId))
        {
            return TryMapElementPoint(chatPoint.ElementId, captures, out pointTarget);
        }

        if (string.Equals(chatPoint.Source, "visual-target", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(chatPoint.TargetId))
        {
            return TryMapVisualTarget(chatPoint.TargetId, captures, out pointTarget);
        }

        var targetCapture = chatPoint.ScreenNumber is int screenNumber
            ? captures.FirstOrDefault(capture => capture.ScreenNumber == screenNumber)
            : captures.FirstOrDefault(capture => capture.IsCursorScreen);

        if (targetCapture is null)
        {
            return false;
        }

        var desktopPoint = MapScreenshotPoint(
            chatPoint.X,
            chatPoint.Y,
            targetCapture);

        System.Windows.Rect? desktopBounds = chatPoint.Bounds is { } bounds
            ? MapScreenshotBounds(bounds, targetCapture)
            : null;

        pointTarget = new PointTarget(
            desktopPoint,
            chatPoint.Label,
            chatPoint.ScreenNumber,
            desktopBounds);
        return true;
    }

    private static bool TryMapElementPoint(
        string elementId,
        IReadOnlyList<ScreenCapturePayload> captures,
        out PointTarget pointTarget)
    {
        pointTarget = new PointTarget(new WpfPoint(), null, null);
        foreach (var capture in captures)
        {
            var element = capture.Elements?.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, elementId, StringComparison.OrdinalIgnoreCase));
            if (element is null)
            {
                continue;
            }

            var center = MapScreenshotPoint(element.CenterX, element.CenterY, capture);
            var bounds = MapScreenshotBounds(
                new ChatBounds(element.X, element.Y, element.Width, element.Height),
                capture);
            pointTarget = new PointTarget(
                center,
                element.Name,
                capture.ScreenNumber,
                bounds);
            return true;
        }

        return false;
    }

    private static bool TryMapVisualTarget(
        string targetId,
        IReadOnlyList<ScreenCapturePayload> captures,
        out PointTarget pointTarget)
    {
        pointTarget = new PointTarget(new WpfPoint(), null, null);
        foreach (var capture in captures)
        {
            var target = capture.VisualTargets?.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, targetId, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                continue;
            }

            var center = MapScreenshotPoint(target.CenterX, target.CenterY, capture);
            var bounds = MapScreenshotBounds(
                new ChatBounds(target.X, target.Y, target.Width, target.Height),
                capture);
            pointTarget = new PointTarget(
                center,
                target.LabelHint ?? target.Id,
                capture.ScreenNumber,
                bounds);
            return true;
        }

        return false;
    }

    private static WpfPoint MapScreenshotPoint(double x, double y, ScreenCapturePayload capture)
    {
        var clampedX = Math.Clamp(x, 0, capture.ScreenshotWidthInPixels);
        var clampedY = Math.Clamp(y, 0, capture.ScreenshotHeightInPixels);
        var desktopX = capture.DesktopLeft + clampedX * capture.DisplayWidthInPixels / capture.ScreenshotWidthInPixels;
        var desktopY = capture.DesktopTop + clampedY * capture.DisplayHeightInPixels / capture.ScreenshotHeightInPixels;
        return new WpfPoint(desktopX, desktopY);
    }

    private static System.Windows.Rect MapScreenshotBounds(ChatBounds bounds, ScreenCapturePayload capture)
    {
        var leftTop = MapScreenshotPoint(bounds.X, bounds.Y, capture);
        var rightBottom = MapScreenshotPoint(bounds.X + bounds.Width, bounds.Y + bounds.Height, capture);
        return new System.Windows.Rect(leftTop, rightBottom);
    }
}
