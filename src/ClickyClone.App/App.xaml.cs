using System.Windows;
using ClickyClone.Core;
using ClickyClone.Services;
using ClickyClone.UI;
using System.Diagnostics;

namespace ClickyClone;

public partial class App : System.Windows.Application
{
    private TrayController? trayController;
    private CompanionManager? companionManager;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Any(arg => string.Equals(arg, "--quit", StringComparison.OrdinalIgnoreCase)))
        {
            QuitOtherCopiesFromSameInstallPath();
            Shutdown();
            return;
        }

        AppLogger.Reset();
        AppLogger.Info("Application startup.");
        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Error("Unhandled dispatcher exception", args.Exception);
            args.Handled = true;
        };

        var workerClient = new WorkerClient(AppConfig.WorkerBaseUri);
        var overlayManager = new OverlayManager();
        var audioRecorder = new AudioRecorder();
        var transcriptionClient = new AssemblyAIStreamingClient(workerClient);
        var screenCaptureService = new ScreenCaptureService();
        var textToSpeechPlayer = new TextToSpeechPlayer(workerClient);
        var hotkeyMonitor = new GlobalPushToTalkMonitor();

        companionManager = new CompanionManager(
            workerClient,
            overlayManager,
            audioRecorder,
            transcriptionClient,
            screenCaptureService,
            textToSpeechPlayer,
            hotkeyMonitor);

        trayController = new TrayController(companionManager);
        companionManager.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLogger.Info("Application exit.");
        companionManager?.Dispose();
        trayController?.Dispose();
        base.OnExit(e);
    }

    private static void QuitOtherCopiesFromSameInstallPath()
    {
        using var currentProcess = Process.GetCurrentProcess();
        var currentPath = currentProcess.MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            return;
        }

        foreach (var process in Process.GetProcessesByName("ClickyClone"))
        {
            using (process)
            {
                if (process.Id == currentProcess.Id)
                {
                    continue;
                }

                try
                {
                    if (!string.Equals(process.MainModule?.FileName, currentPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
                catch
                {
                    // The quit shortcut is a last-resort control path; ignore protected/racing processes.
                }
            }
        }
    }
}
