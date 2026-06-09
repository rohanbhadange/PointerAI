namespace Nudge;

internal static class AppConfig
{
    public const string AppVersion = "1.0.0";
    public const string WorkerVersion = "1.0.0";
    public static readonly Uri GitHubLatestReleaseUri = new("https://api.github.com/repos/rohanbhadange/PointerAI/releases/latest");
    public static readonly Uri GitHubReleasesUri = new("https://github.com/rohanbhadange/PointerAI/releases");
    public static readonly Uri WorkerBaseUri = new("https://nudge.rohanbhadange18.workers.dev");
}
