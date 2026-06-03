namespace ClickyClone.Core;

public sealed record PointTarget(
    System.Windows.Point DesktopPoint,
    string? Label,
    int? ScreenNumber,
    System.Windows.Rect? DesktopBounds = null);
