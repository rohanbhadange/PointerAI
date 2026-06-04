using System.Net.WebSockets;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ClickyClone.Services;

public sealed class AssemblyAIStreamingClient : IDisposable
{
    private readonly IBackendClient backendClient;
    private readonly object transcriptLock = new();
    private readonly Dictionary<int, string> committedTurns = [];
    private ClientWebSocket? webSocket;
    private CancellationTokenSource? receiveCancellation;
    private Task? receiveTask;
    private TaskCompletionSource<string>? finalTranscriptCompletion;
    private int activeTurnOrder;
    private string activeTurnText = "";
    private bool isFinalizing;

    public AssemblyAIStreamingClient(IBackendClient backendClient)
    {
        this.backendClient = backendClient;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Cancel();
        AppLogger.Info("AssemblyAI streaming start requested.");

        var token = await backendClient.GetAssemblyAITokenAsync(cancellationToken);
        var websocketUri = new Uri(
            "wss://streaming.assemblyai.com/v3/ws" +
            "?sample_rate=16000" +
            "&encoding=pcm_s16le" +
            "&format_turns=true" +
            "&speech_model=u3-rt-pro" +
            $"&token={Uri.EscapeDataString(token)}");

        webSocket = new ClientWebSocket();
        await webSocket.ConnectAsync(websocketUri, cancellationToken);
        AppLogger.Info($"AssemblyAI websocket connected. State={webSocket.State}");

        receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        receiveTask = Task.Run(() => ReceiveLoopAsync(receiveCancellation.Token), CancellationToken.None);
    }

    public void SendAudio(byte[] pcm16Bytes)
    {
        var socket = webSocket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            AppLogger.Info("Audio chunk dropped because AssemblyAI websocket is not open.");
            return;
        }

        _ = socket.SendAsync(
            pcm16Bytes,
            WebSocketMessageType.Binary,
            true,
            CancellationToken.None);
    }

    public async Task<string> StopAndFinalizeAsync(CancellationToken cancellationToken)
    {
        var socket = webSocket;
        if (socket is null)
        {
            AppLogger.Info("AssemblyAI finalize requested without an active websocket.");
            return "";
        }

        isFinalizing = true;
        AppLogger.Info("AssemblyAI finalize requested.");
        finalTranscriptCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await SendJsonAsync(new { type = "ForceEndpoint" }, cancellationToken);

        var completedTask = await Task.WhenAny(
            finalTranscriptCompletion.Task,
            Task.Delay(TimeSpan.FromSeconds(2.8), cancellationToken));

        var transcript = completedTask == finalTranscriptCompletion.Task
            ? await finalTranscriptCompletion.Task
            : ComposeTranscript();
        AppLogger.Info($"AssemblyAI finalize completed. TranscriptLength={transcript.Length}");

        await SendJsonAsync(new { type = "Terminate" }, CancellationToken.None);
        await CloseSocketAsync();
        return transcript.Trim();
    }

    public void Cancel()
    {
        AppLogger.Info("AssemblyAI streaming cancelled/reset.");
        receiveCancellation?.Cancel();
        receiveCancellation?.Dispose();
        receiveCancellation = null;

        if (webSocket is not null)
        {
            try
            {
                webSocket.Abort();
            }
            catch
            {
            }

            webSocket.Dispose();
            webSocket = null;
        }

        lock (transcriptLock)
        {
            committedTurns.Clear();
            activeTurnOrder = 0;
            activeTurnText = "";
            isFinalizing = false;
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var buffer = new byte[64 * 1024];

            while (!cancellationToken.IsCancellationRequested && webSocket?.State == WebSocketState.Open)
            {
                using var messageStream = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await webSocket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        AppLogger.Info("AssemblyAI websocket close received.");
                        return;
                    }

                    messageStream.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    HandleTextMessage(Encoding.UTF8.GetString(messageStream.ToArray()));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            AppLogger.Error("AssemblyAI receive loop failed", error);
            finalTranscriptCompletion?.TrySetException(error);
        }
    }

    private void HandleTextMessage(string message)
    {
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var typeElement)
            ? typeElement.GetString()
            : null;

        if (!string.Equals(type, "Turn", StringComparison.OrdinalIgnoreCase))
        {
            AppLogger.Info($"AssemblyAI message received. Type={type ?? "unknown"}");
            return;
        }

        var transcript = root.TryGetProperty("transcript", out var transcriptElement)
            ? transcriptElement.GetString() ?? ""
            : "";

        var turnOrder = root.TryGetProperty("turn_order", out var orderElement)
            ? orderElement.GetInt32()
            : activeTurnOrder;

        var isEndOfTurn = root.TryGetProperty("end_of_turn", out var endElement) && endElement.GetBoolean();
        var isFormatted = root.TryGetProperty("turn_is_formatted", out var formattedElement) && formattedElement.GetBoolean();
        if (!string.IsNullOrWhiteSpace(transcript) || isEndOfTurn || isFormatted)
        {
            AppLogger.Info($"AssemblyAI turn. Order={turnOrder} End={isEndOfTurn} Formatted={isFormatted} TranscriptLength={transcript.Length}");
        }

        lock (transcriptLock)
        {
            if (isEndOfTurn || isFormatted)
            {
                if (!string.IsNullOrWhiteSpace(transcript))
                {
                    committedTurns[turnOrder] = transcript.Trim();
                }

                activeTurnText = "";
            }
            else
            {
                activeTurnOrder = turnOrder;
                activeTurnText = transcript.Trim();
            }

            if (isFinalizing && (isEndOfTurn || isFormatted))
            {
                finalTranscriptCompletion?.TrySetResult(ComposeTranscript());
            }
        }
    }

    private string ComposeTranscript()
    {
        lock (transcriptLock)
        {
            var parts = committedTurns
                .OrderBy(pair => pair.Key)
                .Select(pair => pair.Value)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToList();

            if (!string.IsNullOrWhiteSpace(activeTurnText))
            {
                parts.Add(activeTurnText);
            }

            return string.Join(" ", parts);
        }
    }

    private async Task SendJsonAsync(object payload, CancellationToken cancellationToken)
    {
        if (webSocket is null || webSocket.State != WebSocketState.Open)
        {
            return;
        }

        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        AppLogger.Info($"AssemblyAI control message sent. Payload={json}");
    }

    private async Task CloseSocketAsync()
    {
        receiveCancellation?.Cancel();

        if (webSocket is not null)
        {
            try
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
            catch
            {
            }

            webSocket.Dispose();
            webSocket = null;
        }
    }

    public void Dispose()
    {
        Cancel();
    }
}
