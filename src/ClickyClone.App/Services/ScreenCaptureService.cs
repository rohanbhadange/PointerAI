using System.Drawing;
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

                var elementStopwatch = Stopwatch.StartNew();
                var elements = CaptureAccessibleElements(bounds, index + 1, cursorPosition, AccessibleElementBudget);
                LogAccessibleElementDiagnostic(index + 1, bounds, elements, elementStopwatch.Elapsed);
                AppLogger.Info($"PERF stage=uia_catalog screen={index + 1} ms={elementStopwatch.ElapsedMilliseconds} elements={elements.Count}");

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
                    elements));
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

    private static void LogAccessibleElementDiagnostic(
        int screenNumber,
        Rectangle bounds,
        IReadOnlyList<ScreenElementPayload> elements,
        TimeSpan elapsed)
    {
        AppLogger.Info(
            $"UIA diagnostic. Screen={screenNumber} Bounds={bounds} Count={elements.Count} " +
            $"BudgetMs={AccessibleElementBudget.TotalMilliseconds:0} ElapsedMs={elapsed.TotalMilliseconds:0}");

        foreach (var element in elements.Take(12))
        {
            AppLogger.Info(
                $"UIA candidate. Screen={screenNumber} Id={element.Id} Name=\"{element.Name}\" Type={element.ControlType} " +
                $"Window=\"{element.WindowTitle ?? ""}\" Clickable={element.IsClickable} " +
                $"Bounds={element.X:0.0},{element.Y:0.0},{element.Width:0.0},{element.Height:0.0} " +
                $"Center={element.CenterX:0.0},{element.CenterY:0.0} Score={element.Score:0.0}");
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
