using System.IO;
using System.Reflection;
using System.Text.Json;
using ClickyClone.Core;
using ClickyClone.Services;
using ClickyClone.Tests;

Run("settings save and load worker url", SettingsSaveAndLoadWorkerUrl);
Run("settings migrates old worker url", SettingsMigratesOldWorkerUrl);
Run("settings save and load local mode", SettingsSaveAndLoadLocalMode);
Run("parses local env", ParsesLocalEnv);
await RunAsync("local diagnostics reports booleans", LocalDiagnosticsReportsBooleans);
Run("extracts local computer-use point", ExtractsLocalComputerUsePoint);
Run("routes guidance requests with optional pointing", RoutesGuidanceRequestsWithOptionalPointing);
Run("parses wrangler worker url", ParsesWranglerWorkerUrl);
Run("maps cursor-screen point", MapsCursorScreenPoint);
Run("maps explicit secondary screen with negative coordinates", MapsExplicitSecondaryScreenWithNegativeCoordinates);
Run("clamps out-of-bounds model coordinates", ClampsOutOfBoundsCoordinates);
Run("ignores missing screen number", IgnoresMissingScreenNumber);
Run("maps physical pixels under dpi metadata", MapsPhysicalPixelsUnderDpiMetadata);
Run("maps exact element center on explicit scaled secondary screen", MapsExactElementCenterOnExplicitScaledSecondaryScreen);
Run("maps target bounds with point center", MapsTargetBoundsWithPointCenter);
Run("maps computer-use screenshot point", MapsComputerUseScreenshotPoint);
Run("maps selected UIA element by local id", MapsSelectedUiaElementByLocalId);
Run("maps selected visual target by local id", MapsSelectedVisualTargetByLocalId);
await RunAsync("captures screenshot and accessibility catalog", CaptureSmoke.RunAsync);

Console.WriteLine("All local simulation tests passed.");

static void SettingsSaveAndLoadWorkerUrl()
{
    var settingsPath = Path.Combine(Path.GetTempPath(), "ClickyClone.Tests", Guid.NewGuid().ToString("N"), "settings.json");
    var store = new AppSettingsStore(settingsPath);
    store.Save(new AppSettings("worker", "https://example.workers.dev/", false));

    var loaded = store.Load();
    AssertStringEqual("worker", loaded.BackendMode, "backend mode");
    AssertStringEqual("https://example.workers.dev/", loaded.WorkerBaseUrl ?? "", "worker url");
    Assert(!loaded.UseDeveloperWorker);
}

static void SettingsSaveAndLoadLocalMode()
{
    var settingsPath = Path.Combine(Path.GetTempPath(), "ClickyClone.Tests", Guid.NewGuid().ToString("N"), "settings.json");
    var store = new AppSettingsStore(settingsPath);
    store.Save(new AppSettings("local"));

    var loaded = store.Load();
    AssertStringEqual("local", loaded.BackendMode, "backend mode");
}

static void SettingsMigratesOldWorkerUrl()
{
    var settingsPath = Path.Combine(Path.GetTempPath(), "ClickyClone.Tests", Guid.NewGuid().ToString("N"), "settings.json");
    Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
    File.WriteAllText(settingsPath, """{"workerBaseUrl":"https://old.example.workers.dev/","useDeveloperWorker":false}""");

    var loaded = new AppSettingsStore(settingsPath).Load();
    AssertStringEqual("worker", loaded.BackendMode, "backend mode");
    AssertStringEqual("https://old.example.workers.dev/", loaded.WorkerBaseUrl ?? "", "worker url");
}

static void ParsesLocalEnv()
{
    var values = LocalEnv.Parse([
        "# comment",
        "OPENAI_API_KEY = sk-test",
        "ASSEMBLYAI_API_KEY=",
        "ELEVENLABS_VOICE_ID='voice-id'",
        "OPENAI_MODEL=\"gpt-test\""
    ]);

    AssertStringEqual("sk-test", values["OPENAI_API_KEY"], "openai key");
    AssertStringEqual("", values["ASSEMBLYAI_API_KEY"], "blank assemblyai key");
    AssertStringEqual("voice-id", values["ELEVENLABS_VOICE_ID"], "voice id");
    AssertStringEqual("gpt-test", values["OPENAI_MODEL"], "openai model");
}

