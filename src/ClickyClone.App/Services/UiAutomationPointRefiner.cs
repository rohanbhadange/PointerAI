using System.Text.RegularExpressions;
using System.Windows.Automation;
using ClickyClone.Core;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace ClickyClone.Services;

public sealed class UiAutomationPointRefiner
{
    private static readonly Regex WordRegex = new("[a-z0-9]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "click", "go", "i", "it", "me", "of", "on", "please",
        "point", "show", "the", "there", "this", "to", "where", "you", "button", "control", "cursor"
    };

    public async Task<PointTarget> RefineAsync(
        PointTarget modelPointTarget,
        string transcript,
        IReadOnlyList<ScreenCapturePayload> captures,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(900));

            var refinementTask = Task.Run(
                () => Refine(modelPointTarget, transcript, captures, timeout.Token),
                CancellationToken.None);

            var completedTask = await Task.WhenAny(refinementTask, Task.Delay(TimeSpan.FromMilliseconds(950), cancellationToken));
            if (completedTask != refinementTask)
            {
                AppLogger.Info("UI Automation point refinement timed out.");
                return modelPointTarget;
            }

            return await refinementTask;
        }
        catch (Exception error)
        {
            AppLogger.Error("UI Automation point refinement failed", error);
            return modelPointTarget;
        }
    }

    private static PointTarget Refine(
        PointTarget modelPointTarget,
        string transcript,
        IReadOnlyList<ScreenCapturePayload> captures,
        CancellationToken cancellationToken)
    {
        AppLogger.Info($"UI Automation point refinement started. Label=\"{modelPointTarget.Label ?? ""}\" X={modelPointTarget.DesktopPoint.X:0.0} Y={modelPointTarget.DesktopPoint.Y:0.0}");
        var targetCapture = modelPointTarget.ScreenNumber is int screenNumber
            ? captures.FirstOrDefault(capture => capture.ScreenNumber == screenNumber)
            : captures.FirstOrDefault(capture => capture.IsCursorScreen);

        if (targetCapture is null)
        {
            return modelPointTarget;
        }

        var transcriptWords = ExtractMeaningfulWords(transcript).ToArray();
        var labelWords = ExtractMeaningfulWords(modelPointTarget.Label ?? "").ToArray();
        if (transcriptWords.Length == 0 && labelWords.Length == 0)
        {
            return modelPointTarget;
        }

        var screenLeft = targetCapture.DesktopLeft;
        var screenTop = targetCapture.DesktopTop;
        var screenRight = screenLeft + targetCapture.DisplayWidthInPixels;
        var screenBottom = screenTop + targetCapture.DisplayHeightInPixels;
        var screenArea = targetCapture.DisplayWidthInPixels * targetCapture.DisplayHeightInPixels;

        var root = AutomationElement.RootElement;
        var pointElements = FindElementsAroundPoint(modelPointTarget.DesktopPoint);
        var elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        AutomationCandidate? bestCandidate = null;
        var consideredCandidateCount = 0;

        foreach (var element in pointElements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = TryBuildCandidate(
                element,
                modelPointTarget,
                transcriptWords,
                labelWords,
                screenLeft,
                screenTop,
                screenRight,
                screenBottom,
                screenArea);

            if (candidate is null)
            {
                continue;
            }

            consideredCandidateCount++;
            if (bestCandidate is null || candidate.Score > bestCandidate.Score)
            {
                bestCandidate = candidate;
            }
        }

        for (var index = 0; index < elements.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var element = elements[index];
            var candidate = TryBuildCandidate(
                element,
                modelPointTarget,
                transcriptWords,
                labelWords,
                screenLeft,
                screenTop,
                screenRight,
                screenBottom,
                screenArea);

            if (candidate is null)
            {
                continue;
            }

            consideredCandidateCount++;
            if (bestCandidate is null || candidate.Score > bestCandidate.Score)
            {
                bestCandidate = candidate;
            }
        }

        if (bestCandidate is null || bestCandidate.Score < 80)
        {
            var topCandidateText = bestCandidate is null
                ? "none"
                : $"Name=\"{bestCandidate.Name}\" Score={bestCandidate.Score:0.0} CenterX={bestCandidate.Center.X:0.0} CenterY={bestCandidate.Center.Y:0.0}";
            AppLogger.Info($"UI Automation point refinement found no high-confidence candidate. Candidates={consideredCandidateCount} Top={topCandidateText}");
            return modelPointTarget;
        }

        AppLogger.Info(
            $"UI Automation point refined. Name=\"{bestCandidate.Name}\" Score={bestCandidate.Score:0.0} " +
            $"OldX={modelPointTarget.DesktopPoint.X:0.0} OldY={modelPointTarget.DesktopPoint.Y:0.0} " +
            $"NewX={bestCandidate.Center.X:0.0} NewY={bestCandidate.Center.Y:0.0}");

        return modelPointTarget with { DesktopPoint = bestCandidate.Center, Label = bestCandidate.Name };
    }

    private static IEnumerable<AutomationElement> FindElementsAroundPoint(WpfPoint desktopPoint)
    {
        var offsets = new[]
        {
            (X: 0, Y: 0),
            (X: -24, Y: 0),
            (X: 24, Y: 0),
            (X: 0, Y: -24),
            (X: 0, Y: 24),
            (X: -48, Y: 0),
            (X: 48, Y: 0),
            (X: 0, Y: -48),
            (X: 0, Y: 48)
        };

        var seenRuntimeIds = new HashSet<string>();
        foreach (var offset in offsets)
        {
            AutomationElement? element;
            try
            {
                element = AutomationElement.FromPoint(new WpfPoint(desktopPoint.X + offset.X, desktopPoint.Y + offset.Y));
            }
            catch
            {
                continue;
            }

            while (element is not null)
            {
                string runtimeId;
                try
                {
                    runtimeId = string.Join(".", element.GetRuntimeId());
                }
                catch
                {
                    break;
                }

                if (seenRuntimeIds.Add(runtimeId))
                {
                    yield return element;
                }

                try
                {
                    element = TreeWalker.ControlViewWalker.GetParent(element);
                }
                catch
                {
                    break;
                }
            }
        }
    }

    private static AutomationCandidate? TryBuildCandidate(
        AutomationElement element,
        PointTarget modelPointTarget,
        IReadOnlyCollection<string> transcriptWords,
        IReadOnlyCollection<string> labelWords,
        int screenLeft,
        int screenTop,
        int screenRight,
        int screenBottom,
        int screenArea)
    {
        WpfRect boundingRectangle;
        string name;
        ControlType controlType;
        bool isOffscreen;
        try
        {
            boundingRectangle = element.Current.BoundingRectangle;
            name = element.Current.Name ?? "";
            controlType = element.Current.ControlType;
            isOffscreen = element.Current.IsOffscreen;
        }
        catch
        {
            return null;
        }

        if (isOffscreen || string.IsNullOrWhiteSpace(name) || boundingRectangle.IsEmpty)
        {
            return null;
        }

        if (boundingRectangle.Width < 4 || boundingRectangle.Height < 4)
        {
            return null;
        }

        if (boundingRectangle.Right < screenLeft ||
            boundingRectangle.Left > screenRight ||
            boundingRectangle.Bottom < screenTop ||
            boundingRectangle.Top > screenBottom)
        {
            return null;
        }

        var area = boundingRectangle.Width * boundingRectangle.Height;
        if (area > screenArea * 0.45)
        {
            return null;
        }

        var center = new WpfPoint(
            boundingRectangle.Left + boundingRectangle.Width / 2,
            boundingRectangle.Top + boundingRectangle.Height / 2);
        var distance = Distance(center, modelPointTarget.DesktopPoint);
        var nameWords = ExtractMeaningfulWords(name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var transcriptOverlapCount = transcriptWords.Count(nameWords.Contains);
        var labelOverlapCount = labelWords.Count(nameWords.Contains);

        var exactTranscriptBonus = transcriptWords.Count > 0 && transcriptWords.All(nameWords.Contains) ? 260 : 0;
        var exactLabelBonus = labelWords.Count > 0 && labelWords.All(nameWords.Contains) ? 65 : 0;
        var controlBonus = IsClickableControl(controlType) ? 35 : 0;
        var distanceBonus = Math.Max(0, 110 - distance / 5);
        var sizePenalty = area > 160_000 ? 40 : 0;
        var score = exactTranscriptBonus + exactLabelBonus + transcriptOverlapCount * 75 + labelOverlapCount * 18 + controlBonus + distanceBonus - sizePenalty;

        if (transcriptOverlapCount == 0 && labelOverlapCount == 0)
        {
            return null;
        }

        if (distance > 700 && exactTranscriptBonus == 0 && transcriptOverlapCount < 2)
        {
            return null;
        }

        if (distance > 1400 && exactTranscriptBonus == 0)
        {
            return null;
        }

        return new AutomationCandidate(name.Trim(), center, score);
    }

    private static IEnumerable<string> ExtractMeaningfulWords(string text)
    {
        return ExtractWords(text)
            .Where(word => word.Length > 1 && !StopWords.Contains(word));
    }

    private static IEnumerable<string> ExtractWords(string text)
    {
        foreach (Match match in WordRegex.Matches(text.ToLowerInvariant()))
        {
            yield return match.Value;
        }
    }

    private static bool IsClickableControl(ControlType controlType)
    {
        return controlType == ControlType.Button ||
               controlType == ControlType.Hyperlink ||
               controlType == ControlType.MenuItem ||
               controlType == ControlType.TabItem ||
               controlType == ControlType.ListItem ||
               controlType == ControlType.Edit ||
               controlType == ControlType.ComboBox ||
               controlType == ControlType.CheckBox ||
               controlType == ControlType.RadioButton;
    }

    private static double Distance(WpfPoint firstPoint, WpfPoint secondPoint)
    {
        var deltaX = firstPoint.X - secondPoint.X;
        var deltaY = firstPoint.Y - secondPoint.Y;
        return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
    }

    private sealed record AutomationCandidate(string Name, WpfPoint Center, double Score);
}
