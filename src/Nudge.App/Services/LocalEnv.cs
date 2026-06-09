using System.IO;
using System.Text;

namespace Nudge.Services;

public sealed record LocalEnvSettings(
    string? OpenAIKey,
    string? AssemblyAIKey,
    string? ElevenLabsKey,
    string? ElevenLabsVoiceId,
    string OpenAIComputerModel,
    string OpenAIModel,
    string ElevenLabsModel);

public static class LocalEnv
{
    public const string OpenAIKeyName = "OPENAI_API_KEY";
    public const string AssemblyAIKeyName = "ASSEMBLYAI_API_KEY";
    public const string ElevenLabsKeyName = "ELEVENLABS_API_KEY";
    public const string ElevenLabsVoiceIdName = "ELEVENLABS_VOICE_ID";
    public const string OpenAIComputerModelName = "OPENAI_COMPUTER_MODEL";
    public const string OpenAIModelName = "OPENAI_MODEL";
    public const string ElevenLabsModelName = "ELEVENLABS_MODEL";

    public const string DefaultOpenAIComputerModel = "gpt-5.5";
    public const string DefaultOpenAIModel = "gpt-4.1-mini";
    public const string DefaultElevenLabsModel = "eleven_flash_v2_5";

    public static string AppEnvPath => Path.Combine(AppContext.BaseDirectory, ".env");

    public static LocalEnvSettings Load(string path)
    {
        var values = File.Exists(path)
            ? Parse(File.ReadAllLines(path))
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return new LocalEnvSettings(
            Get(values, OpenAIKeyName),
            Get(values, AssemblyAIKeyName),
            Get(values, ElevenLabsKeyName),
            Get(values, ElevenLabsVoiceIdName),
            Get(values, OpenAIComputerModelName) ?? DefaultOpenAIComputerModel,
            Get(values, OpenAIModelName) ?? DefaultOpenAIModel,
            Get(values, ElevenLabsModelName) ?? DefaultElevenLabsModel);
    }

    public static Dictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            values[key] = Unquote(value);
        }

        return values;
    }

    public static void Save(
        string path,
        string openAIKey,
        string assemblyAIKey,
        string elevenLabsKey,
        string elevenLabsVoiceId)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var text = new StringBuilder();
        text.AppendLine($"{OpenAIKeyName}={openAIKey.Trim()}");
        text.AppendLine($"{AssemblyAIKeyName}={assemblyAIKey.Trim()}");
        text.AppendLine($"{ElevenLabsKeyName}={elevenLabsKey.Trim()}");
        text.AppendLine($"{ElevenLabsVoiceIdName}={elevenLabsVoiceId.Trim()}");
        text.AppendLine($"{OpenAIComputerModelName}={DefaultOpenAIComputerModel}");
        text.AppendLine($"{OpenAIModelName}={DefaultOpenAIModel}");
        text.AppendLine($"{ElevenLabsModelName}={DefaultElevenLabsModel}");
        File.WriteAllText(path, text.ToString());
    }

    private static string? Get(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value.StartsWith("\"", StringComparison.Ordinal) && value.EndsWith("\"", StringComparison.Ordinal)) ||
             (value.StartsWith("'", StringComparison.Ordinal) && value.EndsWith("'", StringComparison.Ordinal))))
        {
            return value[1..^1];
        }

        return value;
    }
}
