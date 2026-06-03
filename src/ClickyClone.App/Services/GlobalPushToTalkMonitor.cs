using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace ClickyClone.Services;

public sealed class GlobalPushToTalkMonitor : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int VK_CONTROL = 0x11;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_MENU = 0x12;
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;

    private readonly LowLevelKeyboardProc keyboardProc;
    private IntPtr hookId = IntPtr.Zero;
    private bool isShortcutPressed;
    private bool isCtrlDown;
    private bool isAltDown;

    public event EventHandler? Pressed;
    public event EventHandler? Released;

    public GlobalPushToTalkMonitor()
    {
        keyboardProc = HookCallback;
    }

    public void Start()
    {
        if (hookId != IntPtr.Zero)
        {
            return;
        }

        using var currentProcess = Process.GetCurrentProcess();
        using var currentModule = currentProcess.MainModule;
        hookId = SetWindowsHookEx(
            WH_KEYBOARD_LL,
            keyboardProc,
            GetModuleHandle(currentModule?.ModuleName),
            0);

        if (hookId == IntPtr.Zero)
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            AppLogger.Error("Global keyboard hook failed", error);
            throw error;
        }

        AppLogger.Info("Global push-to-talk hook started.");
    }

    public void Stop()
    {
        if (hookId == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(hookId);
        hookId = IntPtr.Zero;
        isShortcutPressed = false;
        AppLogger.Info("Global push-to-talk hook stopped.");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = wParam.ToInt32();
            if (message is WM_KEYDOWN or WM_KEYUP or WM_SYSKEYDOWN or WM_SYSKEYUP)
            {
                var keyInfo = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                UpdateKeyState(keyInfo.VkCode, message is WM_KEYDOWN or WM_SYSKEYDOWN);
                UpdateShortcutState();
            }
        }

        return CallNextHookEx(hookId, nCode, wParam, lParam);
    }

    private void UpdateShortcutState()
    {
        var isCurrentlyPressed = isCtrlDown && isAltDown;

        if (isCurrentlyPressed && !isShortcutPressed)
        {
            isShortcutPressed = true;
            AppLogger.Info("Push-to-talk pressed.");
            Pressed?.Invoke(this, EventArgs.Empty);
        }
        else if (!isCurrentlyPressed && isShortcutPressed)
        {
            isShortcutPressed = false;
            AppLogger.Info("Push-to-talk released.");
            Released?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateKeyState(int virtualKey, bool isKeyDown)
    {
        switch (virtualKey)
        {
            case VK_CONTROL:
            case VK_LCONTROL:
            case VK_RCONTROL:
                isCtrlDown = isKeyDown;
                break;
            case VK_MENU:
            case VK_LMENU:
            case VK_RMENU:
                isAltDown = isKeyDown;
                break;
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public int VkCode;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr DwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelKeyboardProc lpfn,
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
