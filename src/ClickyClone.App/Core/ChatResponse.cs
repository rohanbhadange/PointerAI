namespace ClickyClone.Core;

public sealed record ChatResponse(
    string Text,
    string SpokenText,
    ChatPoint? Point);

public sealed record ChatPoint(
    double X,
    double Y,
    string? Label,
    int? ScreenNumber,
    string? Source = null,
    ChatBounds? Bounds = null,
    string? ElementId = null,
    string? TargetId = null);

public sealed record ChatBounds(
    double X,
    double Y,
    double Width,
    double Height);

public sealed record LocateResponse(
    bool Ok,
    string? Provider,
    double? X,
    double? Y,
    string? Label,
    int? ScreenNumber,
    string? CoordinateSpace,
    string? Error,
    System.Text.Json.JsonElement? RawAction = null);
