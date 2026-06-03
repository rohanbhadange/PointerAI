using ClickyClone.Core;
using ClickyClone.Tests;

Run("maps cursor-screen point", MapsCursorScreenPoint);
Run("maps explicit secondary screen with negative coordinates", MapsExplicitSecondaryScreenWithNegativeCoordinates);
Run("clamps out-of-bounds model coordinates", ClampsOutOfBoundsCoordinates);
Run("ignores missing screen number", IgnoresMissingScreenNumber);
Run("maps physical pixels under dpi metadata", MapsPhysicalPixelsUnderDpiMetadata);
Run("maps exact element center on explicit scaled secondary screen", MapsExactElementCenterOnExplicitScaledSecondaryScreen);
Run("maps target bounds with point center", MapsTargetBoundsWithPointCenter);
Run("maps selected UIA element by local id", MapsSelectedUiaElementByLocalId);
await RunAsync("captures screenshot and accessibility catalog", CaptureSmoke.RunAsync);

Console.WriteLine("All local simulation tests passed.");

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

static void MapsSelectedUiaElementByLocalId()
{
    var captures = new[]
    {
        new ScreenCapturePayload(
            "screen 1",
            "image/png",
            "",
            null,
            true,
            1,
            1000,
            800,
            500,
            400,
            10,
            20,
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
