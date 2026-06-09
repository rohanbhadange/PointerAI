using NAudio.Wave;
using NAudio.CoreAudioApi;
using System.IO;

namespace Nudge.Services;

public sealed class AudioChunkEventArgs : EventArgs
{
    public AudioChunkEventArgs(byte[] pcm16Bytes, double audioLevel)
    {
        Pcm16Bytes = pcm16Bytes;
        AudioLevel = audioLevel;
    }

    public byte[] Pcm16Bytes { get; }
    public double AudioLevel { get; }
}

public sealed class AudioRecorder : IDisposable
{
    private WaveInEvent? waveIn;
    private MemoryStream? recordingStream;
    private WaveFileWriter? waveWriter;
    private int chunksRecorded;

    public event EventHandler<AudioChunkEventArgs>? AudioAvailable;

    public void Start()
    {
        Stop();
        AppLogger.Info("Starting microphone capture.");
        chunksRecorded = 0;
        recordingStream = new MemoryStream();

        waveIn = new WaveInEvent
        {
            DeviceNumber = 0,
            WaveFormat = new WaveFormat(16_000, 16, 1),
            BufferMilliseconds = 100,
            NumberOfBuffers = 3
        };
        waveWriter = new WaveFileWriter(new IgnoreDisposeStream(recordingStream), waveIn.WaveFormat);

        waveIn.DataAvailable += HandleDataAvailable;
        waveIn.RecordingStopped += HandleRecordingStopped;
        waveIn.StartRecording();
        AppLogger.Info($"Microphone capture started. DeviceCount={WaveIn.DeviceCount} Format={waveIn.WaveFormat}");
    }

    public void Stop()
    {
        if (waveIn is null)
        {
            return;
        }

        waveIn.DataAvailable -= HandleDataAvailable;
        waveIn.RecordingStopped -= HandleRecordingStopped;
        waveIn.StopRecording();
        waveIn.Dispose();
        waveIn = null;

        waveWriter?.Flush();
        waveWriter?.Dispose();
        waveWriter = null;
        AppLogger.Info($"Microphone capture stopped. ChunksRecorded={chunksRecorded}");
    }

    public byte[] GetRecordedWavBytes()
    {
        waveWriter?.Flush();
        var bytes = recordingStream?.ToArray() ?? [];
        AppLogger.Info($"Recorded WAV read. Bytes={bytes.Length}");
        return bytes;
    }

    private void HandleDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
        {
            return;
        }

        var buffer = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, buffer, 0, e.BytesRecorded);
        waveWriter?.Write(buffer, 0, buffer.Length);
        chunksRecorded++;
        var audioLevel = CalculateAudioLevel(buffer, waveIn?.WaveFormat);
        if (chunksRecorded == 1 || chunksRecorded % 25 == 0)
        {
            AppLogger.Info($"Microphone chunk recorded. Count={chunksRecorded} Bytes={buffer.Length} Level={audioLevel:0.000}");
        }

        try
        {
            AudioAvailable?.Invoke(this, new AudioChunkEventArgs(buffer, audioLevel));
        }
        catch (Exception error)
        {
            AppLogger.Error("AudioAvailable subscriber failed", error);
        }
    }

    private static void HandleRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            AppLogger.Error("Microphone recording stopped with an error", e.Exception);
        }
    }

    private static double CalculateAudioLevel(byte[] audioBytes, WaveFormat? waveFormat)
    {
        if (waveFormat is null || audioBytes.Length < 2)
        {
            return 0;
        }

        if (waveFormat.Encoding == WaveFormatEncoding.IeeeFloat && waveFormat.BitsPerSample == 32)
        {
            double sumSquares = 0;
            var sampleCount = audioBytes.Length / 4;
            for (var index = 0; index < audioBytes.Length - 3; index += 4)
            {
                var sample = BitConverter.ToSingle(audioBytes, index);
                sumSquares += sample * sample;
            }

            var rms = Math.Sqrt(sumSquares / Math.Max(sampleCount, 1));
            return Math.Clamp(rms * 8.0, 0.0, 1.0);
        }

        if (waveFormat.BitsPerSample == 16)
        {
            double sumSquares = 0;
            var sampleCount = audioBytes.Length / 2;
            for (var index = 0; index < audioBytes.Length - 1; index += 2)
            {
                var sample = BitConverter.ToInt16(audioBytes, index) / 32768.0;
                sumSquares += sample * sample;
            }

            var rms = Math.Sqrt(sumSquares / Math.Max(sampleCount, 1));
            return Math.Clamp(rms * 8.0, 0.0, 1.0);
        }

        return 0;
    }

    public void Dispose()
    {
        Stop();
        recordingStream?.Dispose();
    }

    private sealed class IgnoreDisposeStream : Stream
    {
        private readonly Stream inner;

        public IgnoreDisposeStream(Stream inner)
        {
            this.inner = inner;
        }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing)
        {
            Flush();
        }
    }
}