static async Task LocalDiagnosticsReportsBooleans()
{
    var envPath = Path.Combine(Path.GetTempPath(), "ClickyClone.Tests", Guid.NewGuid().ToString("N"), ".env");
    LocalEnv.Save(envPath, "sk-test", "assembly-test", "eleven-test", "voice-test");
    var client = new LocalProviderClient(envPath);
    var diagnostics = await client.GetDiagnosticsAsync(CancellationToken.None);

    Assert(diagnostics.Secrets is { OpenAI: true, AssemblyAI: true, ElevenLabs: true, ElevenLabsVoice: true });
    AssertStringEqual("openai-computer-use", diagnostics.Locator?.Provider ?? "", "locator provider");
    AssertStringEqual(LocalEnv.DefaultOpenAIComputerModel, diagnostics.Locator?.Model ?? "", "locator model");
}

static void ExtractsLocalComputerUsePoint()
{
    using var document = JsonDocument.Parse("""
    {
      "output": [
        {
          "type": "computer_call",
          "action": { "type": "click", "x": 125.4, "y": 99.6 }
        }
      ]
    }
    """);
    var capture = Capture(screenNumber: 1, isCursorScreen: true, left: 0, top: 0, displayWidth: 1000, displayHeight: 800, shotWidth: 500, shotHeight: 400);

    var point = LocalProviderClient.ExtractComputerUsePointForTest(document.RootElement, "openai-computer-use", "Generate Mesh", capture);
    Assert(point is not null);
    AssertEqual(125, point!.X, "x");
    AssertEqual(100, point.Y, "y");
    AssertStringEqual("computer-use", point.Source ?? "", "source");
}

static void RoutesGuidanceRequestsWithOptionalPointing()
{
    Assert(InvokeCompanionBool("ShouldAttemptPointing", "show me how to add a Python animation"));
    Assert(InvokeCompanionBool("ShouldAttemptPointing", "how can I see solution information"));
    Assert(!InvokeCompanionBool("IsDirectPointingRequest", "show me how to add a Python animation"));
    Assert(InvokeCompanionBool("IsDirectPointingRequest", "point to solution information"));
    Assert(!InvokeCompanionBool("ShouldAttemptPointing", "explain what this error means"));
}

