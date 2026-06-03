using NAudio.Wave;
using System.IO;

namespace ClickyClone.Services;

public sealed class TextToSpeechPlayer : IDisposable
{
    private readonly WorkerClient workerClient;
    private WaveOutEvent? waveOut;
    private Mp3FileReader? mp3Reader;

    public TextToSpeechPlayer(WorkerClient workerClient)
    {
        this.workerClient = workerClient;
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken)
    {
        Stop();
        AppLogger.Info("TTS playback start requested.");

        var audioBytes = await workerClient.TextToSpeechAsync(text, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        mp3Reader = new Mp3FileReader(new MemoryStream(audioBytes));
        waveOut = new WaveOutEvent();

        var playbackCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        waveOut.PlaybackStopped += (_, _) => playbackCompletion.TrySetResult(null);
        waveOut.Init(mp3Reader);
        waveOut.Play();
        AppLogger.Info($"TTS playback started. Bytes={audioBytes.Length}");

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            Stop();
            playbackCompletion.TrySetCanceled(cancellationToken);
        });

        await playbackCompletion.Task;
        AppLogger.Info("TTS playback completed.");
    }

    public void Stop()
    {
        if (waveOut is not null)
        {
            AppLogger.Info("TTS playback stopped.");
            try
            {
                waveOut.Stop();
            }
            catch
            {
            }

            waveOut.Dispose();
            waveOut = null;
        }

        mp3Reader?.Dispose();
        mp3Reader = null;
    }

    public void Dispose()
    {
        Stop();
    }
}
