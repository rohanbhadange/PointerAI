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
    private AppSettingsStore? settingsStore;

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

        settingsStore = new AppSettingsStore();
        var backendClient = ResolveBackendClient(settingsStore);
        if (backendClient is null)
        {
            Shutdown();
            return;
        }

        var overlayManager = new OverlayManager();
        var audioRecorder = new AudioRecorder();
        var transcriptionClient = new AssemblyAIStreamingClient(backendClient);
        var screenCaptureService = new ScreenCaptureService();
        var textToSpeechPlayer = new TextToSpeechPlayer(backendClient);
        var hotkeyMonitor = new GlobalPushToTalkMonitor();

        companionManager = new CompanionManager(
            backendClient,
            overlayManager,
            audioRecorder,
            transcriptionClient,
            screenCaptureService,
            textToSpeechPlayer,
            hotkeyMonitor);

        trayController = new TrayController(companionManager, OpenSetupFromTray);
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

    private static IBackendClient? ResolveBackendClient(AppSettingsStore settingsStore)
    {
        var settings = settingsStore.Load();
        if (TryCreateConfiguredBackend(settings, out var configuredBackend) && IsBackendReady(configuredBackend))
        {
            return configuredBackend;
        }

        var setupWindow = new WorkerSetupWindow(settingsStore);
        if (setupWindow.ShowDialog() != true)
        {
            return null;
        }

        settings = settingsStore.Load();
        if (TryCreateConfiguredBackend(settings, out configuredBackend) && IsBackendReady(configuredBackend))
        {
            return configuredBackend;
        }

        System.Windows.MessageBox.Show(
            "ClickyClone could not confirm setup. Open setup again and check the missing items.",
            "ClickyClone setup",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return null;
    }

    private static bool TryCreateConfiguredBackend(AppSettings settings, out IBackendClient backendClient)
    {
        if (string.Equals(settings.BackendMode, "local", StringComparison.OrdinalIgnoreCase))
        {
            backendClient = new LocalProviderClient(LocalEnv.AppEnvPath);
            return true;
        }

        if (settings.UseDeveloperWorker)
        {
            backendClient = new WorkerClient(AppConfig.WorkerBaseUri);
            return true;
        }

        if (Uri.TryCreate(settings.WorkerBaseUrl, UriKind.Absolute, out var workerUri) &&
            (workerUri.Scheme == Uri.UriSchemeHttp || workerUri.Scheme == Uri.UriSchemeHttps))
        {
            backendClient = new WorkerClient(workerUri);
            return true;
        }

        backendClient = null!;
        return false;
    }

    private static bool IsBackendReady(IBackendClient backendClient)
    {
        try
        {
            return Task.Run(async () =>
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await backendClient.CheckHealthAsync(cancellation.Token);
                var diagnostics = await backendClient.GetDiagnosticsAsync(cancellation.Token);
                return diagnostics.Secrets is
                {
                    OpenAI: true,
                    AssemblyAI: true,
                    ElevenLabs: true,
                    ElevenLabsVoice: true
                };
            }).GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            AppLogger.Error("Configured backend readiness check failed", error);
            return false;
        }
    }

    private void OpenSetupFromTray()
    {
        if (settingsStore is null)
        {
            return;
        }

        var setupWindow = new WorkerSetupWindow(settingsStore);
        if (setupWindow.ShowDialog() == true)
        {
            System.Windows.MessageBox.Show(
                "Worker setup was updated. Restart ClickyClone to use the new Worker.",
                "ClickyClone setup",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }
}
