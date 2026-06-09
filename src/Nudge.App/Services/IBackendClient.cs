using Nudge.Core;

namespace Nudge.Services;

public interface IBackendClient
{
    Task CheckHealthAsync(CancellationToken cancellationToken);

    Task<WorkerDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken);

    Task<bool> CheckPointingSelfTestAsync(CancellationToken cancellationToken);

    Task<string> GetAssemblyAITokenAsync(CancellationToken cancellationToken);

    Task<ChatResponse> SendChatAsync(
        string transcript,
        IReadOnlyList<ScreenCapturePayload> images,
        IReadOnlyList<ConversationTurn> conversationHistory,
        CancellationToken cancellationToken);

    Task<LocateResponse> LocateAsync(
        string goal,
        ScreenCapturePayload capture,
        string provider,
        CancellationToken cancellationToken);

    Task<string> TranscribeAudioAsync(byte[] wavBytes, CancellationToken cancellationToken);

    Task<byte[]> TextToSpeechAsync(string text, CancellationToken cancellationToken);
}
