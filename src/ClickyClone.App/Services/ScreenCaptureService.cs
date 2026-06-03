using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Forms;
using ClickyClone.Core;
using WpfRect = System.Windows.Rect;

namespace ClickyClone.Services;

public sealed class ScreenCaptureService
{
    private static readonly TimeSpan AccessibleElementBudget = TimeSpan.FromMilliseconds(450);
    private const int AccessibleElementLimit = 80;
    private const int VisualTargetLimit = 54;
    private const int VisualTargetTileWidth = 96;
    private const int VisualTargetTileHeight = 72;
    private const int VisualTargetMinSpacing = 52;

    public Task<IReadOnlyList<ScreenCapturePayload>> CaptureAllScreensAsync(CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<ScreenCapturePayload>>(() =>
        {
            var captureStopwatch = Stopwatch.StartNew();
            var cursorPosition = Cursor.Position;
            var screens = Screen.AllScreens
                .OrderByDescending(screen => screen.Bounds.Contains(cursorPosition))
                .ThenBy(screen => screen.Bounds.Left)
                .ToArray();

            var captures = new List<ScreenCapturePayload>();

            for (var index = 0; index < screens.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var screen = screens[index];
                var bounds = screen.Bounds;
                var dpiScale = GetDpiScale(bounds);
                var isCursorScreen = bounds.Contains(cursorPosition);
                var screenStopwatch = Stopwatch.StartNew();

                using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    try
                    {
                        graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                    }
                    catch (System.ComponentModel.Win32Exception error)
                    {
                        AppLogger.Error($"Screen capture failed for screen {index + 1} bounds={bounds}", error);
                        throw;
                    }
                }

                using var memoryStream = new MemoryStream();
                bitmap.Save(memoryStream, ImageFormat.Png);
                AppLogger.Info($"PERF stage=screenshot_pixels screen={index + 1} ms={screenStopwatch.ElapsedMilliseconds}");

                var atlasStopwatch = Stopwatch.StartNew();
                var visualTargets = GenerateVisualTargets(bitmap, index + 1);
                using var visualAtlasStream = new MemoryStream();
                using (var atlasBitmap = new Bitmap(bitmap))
                using (var atlasGraphics = Graphics.FromImage(atlasBitmap))
                {
                    DrawVisualAtlas(atlasGraphics, visualTargets);
                    atlasBitmap.Save(visualAtlasStream, ImageFormat.Png);
                }
                LogVisualAtlasDiagnostic(index + 1, bounds, visualTargets, atlasStopwatch.Elapsed);
                AppLogger.Info($"PERF stage=visual_atlas screen={index + 1} ms={atlasStopwatch.ElapsedMilliseconds} targets={visualTargets.Count}");

                var screenNumber = index + 1;
                var label = screens.Length == 1
                    ? $"user's screen (cursor is here) (image dimensions: {bounds.Width}x{bounds.Height} pixels)"
                    : isCursorScreen
                        ? $"screen {screenNumber} of {screens.Length} - cursor is on this screen (primary focus) (image dimensions: {bounds.Width}x{bounds.Height} pixels)"
                        : $"screen {screenNumber} of {screens.Length} - secondary screen (image dimensions: {bounds.Width}x{bounds.Height} pixels)";

                captures.Add(new ScreenCapturePayload(
                    label,
                    "image/png",
                    Convert.ToBase64String(memoryStream.ToArray()),
                    Convert.ToBase64String(visualAtlasStream.ToArray()),
                    null,
                    isCursorScreen,
                    screenNumber,
                    bounds.Width,
                    bounds.Height,
                    bounds.Width,
                    bounds.Height,
                    bounds.Left,
                    bounds.Top,
                    dpiScale.X,
                    dpiScale.Y,
                    bounds.Left / dpiScale.X,
                    bounds.Top / dpiScale.Y,
                    bounds.Width / dpiScale.X,
                    bounds.Height / dpiScale.Y,
                    visualTargets,
                    []));
            }

            AppLogger.Info($"PERF stage=screenshot_total ms={captureStopwatch.ElapsedMilliseconds} screens={captures.Count}");
            return captures;
        }, cancellationToken);
    }

    private static void DrawCoordinateGuide(Graphics graphics, int width, int height)
    {
        using var majorPen = new Pen(Color.FromArgb(140, 0, 122, 255), 1);
        using var minorPen = new Pen(Color.FromArgb(42, 0, 122, 255), 1);
        using var borderPen = new Pen(Color.FromArgb(180, 0, 75, 180), 2);
        using var textBrush = new SolidBrush(Color.FromArgb(235, 0, 66, 160));
        using var textBackBrush = new SolidBrush(Color.FromArgb(220, 255, 255, 255));
        using var font = new Font("Segoe UI", 11, FontStyle.Bold, GraphicsUnit.Pixel);

        graphics.DrawRectangle(borderPen, 0, 0, Math.Max(0, width - 1), Math.Max(0, height - 1));
        DrawGuideLabel(graphics, $"origin 0,0  size {width}x{height}", 4, 4, font, textBrush, textBackBrush);

        for (var x = 0; x <= width; x += 25)
        {
            var isMajor = x % 100 == 0;
            graphics.DrawLine(isMajor ? majorPen : minorPen, x, 0, x, height);
            if (isMajor && x > 0)
            {
                DrawGuideLabel(graphics, $"x={x}", x + 2, 18, font, textBrush, textBackBrush);
                DrawGuideLabel(graphics, $"x={x}", x + 2, height - 18, font, textBrush, textBackBrush);
            }
        }

        for (var y = 0; y <= height; y += 25)
        {
            var isMajor = y % 100 == 0;
            graphics.DrawLine(isMajor ? majorPen : minorPen, 0, y, width, y);
            if (isMajor && y > 0)
            {
                DrawGuideLabel(graphics, $"y={y}", 4, y + 2, font, textBrush, textBackBrush);
                DrawGuideLabel(graphics, $"y={y}", Math.Max(4, width - 58), y + 2, font, textBrush, textBackBrush);
            }
        }
    }

    private static IReadOnlyList<VisualTargetPayload> GenerateVisualTargets(Bitmap bitmap, int screenNumber)
    {
        var candidates = new List<(VisualTargetPayload Target, double Score)>();
        var tileWidth = Math.Min(VisualTargetTileWidth, Math.Max(48, bitmap.Width / 10));
        var tileHeight = Math.Min(VisualTargetTileHeight, Math.Max(42, bitmap.Height / 12));

        for (var y = 0; y < bitmap.Height; y += tileHeight)
        {
            for (var x = 0; x < bitmap.Width; x += tileWidth)
            {
                var width = Math.Min(tileWidth, bitmap.Width - x);
                var height = Math.Min(tileHeight, bitmap.Height - y);
                if (width < 24 || height < 24)
                {
                    continue;
                }

                var score = EstimateVisualInterest(bitmap, x, y, width, height);
                if (score < 0.12)
                {
                    continue;
                }

                var kind = width * height > 18_000 ? "R" : "C";
                var target = new VisualTargetPayload(
                    "",
                    kind == "R" ? "region" : "visual-candidate",
                    x,
                    y,
                    width,
                    height,
                    x + width / 2.0,
                    y + height / 2.0,
                    Math.Round(score, 3),
                    null);
                candidates.Add((target, score));
            }
        }

        var selected = new List<VisualTargetPayload>();
        foreach (var candidate in candidates.OrderByDescending(candidate => candidate.Score))
        {
            if (selected.Count >= VisualTargetLimit)
            {
                break;
            }

            if (!IsFarEnoughFromSelected(candidate.Target, selected))
            {
                continue;
            }

            selected.Add(candidate.Target);
        }

        return selected
            .Select((target, index) => target with
            {
                Id = $"{(target.Kind == "region" ? "R" : "C")}{index + 1:00}"
            })
            .OrderBy(target => target.Y)
            .ThenBy(target => target.X)
            .ToArray();
    }

    private static bool IsFarEnoughFromSelected(
        VisualTargetPayload target,
        IEnumerable<VisualTargetPayload> selectedTargets)
    {
        foreach (var selected in selectedTargets)
        {
            var dx = target.CenterX - selected.CenterX;
            var dy = target.CenterY - selected.CenterY;
            if (Math.Sqrt(dx * dx + dy * dy) < VisualTargetMinSpacing)
            {
                return false;
            }
        }

        return true;
    }

    private static double EstimateVisualInterest(Bitmap bitmap, int x, int y, int width, int height)
    {
        var edgeCount = 0;
        var samples = 0;
        var colorfulness = 0.0;
        const int step = 6;

        for (var sampleY = y; sampleY < y + height - step; sampleY += step)
        {
            for (var sampleX = x; sampleX < x + width - step; sampleX += step)
            {
                var current = bitmap.GetPixel(sampleX, sampleY);
                var right = bitmap.GetPixel(sampleX + step, sampleY);
                var down = bitmap.GetPixel(sampleX, sampleY + step);
                var luma = Luma(current);
                var rightDelta = Math.Abs(luma - Luma(right));
                var downDelta = Math.Abs(luma - Luma(down));
                if (rightDelta > 32 || downDelta > 32)
                {
                    edgeCount++;
                }

                colorfulness += Math.Abs(current.R - current.G) + Math.Abs(current.G - current.B) + Math.Abs(current.B - current.R);
                samples++;
            }
        }

        if (samples == 0)
        {
            return 0;
        }

        var edgeDensity = edgeCount / (double)samples;
        var colorScore = Math.Min(1, colorfulness / samples / 180.0);
        return edgeDensity * 0.8 + colorScore * 0.2;
    }

    private static int Luma(Color color)
    {
        return (int)(0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B);
    }

    private static void DrawVisualAtlas(Graphics graphics, IReadOnlyList<VisualTargetPayload> targets)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var ringPen = new Pen(Color.FromArgb(235, 0, 210, 255), 3);
        using var regionPen = new Pen(Color.FromArgb(220, 255, 190, 0), 2);
        using var leaderPen = new Pen(Color.FromArgb(210, 0, 210, 255), 1);
        using var labelFont = new Font("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);
        using var labelBrush = new SolidBrush(Color.White);
        using var labelBackBrush = new SolidBrush(Color.FromArgb(230, 0, 0, 0));

        foreach (var target in targets)
        {
            var centerX = (float)target.CenterX;
            var centerY = (float)target.CenterY;
            var isRegion = string.Equals(target.Kind, "region", StringComparison.OrdinalIgnoreCase);
            if (isRegion)
            {
                graphics.DrawRectangle(regionPen, (float)target.X, (float)target.Y, (float)target.Width, (float)target.Height);
            }

            graphics.DrawEllipse(ringPen, centerX - 7, centerY - 7, 14, 14);
            graphics.DrawLine(leaderPen, centerX + 8, centerY - 8, centerX + 30, centerY - 22);

            var label = target.Id;
            var labelSize = graphics.MeasureString(label, labelFont);
            var labelX = Math.Min(Math.Max(2, centerX + 30), graphics.VisibleClipBounds.Width - labelSize.Width - 4);
            var labelY = Math.Min(Math.Max(2, centerY - 32), graphics.VisibleClipBounds.Height - labelSize.Height - 4);
            graphics.FillRectangle(labelBackBrush, labelX - 2, labelY - 1, labelSize.Width + 4, labelSize.Height + 2);
            graphics.DrawString(label, labelFont, labelBrush, labelX, labelY);
        }
    }

    private static void DrawGuideLabel(
        Graphics graphics,
        string text,
        float x,
        float y,
        Font font,
        Brush textBrush,
        Brush backgroundBrush)
    {
        var size = graphics.MeasureString(text, font);
        graphics.FillRectangle(backgroundBrush, x, y, size.Width, size.Height);
        graphics.DrawString(text, font, textBrush, x, y);
    }

    private static IReadOnlyList<ScreenElementPayload> CaptureAccessibleElements(
        Rectangle screenBounds,
        int screenNumber,
        Point cursorPosition,
        TimeSpan budget)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var screenArea = screenBounds.Width * screenBounds.Height;
            var candidates = new List<(ScreenElementPayload Element, double Score)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in GetAccessibleRoots(cursorPosition))
            {
                if (stopwatch.Elapsed >= budget || candidates.Count >= AccessibleElementLimit)
                {
                    break;
                }

                var windowTitle = TryGetWindowTitle(root);
                CollectAccessibleElements(
                    root,
                    screenBounds,
                    screenNumber,
                    screenArea,
                    windowTitle,
                    seen,
                    candidates,
                    stopwatch,
                    budget,
                    depth: 0);
            }

            return candidates
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Element.Y)
                .ThenBy(candidate => candidate.Element.X)
                .Take(AccessibleElementLimit)
                .Select(candidate => candidate.Element)
                .ToArray();
        }
        catch (Exception error)
        {
            AppLogger.Error("Accessible element capture failed", error);
            return [];
        }
    }

    private static IEnumerable<AutomationElement> GetAccessibleRoots(Point cursorPosition)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AutomationElement? foregroundElement = null;
        try
        {
            var foregroundWindow = GetForegroundWindow();
            if (foregroundWindow != IntPtr.Zero)
            {
                foregroundElement = AutomationElement.FromHandle(foregroundWindow);
            }
        }
        catch
        {
        }

        if (foregroundElement is not null && TryGetRuntimeKey(foregroundElement) is { } foregroundKey && seen.Add(foregroundKey))
        {
            yield return foregroundElement;
        }

        foreach (var element in TryGetPointAncestry(cursorPosition))
        {
            if (TryGetRuntimeKey(element) is { } key && seen.Add(key))
            {
                yield return element;
            }
        }

        AutomationElement root;
        try
        {
            root = AutomationElement.RootElement;
        }
        catch
        {
            yield break;
        }

        AutomationElement? child = null;
        try
        {
            child = TreeWalker.ControlViewWalker.GetFirstChild(root);
        }
        catch
        {
        }

        while (child is not null)
        {
            if (TryGetRuntimeKey(child) is { } key && seen.Add(key))
            {
                yield return child;
            }

            try
            {
                child = TreeWalker.ControlViewWalker.GetNextSibling(child);
            }
            catch
            {
                yield break;
            }
        }
    }

    private static IEnumerable<AutomationElement> TryGetPointAncestry(Point cursorPosition)
    {
        AutomationElement? element;
        try
        {
            element = AutomationElement.FromPoint(new System.Windows.Point(cursorPosition.X, cursorPosition.Y));
        }
        catch
        {
            yield break;
        }

        while (element is not null)
        {
            yield return element;
            try
            {
                element = TreeWalker.ControlViewWalker.GetParent(element);
            }
            catch
            {
                yield break;
            }
        }
    }

    private static void CollectAccessibleElements(
        AutomationElement root,
        Rectangle screenBounds,
        int screenNumber,
        int screenArea,
        string? windowTitle,
        HashSet<string> seen,
        List<(ScreenElementPayload Element, double Score)> candidates,
        Stopwatch stopwatch,
        TimeSpan budget,
        int depth)
    {
        if (stopwatch.Elapsed >= budget || candidates.Count >= AccessibleElementLimit || depth > 10)
        {
            return;
        }

        var candidate = TryCreateElementPayload(root, screenBounds, screenNumber, screenArea, windowTitle, seen);
        if (candidate is not null)
        {
            candidates.Add(candidate.Value);
        }

        AutomationElement? child;
        try
        {
            child = TreeWalker.ControlViewWalker.GetFirstChild(root);
        }
        catch
        {
            return;
        }

        while (child is not null && stopwatch.Elapsed < budget && candidates.Count < AccessibleElementLimit)
        {
            CollectAccessibleElements(
                child,
                screenBounds,
                screenNumber,
                screenArea,
                windowTitle,
                seen,
                candidates,
                stopwatch,
                budget,
                depth + 1);

            try
            {
                child = TreeWalker.ControlViewWalker.GetNextSibling(child);
            }
            catch
            {
                return;
            }
        }
    }

    private static string? TryGetRuntimeKey(AutomationElement element)
    {
        try
        {
            return string.Join(".", element.GetRuntimeId());
        }
        catch
        {
            return null;
        }
    }

    private static (ScreenElementPayload Element, double Score)? TryCreateElementPayload(
        AutomationElement automationElement,
        Rectangle screenBounds,
        int screenNumber,
        int screenArea,
        string? windowTitle,
        HashSet<string> seen)
    {
        WpfRect rect;
        string name;
        ControlType controlType;
        bool isOffscreen;
        try
        {
            rect = automationElement.Current.BoundingRectangle;
            name = automationElement.Current.Name ?? "";
            controlType = automationElement.Current.ControlType;
            isOffscreen = automationElement.Current.IsOffscreen;
        }
        catch
        {
            return null;
        }

        if (isOffscreen || string.IsNullOrWhiteSpace(name) || rect.IsEmpty)
        {
            return null;
        }

        if (rect.Width < 4 || rect.Height < 4)
        {
            return null;
        }

        if (rect.Right < screenBounds.Left ||
            rect.Left > screenBounds.Right ||
            rect.Bottom < screenBounds.Top ||
            rect.Top > screenBounds.Bottom)
        {
            return null;
        }

        var clippedLeft = Math.Max(rect.Left, screenBounds.Left);
        var clippedTop = Math.Max(rect.Top, screenBounds.Top);
        var clippedRight = Math.Min(rect.Right, screenBounds.Right);
        var clippedBottom = Math.Min(rect.Bottom, screenBounds.Bottom);
        var width = clippedRight - clippedLeft;
        var height = clippedBottom - clippedTop;
        var area = width * height;
        if (area < 16 || area > screenArea * 0.35)
        {
            return null;
        }

        var normalizedName = NormalizeElementName(name);
        if (normalizedName.Length == 0)
        {
            return null;
        }

        var controlName = controlType.ProgrammaticName.Replace("ControlType.", "", StringComparison.OrdinalIgnoreCase);
        var localLeft = clippedLeft - screenBounds.Left;
        var localTop = clippedTop - screenBounds.Top;
        var centerX = localLeft + width / 2;
        var centerY = localTop + height / 2;
        var key = $"{normalizedName}|{controlName}|{Math.Round(localLeft)}|{Math.Round(localTop)}|{Math.Round(width)}|{Math.Round(height)}";
        if (!seen.Add(key))
        {
            return null;
        }

        var isClickable = IsClickableControl(controlType);
        var score = (isClickable ? 100 : 35) +
                    Math.Min(normalizedName.Length, 40) +
                    Math.Max(0, 60 - Math.Sqrt(area) / 8);

        var element = new ScreenElementPayload(
            $"screen{screenNumber}-el{seen.Count}",
            normalizedName,
            controlName,
            Math.Round(localLeft, 1),
            Math.Round(localTop, 1),
            Math.Round(width, 1),
            Math.Round(height, 1),
            Math.Round(centerX, 1),
            Math.Round(centerY, 1),
            windowTitle,
            isClickable,
            Math.Round(score, 1));
        return (element, score);
    }

    private static string? TryGetWindowTitle(AutomationElement element)
    {
        try
        {
            return NormalizeElementName(element.Current.Name ?? "");
        }
        catch
        {
            return null;
        }
    }

    private static void LogVisualAtlasDiagnostic(
        int screenNumber,
        Rectangle bounds,
        IReadOnlyList<VisualTargetPayload> targets,
        TimeSpan elapsed)
    {
        AppLogger.Info(
            $"Visual atlas diagnostic. Screen={screenNumber} Bounds={bounds} Count={targets.Count} " +
            $"BudgetMs={AccessibleElementBudget.TotalMilliseconds:0} ElapsedMs={elapsed.TotalMilliseconds:0}");

        foreach (var target in targets.Take(12))
        {
            AppLogger.Info(
                $"Visual target. Screen={screenNumber} Id={target.Id} Kind={target.Kind} " +
                $"Bounds={target.X:0.0},{target.Y:0.0},{target.Width:0.0},{target.Height:0.0} " +
                $"Center={target.CenterX:0.0},{target.CenterY:0.0} Confidence={target.Confidence:0.000}");
        }
    }

    private static string NormalizeElementName(string name)
    {
        return string.Join(" ", name.Split(['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private static bool IsClickableControl(ControlType controlType)
    {
        return controlType == ControlType.Button ||
               controlType == ControlType.Hyperlink ||
               controlType == ControlType.MenuItem ||
               controlType == ControlType.TabItem ||
               controlType == ControlType.ListItem ||
               controlType == ControlType.Edit ||
               controlType == ControlType.ComboBox ||
               controlType == ControlType.CheckBox ||
               controlType == ControlType.RadioButton ||
               controlType == ControlType.SplitButton;
    }

    private static (double X, double Y) GetDpiScale(Rectangle bounds)
    {
        try
        {
            var monitor = MonitorFromPoint(
                new NativePoint(bounds.Left + Math.Max(1, bounds.Width / 2), bounds.Top + Math.Max(1, bounds.Height / 2)),
                2);
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out var dpiX, out var dpiY) == 0)
            {
                return (dpiX / 96.0, dpiY / 96.0);
            }
        }
        catch
        {
        }

        return (1, 1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
