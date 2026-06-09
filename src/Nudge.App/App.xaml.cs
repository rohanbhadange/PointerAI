using System.Windows;
using Nudge.Core;
using Nudge.Services;
using Nudge.UI;
using System.Diagnostics;

namespace Nudge;

public partial class App : System.Windows.Application
{
    private TrayController? trayController;
    private CompanionManager? companionManager;
    private AppSettingsStore? settingsStore;
    private UpdateService? updateService;

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
        updateService = new UpdateService();
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

        trayController = new TrayController(companionManager, OpenSetupFromTray, CheckForUpdatesFromTrayAsync);
        companionManager.Start();
        _ = CheckForUpdatesOnStartupAsync();
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

        foreach (var process in Process.GetProcessesByName("Nudge"))
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
            "Nudge could not confirm setup. Open setup again and check the missing items.",
            "Nudge setup",
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
                "Setup was updated. Restart Nudge to use the new setup path.",
                "Nudge setup",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private async Task CheckForUpdatesFromTrayAsync()
    {
        await CheckForUpdatesAsync(userInitiated: true);
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
            await CheckForUpdatesAsync(userInitiated: false);
        }
        catch (Exception error)
        {
            AppLogger.Error("Startup update check failed", error);
        }
    }

    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        var service = updateService;
        var store = settingsStore;
        if (service is null || store is null)
        {
            return;
        }

        var settings = store.Load();
        var result = await service.CheckForUpdatesAsync(CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            if (userInitiated)
            {
                await Dispatcher.InvokeAsync(() => System.Windows.MessageBox.Show(
                    result.Error,
                    "Update check failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning));
            }

            return;
        }

        if (!result.UpdateAvailable)
        {
            if (userInitiated)
            {
                await Dispatcher.InvokeAsync(() => System.Windows.MessageBox.Show(
                    $"Nudge is up to date. Current version: {result.CurrentVersion}.",
                    "No update available",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information));
            }

            return;
        }

        var message = BuildUpdatePrompt(result, settings);
        var shouldUpdate = await Dispatcher.InvokeAsync(() => System.Windows.MessageBox.Show(
            message,
            "Nudge update available",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information));

        if (shouldUpdate != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await Dispatcher.InvokeAsync(() => System.Windows.MessageBox.Show(
                "Nudge will download the update now. The app will close when the installer starts.",
                "Downloading update",
                MessageBoxButton.OK,
                MessageBoxImage.Information));

            var installerPath = await service.DownloadInstallerAsync(result, null, CancellationToken.None);
            service.LaunchInstaller(installerPath);
            Shutdown();
        }
        catch (Exception error)
        {
            AppLogger.Error("Update install failed", error);
            await Dispatcher.InvokeAsync(() => System.Windows.MessageBox.Show(
                $"The update could not be installed automatically. You can download it from {result.ReleasePageUrl ?? AppConfig.GitHubReleasesUri.ToString()}.\n\n{error.Message}",
                "Update failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning));
        }
    }

    private static string BuildUpdatePrompt(UpdateCheckResult result, AppSettings settings)
    {
        var setupNote = string.Equals(settings.BackendMode, "local", StringComparison.OrdinalIgnoreCase)
            ? "Your local .env keys will stay in place."
            : "Your Worker URL will stay in place. If this release includes Worker changes, the app may ask you to redeploy your Worker after updating.";

        return $"Version {result.LatestVersion} is available. You are running {result.CurrentVersion}.\n\n{setupNote}\n\nUpdate now?";
    }
}
