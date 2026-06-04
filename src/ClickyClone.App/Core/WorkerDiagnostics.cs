namespace ClickyClone.Core;

public sealed record WorkerDiagnostics(
    bool Ok,
    string? Service,
    string? WorkerVersion,
    WorkerSecretDiagnostics? Secrets,
    WorkerLocatorDiagnostics? Locator,
    WorkerChatDiagnostics? Chat);

public sealed record WorkerSecretDiagnostics(
    bool OpenAI,
    bool AssemblyAI,
    bool ElevenLabs,
    bool ElevenLabsVoice);

public sealed record WorkerLocatorDiagnostics(
    string? Provider,
    string? Model);

public sealed record WorkerChatDiagnostics(
    string? Model);
