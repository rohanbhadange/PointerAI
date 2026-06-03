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
    string? ElementId = null);

public sealed record ChatBounds(
    double X,
    double Y,
    double Width,
    double Height);
