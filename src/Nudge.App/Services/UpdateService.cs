using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nudge.Services;

public sealed record UpdateCheckResult(
    bool UpdateAvailable,
    string CurrentVersion,
    string? LatestVersion,
    string? ReleasePageUrl,
    string? InstallerDownloadUrl,
    string? Error = null);

public sealed class UpdateService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public UpdateService()
    {
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Nudge-Updater/1.0");
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(AppConfig.GitHubLatestReleaseUri, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(false, AppConfig.AppVersion, null, null, null, $"GitHub returned {(int)response.StatusCode}.");
            }

            var release = JsonSerializer.Deserialize<GitHubRelease>(body, JsonOptions);
            var latestVersion = NormalizeVersion(release?.TagName);
            var installerAsset = release?.Assets?
                .FirstOrDefault(asset => asset.Name.Contains("NudgeSetup", StringComparison.OrdinalIgnoreCase) &&
                                         asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                ?? release?.Assets?.FirstOrDefault(asset => asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrWhiteSpace(latestVersion))
            {
                return new UpdateCheckResult(false, AppConfig.AppVersion, null, release?.HtmlUrl, null, "Latest release does not include a version tag.");
            }

            var updateAvailable = IsNewerVersion(latestVersion, AppConfig.AppVersion);
            return new UpdateCheckResult(
                updateAvailable,
                AppConfig.AppVersion,
                latestVersion,
                release?.HtmlUrl,
                installerAsset?.BrowserDownloadUrl,
                updateAvailable && string.IsNullOrWhiteSpace(installerAsset?.BrowserDownloadUrl)
                    ? "Latest release does not include a Windows installer asset."
                    : null);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            AppLogger.Error("Update check failed", error);
            return new UpdateCheckResult(false, AppConfig.AppVersion, null, null, null, error.Message);
        }
    }

    public async Task<string> DownloadInstallerAsync(
        UpdateCheckResult update,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(update.InstallerDownloadUrl))
        {
            throw new InvalidOperationException(update.Error ?? "No installer download URL was available.");
        }

        var updateDirectory = Path.Combine(AppLogger.LogDirectory, "updates");
        Directory.CreateDirectory(updateDirectory);
        var version = update.LatestVersion ?? "latest";
        var installerPath = Path.Combine(updateDirectory, $"NudgeSetup-{version}.exe");

        using var response = await httpClient.GetAsync(update.InstallerDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(installerPath);
        var buffer = new byte[128 * 1024];
        long totalRead = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            totalRead += read;
            if (contentLength is > 0)
            {
                progress?.Report((int)Math.Clamp(totalRead * 100 / contentLength.Value, 0, 100));
            }
        }

        progress?.Report(100);
        return installerPath;
    }

    public void LaunchInstaller(string installerPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
            UseShellExecute = true
        });
    }

    public static bool IsNewerVersion(string latestVersion, string currentVersion)
    {
        if (!Version.TryParse(NormalizeVersion(latestVersion), out var latest) ||
            !Version.TryParse(NormalizeVersion(currentVersion), out var current))
        {
            return false;
        }

        return latest > current;
    }

    private static string NormalizeVersion(string? value)
    {
        return (value ?? "").Trim().TrimStart('v', 'V');
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("assets")] IReadOnlyList<GitHubAsset>? Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);
}
