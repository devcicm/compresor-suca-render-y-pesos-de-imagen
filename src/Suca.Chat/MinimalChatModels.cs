using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SUCA_Compressor
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ChatDeliveryStatus
    {
        Pending,
        Queued,
        Processing,
        Delivered,
        Acknowledged,
        Failed,
        DeadLetter
    }

    public sealed class ChatCompletionRequest
    {
        public string Model { get; set; } = "";
        public List<LmStudioChatMessage> Messages { get; set; } = new();
        public double Temperature { get; set; } = 0;
        public int MaxTokens { get; set; } = 512;
        public string? ConversationId { get; set; }
        public string? IdempotencyKey { get; set; }
        public ChatRagOptions? Rag { get; set; }
        public LmStudioOptions? LmStudio { get; set; }
    }

    public sealed class ChatRagOptions
    {
        public string? SourceDirectory { get; set; }
        public string? SemanticIndexPath { get; set; }
        public bool RefreshIndexBeforeQuery { get; set; } = true;
        public bool Recursive { get; set; } = true;
        public int TopK { get; set; } = 4;
        public int ChunkSize { get; set; } = 1200;
        public int ChunkOverlap { get; set; } = 180;
        public string[]? Extensions { get; set; }
    }

    public sealed class ReliableChatSendRequest
    {
        public string? ConversationId { get; set; }
        public string UserMessage { get; set; } = "";
        public string? Title { get; set; }
        public string? IdempotencyKey { get; set; }
        public LmStudioOptions LmStudio { get; set; } = new();
        public ChatRagOptions? Rag { get; set; }
        public int MaxDeliveryAttempts { get; set; } = 3;
    }

    // Eventos del stream de chat (SSE): el host los reenvia tal cual al navegador.
    public enum ChatStreamEventType
    {
        Started,
        Context,
        Delta,
        Completed,
        Error
    }

    public sealed class ChatStreamEvent
    {
        public ChatStreamEventType Type { get; init; }
        public string? ConversationId { get; init; }
        public string? MessageId { get; init; }
        public string? Text { get; init; }
        public object? Context { get; init; }

        public static ChatStreamEvent Started(string conversationId) =>
            new() { Type = ChatStreamEventType.Started, ConversationId = conversationId };

        public static ChatStreamEvent FromContext(object context) =>
            new() { Type = ChatStreamEventType.Context, Context = context };

        public static ChatStreamEvent Delta(string text) =>
            new() { Type = ChatStreamEventType.Delta, Text = text };

        public static ChatStreamEvent Completed(string conversationId, string messageId, string fullText) =>
            new()
            {
                Type = ChatStreamEventType.Completed,
                ConversationId = conversationId,
                MessageId = messageId,
                Text = fullText
            };

        public static ChatStreamEvent Error(string message) =>
            new() { Type = ChatStreamEventType.Error, Text = message };
    }

    public sealed class ReliableChatEnvelope
    {
        public string MessageId { get; set; } = "";
        public string ConversationId { get; set; } = "";
        public string Role { get; set; } = "user";
        public string Sender { get; set; } = "";
        public string Recipient { get; set; } = "";
        public string Content { get; set; } = "";
        public string? ParentMessageId { get; set; }
        public string? CorrelationId { get; set; }
        public string? IdempotencyKey { get; set; }
        public int SequenceNumber { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? LastAttemptAtUtc { get; set; }
        public DateTimeOffset? DeliveredAtUtc { get; set; }
        public DateTimeOffset? AcknowledgedAtUtc { get; set; }
        public int RetryCount { get; set; }
        public bool AckRequired { get; set; } = true;
        public string ChecksumSha256 { get; set; } = "";
        public ChatDeliveryStatus DeliveryStatus { get; set; } = ChatDeliveryStatus.Pending;
        public ParsedLlmJsonResponse? ParsedResponse { get; set; }
        public string? Error { get; set; }
    }

    public sealed class ChatConversation
    {
        public string ConversationId { get; set; } = "";
        public string Title { get; set; } = "";
        public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastUpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
        public int NextSequenceNumber { get; set; } = 1;
        public List<ReliableChatEnvelope> Messages { get; set; } = new();
    }

    public sealed class ReliableChatResult
    {
        public ChatConversation Conversation { get; set; } = new();
        public ReliableChatEnvelope UserEnvelope { get; set; } = new();
        public ReliableChatEnvelope AssistantEnvelope { get; set; } = new();
        public ParsedLlmJsonResponse Response { get; set; } = new();
        public List<object> Contexts { get; set; } = new();
    }

    public sealed class OpenAiStyleChatResponse
    {
        public string Id { get; set; } = $"chatcmpl-{Guid.NewGuid():N}";
        public string Object { get; set; } = "chat.completion";
        public long Created { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public string Model { get; set; } = "";
        public string ConversationId { get; set; } = "";
        public DeliveryReceipt Delivery { get; set; } = new();
        public ParsedLlmJsonResponse Parsed { get; set; } = new();
        public List<object> Contexts { get; set; } = new();
        public List<OpenAiStyleChoice> Choices { get; set; } = new();
    }

    public sealed class OpenAiStyleChoice
    {
        public int Index { get; set; }
        public OpenAiStyleMessage Message { get; set; } = new();
        public string FinishReason { get; set; } = "stop";
    }

    public sealed class OpenAiStyleMessage
    {
        public string Role { get; set; } = "assistant";
        public string Content { get; set; } = "";
    }

    public sealed class DeliveryReceipt
    {
        public string MessageId { get; set; } = "";
        public string ConversationId { get; set; } = "";
        public string Status { get; set; } = "";
        public int RetryCount { get; set; }
        public DateTimeOffset? DeliveredAtUtc { get; set; }
        public DateTimeOffset? AcknowledgedAtUtc { get; set; }
    }
}
