using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ClickyClone.Services;
using ClickyClone.UI;
using Application = System.Windows.Application;
using WpfPoint = System.Windows.Point;

namespace ClickyClone.Core;

public sealed class CompanionManager : INotifyPropertyChanged, IDisposable
{
    private readonly IBackendClient backendClient;
    private readonly OverlayManager overlayManager;
    private readonly AudioRecorder audioRecorder;
    private readonly AssemblyAIStreamingClient transcriptionClient;
    private readonly ScreenCaptureService screenCaptureService;
    private readonly TextToSpeechPlayer textToSpeechPlayer;
    private readonly GlobalPushToTalkMonitor hotkeyMonitor;
    private readonly List<ConversationTurn> conversationHistory = [];
    private readonly SemaphoreSlim interactionGate = new(1, 1);
    private const string LocatorProvider = "openai-computer-use";
    private static readonly Regex WordRegex = new("[a-z0-9]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> PointingIntentWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "click", "find", "locate", "open", "point", "press", "select"
    };
    private static readonly HashSet<string> CatalogDiagnosticStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "button", "click", "control", "cursor", "find", "for", "go", "i", "it", "me",
        "of", "on", "please", "point", "show", "the", "there", "this", "to", "where", "you"
    };

    private CancellationTokenSource? currentInteractionCancellation;
    private CompanionStatus status = new(CompanionVoiceState.Idle, "Ready");
    private bool isStarted;
    private bool isRecording;
    private bool releaseRequestedDuringStartup;
    private int interactionGeneration;

    public event PropertyChangedEventHandler? PropertyChanged;

    public CompanionStatus Status
    {
        get => status;
        private set
        {
            status = value;
            OnPropertyChanged();
        }
    }

    public void Start()
    {
        if (isStarted)
        {
            return;
        }

        isStarted = true;
        AppLogger.Info("Companion manager starting.");
        overlayManager.Show();
        hotkeyMonitor.Pressed += HandlePushToTalkPressed;
        hotkeyMonitor.Released += HandlePushToTalkReleased;
        hotkeyMonitor.Start();
        _ = CheckWorkerHealthAsync();
    }

    public CompanionManager(
        IBackendClient backendClient,
        OverlayManager overlayManager,
        AudioRecorder audioRecorder,
        AssemblyAIStreamingClient transcriptionClient,
        ScreenCaptureService screenCaptureService,
        TextToSpeechPlayer textToSpeechPlayer,
        GlobalPushToTalkMonitor hotkeyMonitor)
    {
        this.backendClient = backendClient;
        this.overlayManager = overlayManager;
        this.audioRecorder = audioRecorder;
        this.transcriptionClient = transcriptionClient;
        this.screenCaptureService = screenCaptureService;
        this.textToSpeechPlayer = textToSpeechPlayer;
        this.hotkeyMonitor = hotkeyMonitor;
    }

    public void ShowPanel()
    {
        overlayManager.Show();
    }

    public void Quit()
    {
        Application.Current.Shutdown();
    }

    private async Task CheckWorkerHealthAsync()
    {
        try
        {
            await backendClient.CheckHealthAsync(CancellationToken.None);
            var hasExactPointingSupport = await backendClient.CheckPointingSelfTestAsync(CancellationToken.None);
            if (!hasExactPointingSupport)
            {
                AppLogger.Info("Worker exact pointing support is not available. Redeploy worker/clickyclone-worker.js before judging pointing accuracy.");
            }
        }
        catch (Exception error)
        {
            AppLogger.Error("Worker health check failed", error);
            Status = new CompanionStatus(
                CompanionVoiceState.Error,
                "Worker unavailable",
                LastError: error.Message);
        }
    }

    private void HandlePushToTalkPressed(object? sender, EventArgs e)
    {
        _ = Task.Run(async () =>
        {
            await BeginPushToTalkAsync();
        });
    }

    private void HandlePushToTalkReleased(object? sender, EventArgs e)
    {
        _ = Task.Run(async () =>
        {
            await EndPushToTalkAsync();
        });
    }

    private async Task BeginPushToTalkAsync()
    {
        await interactionGate.WaitAsync();
        AppLogger.Info("Interaction begin.");
        var generation = ++interactionGeneration;
        releaseRequestedDuringStartup = false;
        isRecording = false;
        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                currentInteractionCancellation?.Cancel();
                currentInteractionCancellation?.Dispose();
                currentInteractionCancellation = new CancellationTokenSource();

                textToSpeechPlayer.Stop();
                overlayManager.ClearPointTarget();
                overlayManager.Show();
                overlayManager.SetVoiceState(CompanionVoiceState.Listening);
                Status = new CompanionStatus(CompanionVoiceState.Listening, "Listening");
            });

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                audioRecorder.AudioAvailable += HandleAudioAvailable;
                audioRecorder.Start();
                isRecording = true;
            });
            AppLogger.Info("Interaction recording started.");
        }
        catch (Exception error)
        {
            await FailCurrentInteractionAsync("Couldn't start voice input", error);
        }
        finally
        {
            interactionGate.Release();
        }

        if (releaseRequestedDuringStartup && generation == interactionGeneration)
        {
            AppLogger.Info("Release was requested during startup; finalizing after recorder startup.");
            await EndPushToTalkAsync();
        }
    }

    private async Task EndPushToTalkAsync()
    {
        var releaseStopwatch = Stopwatch.StartNew();
        await interactionGate.WaitAsync();
        if (Status.VoiceState != CompanionVoiceState.Listening)
        {
            AppLogger.Info($"Interaction end ignored. State={Status.VoiceState}");
            interactionGate.Release();
            return;
        }

        if (!isRecording)
        {
            releaseRequestedDuringStartup = true;
            AppLogger.Info("Interaction release deferred until microphone startup completes.");
            interactionGate.Release();
            return;
        }

        AppLogger.Info("Interaction end.");

        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                audioRecorder.AudioAvailable -= HandleAudioAvailable;
                audioRecorder.Stop();
                isRecording = false;
                overlayManager.SetVoiceState(CompanionVoiceState.Processing);
                Status = Status with { VoiceState = CompanionVoiceState.Processing, StatusText = "Processing" };
            });

            var cancellationToken = currentInteractionCancellation?.Token ?? CancellationToken.None;
            var wavBytes = audioRecorder.GetRecordedWavBytes();
            AppLogger.Info($"PERF stage=audio_stop_and_wav ms={releaseStopwatch.ElapsedMilliseconds} bytes={wavBytes.Length}");
            if (wavBytes.Length < 4096)
            {
                AppLogger.Info($"Recording too small to transcribe. Bytes={wavBytes.Length}");
                await RunOnUiAsync(() =>
                {
                    Status = new CompanionStatus(CompanionVoiceState.Idle, "Ready");
                    overlayManager.SetVoiceState(CompanionVoiceState.Idle);
                });
                return;
            }

            var captureTask = CaptureScreensForInteractionAsync(cancellationToken);
            var transcribeStopwatch = Stopwatch.StartNew();
            var transcript = await backendClient.TranscribeAudioAsync(wavBytes, cancellationToken);
            AppLogger.Info($"PERF stage=transcription_total ms={transcribeStopwatch.ElapsedMilliseconds}");
            AppLogger.Info($"Transcript finalized. Length={transcript.Length}");

            if (string.IsNullOrWhiteSpace(transcript))
            {
                AppLogger.Info("Interaction ended with empty transcript.");
                _ = captureTask.ContinueWith(
                    task => AppLogger.Error("Background screen capture failed after empty transcript", task.Exception),
                    TaskContinuationOptions.OnlyOnFaulted);
                await RunOnUiAsync(() =>
                {
                    Status = new CompanionStatus(CompanionVoiceState.Idle, "Ready");
                    overlayManager.SetVoiceState(CompanionVoiceState.Idle);
                });
                return;
            }

            var captures = await captureTask;
            await ProcessTranscriptAsync(transcript, captures, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            await FailCurrentInteractionAsync("Couldn't process that", error);
        }
        finally
        {
            interactionGate.Release();
        }
    }

    private void HandleAudioAvailable(object? sender, AudioChunkEventArgs e)
    {
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            overlayManager.SetAudioLevel(e.AudioLevel);
        });
    }

    private async Task<IReadOnlyList<ScreenCapturePayload>> CaptureScreensForInteractionAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await RunOnUiAsync(overlayManager.SuspendVisualsForCapture);
        try
        {
            var captures = await screenCaptureService.CaptureAllScreensAsync(cancellationToken, includeVisualAtlas: false);
            AppLogger.Info($"PERF stage=screen_capture_total ms={stopwatch.ElapsedMilliseconds}");
            AppLogger.Info($"Screen capture completed. Count={captures.Count}");
            foreach (var capture in captures)
            {
                AppLogger.Info($"Screen capture elements. Screen={capture.ScreenNumber} Count={capture.Elements?.Count ?? 0} VisualTargets={capture.VisualTargets?.Count ?? 0} Atlas={capture.VisualAtlasBase64Data?.Length ?? 0} Dpi={capture.DpiScaleX:0.00}x{capture.DpiScaleY:0.00} Size={capture.ScreenshotWidthInPixels}x{capture.ScreenshotHeightInPixels}");
            }

            return captures;
        }
        finally
        {
            await RunOnUiAsync(overlayManager.ResumeVisualsAfterCapture);
        }
    }

    private async Task ProcessTranscriptAsync(
        string transcript,
        IReadOnlyList<ScreenCapturePayload> captures,
        CancellationToken cancellationToken)
    {
        var processStopwatch = Stopwatch.StartNew();
        AppLogger.Info($"Processing transcript. Length={transcript.Length}");
        await RunOnUiAsync(() =>
        {
            Status = new CompanionStatus(CompanionVoiceState.Processing, "Thinking", LastTranscript: transcript);
        });

        var shouldAttemptPointing = ShouldAttemptPointing(transcript);
        var isDirectPointingRequest = IsDirectPointingRequest(transcript);
        AppLogger.Info($"Pointing intent. ShouldAttemptPointing={shouldAttemptPointing} IsDirectPointingRequest={isDirectPointingRequest} Provider={LocatorProvider}");
        LogElementCatalogDiagnostics(transcript, captures);

        Task<LocateResponse>? locateTask = null;
        ScreenCapturePayload? locateCapture = null;
        if (shouldAttemptPointing)
        {
            locateCapture = SelectLocateCapture(captures);
            if (locateCapture is not null)
            {
                locateTask = backendClient.LocateAsync(transcript, locateCapture, LocatorProvider, cancellationToken);
            }
            else
            {
                AppLogger.Info("No capture available for locator.");
            }
        }

        string spokenText;
        if (isDirectPointingRequest)
        {
            // Pointing turns use /locate as the authoritative coordinate source.
            AppLogger.Info("Skipping /chat for pointing request; locator is authoritative.");
            spokenText = "";
        }
        else
        {
            var chatStopwatch = Stopwatch.StartNew();
            var chatResponse = await backendClient.SendChatAsync(
                transcript,
                captures,
                conversationHistory,
                cancellationToken);
            AppLogger.Info($"PERF stage=chat_total ms={chatStopwatch.ElapsedMilliseconds}");

            spokenText = string.IsNullOrWhiteSpace(chatResponse.SpokenText)
                ? chatResponse.Text
                : chatResponse.SpokenText;
            AppLogger.Info($"Chat response parsed. TextLength={chatResponse.Text.Length} SpokenLength={spokenText.Length} HasPoint={chatResponse.Point is not null}");
        }

        ChatPoint? point = null;
        if (locateTask is not null && locateCapture is not null)
        {
            try
            {
                var locateResponse = await locateTask;
                point = ToComputerUsePoint(locateResponse, locateCapture);
                if (point is null)
                {
                    AppLogger.Info($"Locator did not return a valid point. Ok={locateResponse.Ok} Error={locateResponse.Error ?? ""}");
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                AppLogger.Error("Locator request failed", error);
            }
        }

        if (isDirectPointingRequest)
        {
            spokenText = point is not null
                ? "I'll point to it."
                : "I couldn't find that on the screen.";
        }

        conversationHistory.Add(new ConversationTurn(transcript, spokenText));
        if (conversationHistory.Count > 10)
        {
            conversationHistory.RemoveRange(0, conversationHistory.Count - 10);
        }

        if (PointMapper.TryMapPoint(point, captures, out var pointTarget))
        {
            AppLogger.Info($"Point mapped. Label={pointTarget.Label ?? ""} PointSource={point?.Source ?? "unknown"} SourceX={point?.X:0.0} SourceY={point?.Y:0.0} Screen={point?.ScreenNumber?.ToString() ?? "cursor"} DesktopX={pointTarget.DesktopPoint.X:0.0} DesktopY={pointTarget.DesktopPoint.Y:0.0} HasBounds={pointTarget.DesktopBounds is not null}");
            await RunOnUiAsync(() => overlayManager.PointAt(pointTarget));
            AppLogger.Info($"PERF stage=release_to_point ms={processStopwatch.ElapsedMilliseconds}");
        }
        else
        {
            AppLogger.Info("No valid point target returned or mapped.");
        }

        await RunOnUiAsync(() =>
        {
            Status = new CompanionStatus(
                CompanionVoiceState.Responding,
                "Responding",
                LastTranscript: transcript,
                LastResponse: spokenText);

            overlayManager.SetVoiceState(CompanionVoiceState.Responding);
        });

        if (!string.IsNullOrWhiteSpace(spokenText))
        {
            var ttsStopwatch = Stopwatch.StartNew();
            await textToSpeechPlayer.SpeakAsync(spokenText, cancellationToken);
            AppLogger.Info($"PERF stage=tts_total ms={ttsStopwatch.ElapsedMilliseconds}");
        }

        await RunOnUiAsync(() =>
        {
            Status = Status with { VoiceState = CompanionVoiceState.Idle, StatusText = "Ready" };
            overlayManager.SetVoiceState(CompanionVoiceState.Idle);
        });
        AppLogger.Info("Interaction completed.");
    }

    private Task FailCurrentInteractionAsync(string statusText, Exception error)
    {
        AppLogger.Error(statusText, error);
        return RunOnUiAsync(() =>
        {
            audioRecorder.AudioAvailable -= HandleAudioAvailable;
            audioRecorder.Stop();
            isRecording = false;
            transcriptionClient.Cancel();
            overlayManager.SetVoiceState(CompanionVoiceState.Idle);
            Status = new CompanionStatus(
                CompanionVoiceState.Error,
                statusText,
                LastError: error.Message);
        });
    }

    private static Task RunOnUiAsync(Action action)
    {
        return Application.Current.Dispatcher.InvokeAsync(action).Task;
    }

    private static bool ShouldAttemptPointing(string transcript)
    {
        var normalized = transcript.Trim().ToLowerInvariant();
        if (normalized.StartsWith("what ", StringComparison.Ordinal) ||
            normalized.StartsWith("why ", StringComparison.Ordinal) ||
            normalized.StartsWith("tell me ", StringComparison.Ordinal) ||
            normalized.StartsWith("explain ", StringComparison.Ordinal))
        {
            return false;
        }

        if (normalized.StartsWith("how ", StringComparison.Ordinal) ||
            normalized.StartsWith("how do ", StringComparison.Ordinal) ||
            normalized.StartsWith("how can ", StringComparison.Ordinal) ||
            normalized.Contains("show me", StringComparison.Ordinal) ||
            normalized.Contains("can you see", StringComparison.Ordinal))
        {
            return true;
        }

        return IsDirectPointingRequest(transcript);
    }

    private static bool IsDirectPointingRequest(string transcript)
    {
        var normalized = transcript.Trim().ToLowerInvariant();
        if (normalized.Contains("where is", StringComparison.Ordinal) ||
            normalized.Contains("where's", StringComparison.Ordinal) ||
            normalized.Contains("point to", StringComparison.Ordinal) ||
            normalized.Contains("show me where", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (Match match in WordRegex.Matches(transcript))
        {
            if (PointingIntentWords.Contains(match.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static ScreenCapturePayload? SelectLocateCapture(IReadOnlyList<ScreenCapturePayload> captures)
    {
        return captures.FirstOrDefault(capture => capture.IsCursorScreen) ??
               captures.OrderBy(capture => capture.ScreenNumber).FirstOrDefault();
    }

    private static ChatPoint? ToComputerUsePoint(LocateResponse locateResponse, ScreenCapturePayload capture)
    {
        if (!locateResponse.Ok ||
            locateResponse.X is not { } x ||
            locateResponse.Y is not { } y ||
            !string.Equals(locateResponse.CoordinateSpace, "screenshot_pixels_top_left", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new ChatPoint(
            x,
            y,
            locateResponse.Label,
            locateResponse.ScreenNumber ?? capture.ScreenNumber,
            "computer-use");
    }

    private static void LogElementCatalogDiagnostics(string transcript, IReadOnlyList<ScreenCapturePayload> captures)
    {
        var queryWords = ExtractCatalogDiagnosticWords(transcript).ToArray();
        var queryText = queryWords.Length == 0 ? "none" : string.Join(",", queryWords);
        AppLogger.Info($"Element catalog diagnostic query. Words={queryText}");

        foreach (var capture in captures)
        {
            var elements = capture.Elements ?? [];
            var sample = elements
                .Take(12)
                .Select(FormatCatalogElement)
                .ToArray();
            AppLogger.Info($"Element catalog sample. Screen={capture.ScreenNumber} Items={(sample.Length == 0 ? "none" : string.Join(" | ", sample))}");

            if (queryWords.Length == 0)
            {
                continue;
            }

            var matches = elements
                .Select(element => new
                {
                    Element = element,
                    Score = queryWords.Count(word => element.Name.Contains(word, StringComparison.OrdinalIgnoreCase))
                })
                .Where(candidate => candidate.Score > 0)
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Element.Y)
                .ThenBy(candidate => candidate.Element.X)
                .Take(12)
                .Select(candidate => $"{FormatCatalogElement(candidate.Element)} score={candidate.Score}")
                .ToArray();

            AppLogger.Info($"Element catalog transcript matches. Screen={capture.ScreenNumber} Items={(matches.Length == 0 ? "none" : string.Join(" | ", matches))}");
        }
    }

    private static IEnumerable<string> ExtractCatalogDiagnosticWords(string text)
    {
        foreach (Match match in WordRegex.Matches(text.ToLowerInvariant()))
        {
            var word = match.Value;
            if (word.Length > 1 && !CatalogDiagnosticStopWords.Contains(word))
            {
                yield return word;
            }
        }
    }

    private static string FormatCatalogElement(ScreenElementPayload element)
    {
        return $"{element.Id}:{element.Name}({element.ControlType})@{element.CenterX:0},{element.CenterY:0}";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        currentInteractionCancellation?.Cancel();
        currentInteractionCancellation?.Dispose();
        interactionGate.Dispose();
        hotkeyMonitor.Pressed -= HandlePushToTalkPressed;
        hotkeyMonitor.Released -= HandlePushToTalkReleased;
        hotkeyMonitor.Dispose();
        audioRecorder.Dispose();
        transcriptionClient.Dispose();
        textToSpeechPlayer.Dispose();
        overlayManager.Dispose();
    }
}
