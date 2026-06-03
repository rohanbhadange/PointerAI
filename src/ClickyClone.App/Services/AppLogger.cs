using System.IO;

namespace ClickyClone.Services;

public static class AppLogger
{
    private static readonly object SyncRoot = new();

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClickyClone");

    public static string LogPath { get; } = Path.Combine(LogDirectory, "clickyclone.log");

    public static void Reset()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            lock (SyncRoot)
            {
                File.WriteAllText(LogPath, "");
            }
        }
        catch
        {
            // Logging must never break startup.
        }
    }

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Error(string message, Exception? exception = null)
    {
        Write("ERROR", exception is null ? message : $"{message}: {exception}");
    }

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var line = $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}";
            lock (SyncRoot)
            {
                File.AppendAllText(LogPath, line);
            }
        }
        catch
        {
            // Logging must never break the companion loop.
        }
    }
}
