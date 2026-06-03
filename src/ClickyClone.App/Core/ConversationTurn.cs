namespace ClickyClone.Core;

public sealed record ConversationTurn(
    string UserTranscript,
    string AssistantResponse);
