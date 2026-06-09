using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ClickyClone.Core;
using Forms = System.Windows.Forms;
using Color = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;
using WpfRectangle = System.Windows.Shapes.Rectangle;

namespace ClickyClone.UI;

public sealed class OverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const double FollowOffsetX = 16;
    private const double FollowOffsetY = 12;

    private readonly System.Drawing.Rectangle screenBounds;
    private readonly double dpiScaleX;
    private readonly double dpiScaleY;
    private readonly Canvas canvas = new();
    private readonly Polygon cursorTriangle = new();
    private readonly WpfRectangle targetBox = new();
    private readonly List<WpfRectangle> waveformBars = [];
    private readonly Ellipse spinner = new();
    private readonly DispatcherTimer followTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer spinnerTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private double spinnerAngle;
    private CompanionVoiceState voiceState = CompanionVoiceState.Idle;
    private WpfPoint cursorPosition;
    private DispatcherTimer? flightTimer;
    private bool areVisualsSuspendedForCapture;
    private bool isPinned;

    public OverlayWindow(System.Drawing.Rectangle screenBounds)
    {
        this.screenBounds = screenBounds;
        (dpiScaleX, dpiScaleY) = GetDpiScale(screenBounds);

        Left = screenBounds.Left / dpiScaleX;
        Top = screenBounds.Top / dpiScaleY;
        Width = screenBounds.Width / dpiScaleX;
        Height = screenBounds.Height / dpiScaleY;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        ShowInTaskbar = false;
        Topmost = true;
        Content = canvas;

        BuildVisuals();

        followTimer.Tick += (_, _) => FollowCursor();
        followTimer.Start();

        spinnerTimer.Tick += (_, _) =>
        {
            spinnerAngle = (spinnerAngle + 8) % 360;
            spinner.RenderTransform = new RotateTransform(spinnerAngle, 8, 8);
        };
        spinnerTimer.Start();
    }

    public void SetVoiceState(CompanionVoiceState newVoiceState)
    {
        voiceState = newVoiceState;
        UpdateVisualState();
    }

    public void SetAudioLevel(double audioLevel)
    {
        for (var index = 0; index < waveformBars.Count; index++)
        {
            var profile = index switch
            {
                0 or 4 => 0.45,
                1 or 3 => 0.75,
                _ => 1.0
            };

            waveformBars[index].Height = 4 + Math.Clamp(audioLevel, 0, 1) * 18 * profile;
        }
    }

    public void PointAt(PointTarget pointTarget)
    {
        if (!screenBounds.Contains((int)pointTarget.DesktopPoint.X, (int)pointTarget.DesktopPoint.Y))
        {
            ClearPointTarget();
            return;
        }

        var target = new WpfPoint(
            (pointTarget.DesktopPoint.X - screenBounds.Left) / dpiScaleX,
            (pointTarget.DesktopPoint.Y - screenBounds.Top) / dpiScaleY);

        isPinned = true;
        ShowTargetBox(pointTarget);
        AnimateTo(target, () => { });
    }

    public void ClearPointTarget()
    {
        isPinned = false;
        flightTimer?.Stop();
        flightTimer = null;
        targetBox.Visibility = Visibility.Collapsed;
    }

    public void SuspendVisualsForCapture()
    {
        areVisualsSuspendedForCapture = true;
        HideAll();
    }

    public void ResumeVisualsAfterCapture()
    {
        areVisualsSuspendedForCapture = false;
        UpdateVisualState();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var extendedStyle = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW);
    }

    private void BuildVisuals()
    {
        cursorTriangle.Points = new PointCollection
        {
            new(0, 0),
            new(-8, 18),
            new(10, 12)
        };
        Canvas.SetZIndex(cursorTriangle, 2);
        cursorTriangle.Fill = new SolidColorBrush(Color.FromRgb(45, 160, 255));
        cursorTriangle.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Color.FromRgb(45, 160, 255),
            BlurRadius = 12,
            Opacity = 0.9
        };
        canvas.Children.Add(cursorTriangle);

        targetBox.Stroke = new SolidColorBrush(Color.FromRgb(45, 160, 255));
        targetBox.StrokeThickness = 3;
        targetBox.RadiusX = 4;
        targetBox.RadiusY = 4;
        targetBox.Fill = new SolidColorBrush(Color.FromArgb(28, 45, 160, 255));
        targetBox.Visibility = Visibility.Collapsed;
        Canvas.SetZIndex(targetBox, 1);
        targetBox.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Color.FromRgb(45, 160, 255),
            BlurRadius = 16,
            Opacity = 0.85
        };
        canvas.Children.Add(targetBox);

        for (var index = 0; index < 5; index++)
        {
            var bar = new WpfRectangle
            {
                Width = 3,
                Height = 8,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = new SolidColorBrush(Color.FromRgb(45, 160, 255)),
                Visibility = Visibility.Collapsed
            };
            waveformBars.Add(bar);
            canvas.Children.Add(bar);
        }

        spinner.Width = 16;
        spinner.Height = 16;
        spinner.StrokeThickness = 3;
        spinner.Stroke = new SolidColorBrush(Color.FromRgb(45, 160, 255));
        spinner.Visibility = Visibility.Collapsed;
        canvas.Children.Add(spinner);

    }

    private void FollowCursor()
    {
        var cursor = Forms.Cursor.Position;
        if (!screenBounds.Contains(cursor))
        {
            HideAll();
            return;
        }

        if (flightTimer is not null || areVisualsSuspendedForCapture || isPinned)
        {
            return;
        }

        cursorPosition = new WpfPoint(
            (cursor.X - screenBounds.Left) / dpiScaleX + FollowOffsetX,
            (cursor.Y - screenBounds.Top) / dpiScaleY + FollowOffsetY);
        PositionVisuals();
        UpdateVisualState();
    }

    private void PositionVisuals()
    {
        Canvas.SetLeft(cursorTriangle, cursorPosition.X);
        Canvas.SetTop(cursorTriangle, cursorPosition.Y);
        Canvas.SetLeft(spinner, cursorPosition.X - 8);
        Canvas.SetTop(spinner, cursorPosition.Y - 8);

        for (var index = 0; index < waveformBars.Count; index++)
        {
            Canvas.SetLeft(waveformBars[index], cursorPosition.X - 9 + index * 5);
            Canvas.SetTop(waveformBars[index], cursorPosition.Y - waveformBars[index].Height / 2);
        }
    }

    private void UpdateVisualState()
    {
        if (areVisualsSuspendedForCapture)
        {
            HideAll();
            return;
        }

        if (isPinned)
        {
            cursorTriangle.Visibility = Visibility.Visible;
            if (targetBox.Width > 0 && targetBox.Height > 0)
            {
                targetBox.Visibility = Visibility.Visible;
            }

            spinner.Visibility = Visibility.Collapsed;
            foreach (var bar in waveformBars)
            {
                bar.Visibility = Visibility.Collapsed;
            }

            return;
        }

        cursorTriangle.Visibility = voiceState is CompanionVoiceState.Idle or CompanionVoiceState.Responding
            ? Visibility.Visible
            : Visibility.Collapsed;

        spinner.Visibility = voiceState == CompanionVoiceState.Processing
            ? Visibility.Visible
            : Visibility.Collapsed;

        foreach (var bar in waveformBars)
        {
            bar.Visibility = voiceState == CompanionVoiceState.Listening
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void HideAll()
    {
        cursorTriangle.Visibility = Visibility.Collapsed;
        targetBox.Visibility = Visibility.Collapsed;
        spinner.Visibility = Visibility.Collapsed;
        foreach (var bar in waveformBars)
        {
            bar.Visibility = Visibility.Collapsed;
        }
    }

    private void AnimateTo(WpfPoint target, Action onComplete)
    {
        flightTimer?.Stop();

        var start = cursorPosition;
        var distance = Math.Sqrt(Math.Pow(target.X - start.X, 2) + Math.Pow(target.Y - start.Y, 2));
        var duration = TimeSpan.FromMilliseconds(Math.Clamp(distance / 800.0 * 1000.0, 600, 1400));
        var startedAt = DateTime.UtcNow;
        var control = new WpfPoint((start.X + target.X) / 2, Math.Min(start.Y, target.Y) - Math.Min(distance * 0.2, 80));

        flightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        flightTimer.Tick += (_, _) =>
        {
            var progress = Math.Clamp((DateTime.UtcNow - startedAt).TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
            var eased = progress * progress * (3 - 2 * progress);
            var oneMinus = 1 - eased;

            cursorPosition = new WpfPoint(
                oneMinus * oneMinus * start.X + 2 * oneMinus * eased * control.X + eased * eased * target.X,
                oneMinus * oneMinus * start.Y + 2 * oneMinus * eased * control.Y + eased * eased * target.Y);

            PositionVisuals();
            cursorTriangle.Visibility = Visibility.Visible;
            spinner.Visibility = Visibility.Collapsed;

            if (progress >= 1)
            {
                flightTimer?.Stop();
                flightTimer = null;
                onComplete();
            }
        };
        flightTimer.Start();
    }

    private void ShowTargetBox(PointTarget pointTarget)
    {
        if (pointTarget.DesktopBounds is not { } bounds || bounds.Width <= 0 || bounds.Height <= 0)
        {
            targetBox.Visibility = Visibility.Collapsed;
            return;
        }

        var left = (bounds.Left - screenBounds.Left) / dpiScaleX;
        var top = (bounds.Top - screenBounds.Top) / dpiScaleY;
        var width = bounds.Width / dpiScaleX;
        var height = bounds.Height / dpiScaleY;

        targetBox.Width = Math.Max(8, width);
        targetBox.Height = Math.Max(8, height);
        Canvas.SetLeft(targetBox, left);
        Canvas.SetTop(targetBox, top);
        targetBox.Visibility = Visibility.Visible;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private static (double X, double Y) GetDpiScale(System.Drawing.Rectangle bounds)
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

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
