using System.Windows.Forms;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Nudge.Core;
using Nudge.Services;
using WpfPoint = System.Windows.Point;

namespace Nudge.UI;

public sealed class OverlayManager : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const double PinnedClickTolerancePixels = 96;

    private readonly List<OverlayWindow> windows = [];
    private readonly LowLevelMouseProc mouseProc;
    private IntPtr mouseHookId;
    private WpfPoint? pinnedTarget;

    public OverlayManager()
    {
        mouseProc = HandleMouseHook;
    }

    public void Show()
    {
        if (windows.Count > 0)
        {
            return;
        }

        foreach (var screen in Screen.AllScreens)
        {
            var overlayWindow = new OverlayWindow(screen.Bounds);
            windows.Add(overlayWindow);
            overlayWindow.Show();
        }

        StartMouseHook();
    }

    public void SetVoiceState(CompanionVoiceState voiceState)
    {
        foreach (var window in windows)
        {
            window.SetVoiceState(voiceState);
        }
    }

    public void SetAudioLevel(double audioLevel)
    {
        foreach (var window in windows)
        {
            window.SetAudioLevel(audioLevel);
        }
    }

    public void PointAt(PointTarget pointTarget)
    {
        pinnedTarget = pointTarget.DesktopPoint;
        foreach (var window in windows)
        {
            window.PointAt(pointTarget);
        }
    }

    public void ClearPointTarget()
    {
        pinnedTarget = null;
        foreach (var window in windows)
        {
            window.ClearPointTarget();
        }
    }

    public void SuspendVisualsForCapture()
    {
        foreach (var window in windows)
        {
            window.SuspendVisualsForCapture();
        }
    }

    public void ResumeVisualsAfterCapture()
    {
        foreach (var window in windows)
        {
            window.ResumeVisualsAfterCapture();
        }
    }

    public void Dispose()
    {
        StopMouseHook();
        foreach (var window in windows)
        {
            window.Close();
        }

        windows.Clear();
    }

    private void StartMouseHook()
    {
        if (mouseHookId != IntPtr.Zero)
        {
            return;
        }

        using var currentProcess = Process.GetCurrentProcess();
        using var currentModule = currentProcess.MainModule;
        mouseHookId = SetWindowsHookEx(
            WH_MOUSE_LL,
            mouseProc,
            GetModuleHandle(currentModule?.ModuleName),
            0);

        if (mouseHookId == IntPtr.Zero)
        {
            AppLogger.Error("Global mouse hook failed", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }
        else
        {
            AppLogger.Info("Global mouse hook started.");
        }
    }

    private void StopMouseHook()
    {
        if (mouseHookId == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(mouseHookId);
        mouseHookId = IntPtr.Zero;
        AppLogger.Info("Global mouse hook stopped.");
    }

    private IntPtr HandleMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var message = wParam.ToInt32();
        if (nCode >= 0 && IsPinnedReleaseClick(message) && pinnedTarget is WpfPoint target)
        {
            var buttonName = message == WM_RBUTTONDOWN ? "right" : "left";
            var info = Marshal.PtrToStructure<MouseHookStruct>(lParam);
            var dx = info.Point.X - target.X;
            var dy = info.Point.Y - target.Y;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance <= PinnedClickTolerancePixels)
            {
                AppLogger.Info($"Pinned point cleared by {buttonName} click. Distance={distance:0.0}");
                System.Windows.Application.Current.Dispatcher.InvokeAsync(ClearPointTarget);
            }
            else if (distance <= 240)
            {
                AppLogger.Info($"Pinned point {buttonName} click was outside tolerance. Distance={distance:0.0} ClickX={info.Point.X} ClickY={info.Point.Y} TargetX={target.X:0.0} TargetY={target.Y:0.0}");
            }
        }

        return CallNextHookEx(mouseHookId, nCode, wParam, lParam);
    }

    private static bool IsPinnedReleaseClick(int message)
    {
        return message is WM_LBUTTONDOWN or WM_RBUTTONDOWN;
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookStruct
    {
        public NativePoint Point;
        public int MouseData;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelMouseProc lpfn,
        IntPtr hMod,
        uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
