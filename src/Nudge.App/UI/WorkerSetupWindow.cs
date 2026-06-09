using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Nudge.Core;
using Nudge.Services;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfButton = System.Windows.Controls.Button;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Nudge.UI;

public sealed class WorkerSetupWindow : Window
{
    private readonly AppSettingsStore settingsStore;
    private readonly WorkerSetupRunner setupRunner = new();
    private readonly WpfTextBox workerUrlBox = new();
    private readonly PasswordBox localOpenAiKeyBox = new();
    private readonly PasswordBox localAssemblyAiKeyBox = new();
    private readonly PasswordBox localElevenLabsKeyBox = new();
    private readonly PasswordBox localElevenLabsVoiceIdBox = new();
    private readonly TextBlock statusText = new();
    private readonly TextBlock diagnosticsText = new();
    private readonly WpfButton validateButton = new();
    private readonly WpfButton localSaveButton = new();
    private readonly WpfButton developerWorkerButton = new();

    public WorkerSetupWindow(AppSettingsStore settingsStore)
    {
        this.settingsStore = settingsStore;
        Title = "Nudge setup";
        Width = 620;
        Height = 720;
        MinWidth = 560;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanResize;
        Content = BuildContent();
    }

    private UIElement BuildContent()
    {
        var root = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Margin = new Thickness(28),
                Orientation = WpfOrientation.Vertical
            }
        };

        var panel = (StackPanel)root.Content;
        panel.Children.Add(new TextBlock
        {
            Text = "Set up Nudge",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Choose whether Nudge should use your own Cloudflare Worker or local keys stored on this computer.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 20)
        });

        panel.Children.Add(SectionHeader("Use local keys on this computer"));
        panel.Children.Add(BodyText($"No Cloudflare Worker required. Setup will create {LocalEnv.AppEnvPath}."));
        panel.Children.Add(InputLabel("OpenAI API key"));
        panel.Children.Add(localOpenAiKeyBox);
        panel.Children.Add(InputLabel("AssemblyAI API key"));
        panel.Children.Add(localAssemblyAiKeyBox);
        panel.Children.Add(InputLabel("ElevenLabs API key"));
        panel.Children.Add(localElevenLabsKeyBox);
        panel.Children.Add(InputLabel("ElevenLabs voice ID"));
        panel.Children.Add(localElevenLabsVoiceIdBox);

        localSaveButton.Content = "Use local keys";
        localSaveButton.Margin = new Thickness(0, 16, 0, 24);
        localSaveButton.HorizontalAlignment = WpfHorizontalAlignment.Left;
        localSaveButton.Padding = new Thickness(16, 8, 16, 8);
        localSaveButton.Click += async (_, _) => await SaveLocalEnvAsync();
        panel.Children.Add(localSaveButton);

        panel.Children.Add(SectionHeader("Use a Cloudflare Worker URL"));
        panel.Children.Add(BodyText("Deploy the Worker from a terminal, add secrets in Cloudflare, then paste the Worker URL here. Nudge will verify the Worker before saving it."));
        workerUrlBox.Margin = new Thickness(0, 6, 0, 10);
        workerUrlBox.MinWidth = 460;
        workerUrlBox.Text = settingsStore.Load().WorkerBaseUrl ?? "";
        panel.Children.Add(workerUrlBox);

        validateButton.Content = "Use this Worker";
        validateButton.HorizontalAlignment = WpfHorizontalAlignment.Left;
        validateButton.Padding = new Thickness(16, 8, 16, 8);
        validateButton.Click += async (_, _) => await ValidateAndSaveWorkerAsync(workerUrlBox.Text, useDeveloperWorker: false);
        panel.Children.Add(validateButton);

        var advancedButton = new WpfButton
        {
            Content = "Advanced",
            Margin = new Thickness(0, 24, 0, 8),
            HorizontalAlignment = WpfHorizontalAlignment.Left,
            Padding = new Thickness(12, 6, 12, 6)
        };
        advancedButton.Click += (_, _) =>
        {
            developerWorkerButton.Visibility = developerWorkerButton.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        };
        panel.Children.Add(advancedButton);

        developerWorkerButton.Content = "Use developer default Worker";
        developerWorkerButton.Visibility = Visibility.Collapsed;
        developerWorkerButton.HorizontalAlignment = WpfHorizontalAlignment.Left;
        developerWorkerButton.Padding = new Thickness(16, 8, 16, 8);
        developerWorkerButton.Click += async (_, _) => await ValidateAndSaveWorkerAsync(AppConfig.WorkerBaseUri.ToString(), useDeveloperWorker: true);
        panel.Children.Add(developerWorkerButton);

        statusText.TextWrapping = TextWrapping.Wrap;
        statusText.Margin = new Thickness(0, 22, 0, 8);
        statusText.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(statusText);

        diagnosticsText.TextWrapping = TextWrapping.Wrap;
        diagnosticsText.Margin = new Thickness(0, 0, 0, 12);
        panel.Children.Add(diagnosticsText);

        var openLogButton = new WpfButton
        {
            Content = "Open log",
            HorizontalAlignment = WpfHorizontalAlignment.Left,
            Padding = new Thickness(12, 6, 12, 6)
        };
        openLogButton.Click += (_, _) => OpenLog();
        panel.Children.Add(openLogButton);

        return root;
    }

    private async Task ValidateAndSaveWorkerAsync(string workerUrl, bool useDeveloperWorker)
    {
        if (string.IsNullOrWhiteSpace(workerUrl))
        {
            UpdateStatus("Enter a Worker URL first.");
            return;
        }

        SetBusy(true);
        try
        {
            UpdateStatus("Checking Worker...");
            WorkerDiagnostics diagnostics;
            try
            {
                diagnostics = await setupRunner.ValidateWorkerAsync(workerUrl, CancellationToken.None);
            }
            catch (Exception error)
            {
                AppLogger.Error("Worker validation failed", error);
                UpdateStatus("That Worker could not be reached. Check the URL and try again.");
                diagnosticsText.Text = error.Message;
                return;
            }

            diagnosticsText.Text = FormatDiagnostics(diagnostics);
            if (!HasRequiredSecrets(diagnostics))
            {
                UpdateStatus("Worker reachable, but required secrets are missing.");
                return;
            }

            var normalizedUrl = WorkerSetupRunner.NormalizeWorkerUrl(workerUrl);
            settingsStore.Save(new AppSettings("worker", normalizedUrl, useDeveloperWorker));
            UpdateStatus("Setup complete.");
            DialogResult = true;
            Close();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static bool HasRequiredSecrets(WorkerDiagnostics diagnostics)
    {
        return diagnostics.Secrets is
        {
            OpenAI: true,
            AssemblyAI: true,
            ElevenLabs: true,
            ElevenLabsVoice: true
        };
    }

    private static string FormatDiagnostics(WorkerDiagnostics diagnostics)
    {
        var secrets = diagnostics.Secrets;
        if (secrets is null)
        {
            return "Diagnostics did not include setup status.";
        }

        return string.Join(Environment.NewLine, new[]
        {
            $"OpenAI key: {ReadyText(secrets.OpenAI, "OpenAI key")}",
            $"AssemblyAI key: {ReadyText(secrets.AssemblyAI, "AssemblyAI key")}",
            $"ElevenLabs key: {ReadyText(secrets.ElevenLabs, "ElevenLabs key")}",
            $"ElevenLabs voice ID: {ReadyText(secrets.ElevenLabsVoice, "ElevenLabs voice ID")}",
            $"Locator: {diagnostics.Locator?.Provider ?? "unknown"} / {diagnostics.Locator?.Model ?? "unknown"}",
            $"Chat model: {diagnostics.Chat?.Model ?? "unknown"}"
        });
    }

    private static string ReadyText(bool ready, string label)
    {
        return ready ? "ready" : $"missing {label}";
    }

    private void SetBusy(bool isBusy)
    {
        validateButton.IsEnabled = !isBusy;
        developerWorkerButton.IsEnabled = !isBusy;
        localSaveButton.IsEnabled = !isBusy;
        Cursor = isBusy ? System.Windows.Input.Cursors.Wait : null;
    }

    private async Task SaveLocalEnvAsync()
    {
        if (!ValidateLocalSecretFields())
        {
            return;
        }

        SetBusy(true);
        try
        {
            UpdateStatus("Saving local keys...");
            try
            {
                LocalEnv.Save(
                    LocalEnv.AppEnvPath,
                    localOpenAiKeyBox.Password,
                    localAssemblyAiKeyBox.Password,
                    localElevenLabsKeyBox.Password,
                    localElevenLabsVoiceIdBox.Password);
            }
            catch (Exception error)
            {
                AppLogger.Error("Saving local .env failed", error);
                UpdateStatus("Local .env could not be saved. Open the log for details.");
                diagnosticsText.Text = error.Message;
                return;
            }

            var localClient = new LocalProviderClient(LocalEnv.AppEnvPath);
            var diagnostics = await localClient.GetDiagnosticsAsync(CancellationToken.None);
            diagnosticsText.Text = FormatDiagnostics(diagnostics);
            if (!HasRequiredSecrets(diagnostics))
            {
                UpdateStatus(FirstMissingLocalMessage(diagnostics));
                return;
            }

            settingsStore.Save(new AppSettings("local"));
            UpdateStatus("Setup complete.");
            DialogResult = true;
            Close();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private bool ValidateLocalSecretFields()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(localOpenAiKeyBox.Password))
        {
            missing.Add("OpenAI API key");
        }

        if (string.IsNullOrWhiteSpace(localAssemblyAiKeyBox.Password))
        {
            missing.Add("AssemblyAI API key");
        }

        if (string.IsNullOrWhiteSpace(localElevenLabsKeyBox.Password))
        {
            missing.Add("ElevenLabs API key");
        }

        if (string.IsNullOrWhiteSpace(localElevenLabsVoiceIdBox.Password))
        {
            missing.Add("ElevenLabs voice ID");
        }

        if (missing.Count == 0)
        {
            return true;
        }

        UpdateStatus("Missing: " + string.Join(", ", missing) + ".");
        return false;
    }

    private static string FirstMissingLocalMessage(WorkerDiagnostics diagnostics)
    {
        var secrets = diagnostics.Secrets;
        if (secrets is null)
        {
            return "Local diagnostics did not include setup status.";
        }

        if (!secrets.OpenAI)
        {
            return "Local .env saved, missing OpenAI key.";
        }

        if (!secrets.AssemblyAI)
        {
            return "Local .env saved, missing AssemblyAI key.";
        }

        if (!secrets.ElevenLabs)
        {
            return "Local .env saved, missing ElevenLabs key.";
        }

        return !secrets.ElevenLabsVoice
            ? "Local .env saved, missing ElevenLabs voice ID."
            : "Local setup is ready.";
    }

    private void UpdateStatus(string message)
    {
        statusText.Text = message;
        AppLogger.Info($"Setup: {message}");
    }

    private static TextBlock SectionHeader(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 8)
        };
    }

    private static TextBlock BodyText(string text)
    {
        return new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = WpfBrushes.DimGray,
            Margin = new Thickness(0, 0, 0, 10)
        };
    }

    private static TextBlock InputLabel(string text)
    {
        return new TextBlock
        {
            Text = text,
            Margin = new Thickness(0, 8, 0, 4)
        };
    }

    private static void OpenLog()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AppLogger.LogPath,
                UseShellExecute = true
            });
        }
        catch (Exception error)
        {
            AppLogger.Error("Opening setup log failed", error);
        }
    }
}