static bool InvokeCompanionBool(string methodName, string transcript)
{
    var method = typeof(CompanionManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
                 ?? throw new InvalidOperationException($"Missing method {methodName}.");
    return (bool)(method.Invoke(null, [transcript]) ?? false);
}

static void ParsesWranglerWorkerUrl()
{
    const string output = "Uploaded pointerai-test\nDeployed pointerai-test triggers\nhttps://pointerai-test.example.workers.dev";
    var workerUrl = WorkerSetupRunner.TryExtractWorkerUrl(output);
    AssertStringEqual("https://pointerai-test.example.workers.dev", workerUrl ?? "", "worker url");
}

static void MapsCursorScreenPoint()
{
    var captures = new[]
    {
        Capture(screenNumber: 1, isCursorScreen: true, left: 0, top: 0, displayWidth: 1920, displayHeight: 1080, shotWidth: 960, shotHeight: 540),
        Capture(screenNumber: 2, isCursorScreen: false, left: 1920, top: 0, displayWidth: 1280, displayHeight: 720, shotWidth: 640, shotHeight: 360)
    };

    Assert(PointMapper.TryMapPoint(new ChatPoint(480, 270, "center", null), captures, out var target));
    AssertEqual(960, target.DesktopPoint.X, "x");
    AssertEqual(540, target.DesktopPoint.Y, "y");
}

static void MapsExplicitSecondaryScreenWithNegativeCoordinates()
{
    var captures = new[]
    {
        Capture(screenNumber: 1, isCursorScreen: true, left: 0, top: 0, displayWidth: 1920, displayHeight: 1080, shotWidth: 1920, shotHeight: 1080),
        Capture(screenNumber: 2, isCursorScreen: false, left: -1280, top: 120, displayWidth: 1280, displayHeight: 720, shotWidth: 640, shotHeight: 360)
    };

    Assert(PointMapper.TryMapPoint(new ChatPoint(320, 180, "terminal", 2), captures, out var target));
    AssertEqual(-640, target.DesktopPoint.X, "x");
    AssertEqual(480, target.DesktopPoint.Y, "y");
}

static void ClampsOutOfBoundsCoordinates()
{
    var captures = new[]
    {
        Capture(screenNumber: 1, isCursorScreen: true, left: 10, top: 20, displayWidth: 1000, displayHeight: 800, shotWidth: 500, shotHeight: 400)
    };

    Assert(PointMapper.TryMapPoint(new ChatPoint(9999, -5, "edge", null), captures, out var target));
    AssertEqual(1010, target.DesktopPoint.X, "x");
    AssertEqual(20, target.DesktopPoint.Y, "y");
}

static void IgnoresMissingScreenNumber()
{
    var captures = new[]
    {
        Capture(screenNumber: 1, isCursorScreen: true, left: 0, top: 0, displayWidth: 100, displayHeight: 100, shotWidth: 100, shotHeight: 100)
    };

    Assert(!PointMapper.TryMapPoint(new ChatPoint(10, 10, "missing", 9), captures, out _));
}

static void MapsPhysicalPixelsUnderDpiMetadata()
{
    var captures = new[]
    {
        new ScreenCapturePayload(
            "screen 1",
            "image/png",
            "",
            null,
            null,
            true,
            1,
            2560,
            1440,
            2560,
            1440,
            100,
            200,
            1.25,
            1.25,
            80,
            160,
            2048,
            1152)
    };

    Assert(PointMapper.TryMapPoint(new ChatPoint(1280, 720, "center", null), captures, out var target));
    AssertEqual(1380, target.DesktopPoint.X, "x");
    AssertEqual(920, target.DesktopPoint.Y, "y");
}

static void MapsExactElementCenterOnExplicitScaledSecondaryScreen()
{
    var captures = new[]
    {
        new ScreenCapturePayload(
            "screen 1",
            "image/png",
            "",
            null,
            null,
            false,
            1,
            1920,
            1080,
            1920,
            1080,
            0,
            0),
        new ScreenCapturePayload(
            "screen 2",
            "image/png",
            "",
            null,
            null,
            true,
            2,
            2560,
            1440,
            2560,
            1440,
            -2560,
            160,
            1.5,
            1.5,
            -1706.6666667,
            106.6666667,
            1706.6666667,
            960)
    };

    Assert(PointMapper.TryMapPoint(new ChatPoint(1412.4, 15.8, "New tab", 2), captures, out var target));
    AssertEqual(-1147.6, target.DesktopPoint.X, "x");
    AssertEqual(175.8, target.DesktopPoint.Y, "y");
    AssertStringEqual("New tab", target.Label ?? "", "label");
    AssertEqual(2, target.ScreenNumber ?? 0, "screen");
}

static void MapsTargetBoundsWithPointCenter()
{
    var captures = new[]
    {
        Capture(screenNumber: 1, isCursorScreen: true, left: 10, top: 20, displayWidth: 1000, displayHeight: 800, shotWidth: 500, shotHeight: 400)
    };

    Assert(PointMapper.TryMapPoint(
        new ChatPoint(125, 100, "Save", 1, "box", new ChatBounds(100, 80, 50, 40)),
        captures,
        out var target));

    AssertEqual(260, target.DesktopPoint.X, "x");
    AssertEqual(220, target.DesktopPoint.Y, "y");
    Assert(target.DesktopBounds is not null);
    var bounds = target.DesktopBounds.GetValueOrDefault();
    AssertEqual(210, bounds.Left, "bounds.left");
    AssertEqual(180, bounds.Top, "bounds.top");
    AssertEqual(100, bounds.Width, "bounds.width");
    AssertEqual(80, bounds.Height, "bounds.height");
}

static void MapsComputerUseScreenshotPoint()
{
    var captures = new[]
    {
        Capture(screenNumber: 1, isCursorScreen: true, left: 10, top: 20, displayWidth: 1000, displayHeight: 800, shotWidth: 500, shotHeight: 400)
    };

    Assert(PointMapper.TryMapPoint(
        new ChatPoint(125, 100, "Generate Mesh", 1, "computer-use"),
        captures,
        out var target));

    AssertEqual(260, target.DesktopPoint.X, "x");
    AssertEqual(220, target.DesktopPoint.Y, "y");
    AssertStringEqual("Generate Mesh", target.Label ?? "", "label");
    AssertEqual(1, target.ScreenNumber ?? 0, "screen");
}

static void MapsSelectedUiaElementByLocalId()
{
    var captures = new[]
    {
        new ScreenCapturePayload(
            "screen 1",
            "image/png",
            "",
            null,
            null,
            true,
            1,
            1000,
            800,
            500,
            400,
            10,
            20,
            VisualTargets:
            [
                new VisualTargetPayload(
                    "C07",
                    "visual-candidate",
                    100,
                    80,
                    50,
                    40,
                    125,
                    100,
                    0.9,
                    "Solve")
            ],
            Elements:
            [
                new ScreenElementPayload(
                    "screen1-el7",
                    "Solve",
                    "Button",
                    100,
                    80,
                    50,
                    40,
                    125,
                    100,
                    "Ansys Mechanical",
                    true,
                    120)
            ])
    };

    Assert(PointMapper.TryMapPoint(
        new ChatPoint(999, 999, "Wrong coordinate", 1, "element", null, "screen1-el7"),
        captures,
        out var target));

    AssertEqual(260, target.DesktopPoint.X, "x");
    AssertEqual(220, target.DesktopPoint.Y, "y");
    AssertStringEqual("Solve", target.Label ?? "", "label");
    Assert(target.DesktopBounds is not null);
    var bounds = target.DesktopBounds.GetValueOrDefault();
    AssertEqual(210, bounds.Left, "bounds.left");
    AssertEqual(180, bounds.Top, "bounds.top");
    AssertEqual(100, bounds.Width, "bounds.width");
    AssertEqual(80, bounds.Height, "bounds.height");
}

static void MapsSelectedVisualTargetByLocalId()
{
    var captures = new[]
    {
        new ScreenCapturePayload(
            "screen 1",
            "image/png",
            "",
            null,
            null,
            true,
            1,
            1000,
            800,
            500,
            400,
            10,
            20,
            VisualTargets:
            [
                new VisualTargetPayload(
                    "C07",
                    "visual-candidate",
                    100,
                    80,
                    50,
                    40,
                    125,
                    100,
                    0.9,
                    "Solve")
            ])
    };

    Assert(PointMapper.TryMapPoint(
        new ChatPoint(999, 999, "Wrong coordinate", 1, "visual-target", null, null, "C07"),
        captures,
        out var target));

    AssertEqual(260, target.DesktopPoint.X, "x");
    AssertEqual(220, target.DesktopPoint.Y, "y");
    AssertStringEqual("Solve", target.Label ?? "", "label");
    Assert(target.DesktopBounds is not null);
    var bounds = target.DesktopBounds.GetValueOrDefault();
    AssertEqual(210, bounds.Left, "bounds.left");
    AssertEqual(180, bounds.Top, "bounds.top");
    AssertEqual(100, bounds.Width, "bounds.width");
    AssertEqual(80, bounds.Height, "bounds.height");
}


static ScreenCapturePayload Capture(
    int screenNumber,
    bool isCursorScreen,
    int left,
    int top,
    int displayWidth,
    int displayHeight,
    int shotWidth,
    int shotHeight)
{
    return new ScreenCapturePayload(
        $"screen {screenNumber}",
        "image/jpeg",
        "",
        null,
        null,
        isCursorScreen,
        screenNumber,
        displayWidth,
        displayHeight,
        shotWidth,
        shotHeight,
        left,
        top);
}

static void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"{name}: ok");
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"{name}: failed - {error.Message}");
        Environment.ExitCode = 1;
        throw;
    }
}

static async Task RunAsync(string name, Func<Task> test)
{
    try
    {
        await test();
        Console.WriteLine($"{name}: ok");
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"{name}: failed - {error.Message}");
        Environment.ExitCode = 1;
        throw;
    }
}

static void Assert(bool condition)
{
    if (!condition)
    {
        throw new InvalidOperationException("assertion failed");
    }
}

static void AssertEqual(double expected, double actual, string label)
{
    if (Math.Abs(expected - actual) > 0.001)
    {
        throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
    }
}

static void AssertStringEqual(string expected, string actual, string label)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
    }
}
