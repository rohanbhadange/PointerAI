using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using Nudge.Core;

namespace Nudge.Services;

public sealed class WorkerSetupRunner
{
    private static readonly Regex WorkerUrlRegex = new(@"https://[a-zA-Z0-9.-]+\.workers\.dev", RegexOptions.Compiled);
    private readonly string deployRoot;

    public WorkerSetupRunner()
    {
        deployRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nudge",
            "worker-deploy");
    }

    public async Task<bool> HasInternetAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var response = await client.GetAsync("https://workers.cloudflare.com", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<WorkerDiagnostics> ValidateWorkerAsync(
        string workerUrl,
        CancellationToken cancellationToken)
    {
        var client = new WorkerClient(new Uri(NormalizeWorkerUrl(workerUrl)));
        await client.CheckHealthAsync(cancellationToken);
        return await client.GetDiagnosticsAsync(cancellationToken);
    }

    public static string NormalizeWorkerUrl(string workerUrl)
    {
        return workerUrl.Trim().TrimEnd('/') + "/";
    }

    public static string? TryExtractWorkerUrl(string text)
    {
        return WorkerUrlRegex.Match(text) is { Success: true } match
            ? match.Value
            : null;
    }

    public string PrepareDeployPackage()
    {
        Directory.CreateDirectory(deployRoot);
        var appBase = AppContext.BaseDirectory;
        var bundledWorker = Path.Combine(appBase, "SetupWorker", "nudge-worker.js");
        if (!File.Exists(bundledWorker))
        {
            bundledWorker = Path.Combine(appBase, "worker", "nudge-worker.js");
        }

        if (!File.Exists(bundledWorker))
        {
            bundledWorker = Path.GetFullPath(Path.Combine(appBase, "..", "..", "..", "..", "..", "worker", "nudge-worker.js"));
        }

        if (!File.Exists(bundledWorker))
        {
            throw new FileNotFoundException("Bundled Worker file was not found.", bundledWorker);
        }

        File.Copy(bundledWorker, Path.Combine(deployRoot, "nudge-worker.js"), overwrite: true);
        var workerName = "nudge-" + Guid.NewGuid().ToString("N")[..8];

        File.WriteAllText(
            Path.Combine(deployRoot, "wrangler.jsonc"),
            $$"""
            {
              "name": "{{workerName}}",
              "main": "nudge-worker.js",
              "compatibility_date": "2025-01-01"
            }
            """);
        return deployRoot;
    }
}
