namespace Nudge.Core;

public sealed record CompanionStatus(
    CompanionVoiceState VoiceState,
    string StatusText,
    string? LastTranscript = null,
    string? LastResponse = null,
    string? LastError = null);
