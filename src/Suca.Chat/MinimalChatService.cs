using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SUCA_Compressor
{
    public interface IReliableChatService
    {
        Task<ReliableChatResult> SendAsync(ReliableChatSendRequest request, CancellationToken cancellationToken = default);
        Task<OpenAiStyleChatResponse> CompleteAsync(ChatCompletionRequest request, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<ChatConversation>> ListConversationsAsync(CancellationToken cancellationToken = default);
        Task<ChatConversation?> GetConversationAsync(string conversationId, CancellationToken cancellationToken = default);
    }

    internal sealed class ReliableChatStore
    {
        private readonly string _rootPath;
        private readonly IAtomicFileStore _fileStore;
        private readonly IAsyncKeyedLock _keyedLock;

        public ReliableChatStore(string rootPath, IAtomicFileStore fileStore, IAsyncKeyedLock keyedLock)
        {
            _rootPath = rootPath;
            _fileStore = fileStore;
            _keyedLock = keyedLock;
            Directory.CreateDirectory(_rootPath);
            Directory.CreateDirectory(Path.Combine(_rootPath, "conversations"));
            Directory.CreateDirectory(Path.Combine(_rootPath, "outbox"));
            Directory.CreateDirectory(Path.Combine(_rootPath, "inbox"));
            Directory.CreateDirectory(Path.Combine(_rootPath, "dead-letter"));
        }

        public async Task<ChatConversation> GetOrCreateConversationAsync(string? conversationId, string? title, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(conversationId))
            {
                var existing = await GetConversationAsync(conversationId, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                    return existing;
            }

            var created = new ChatConversation
            {
                ConversationId = conversationId ?? Guid.NewGuid().ToString("N"),
                Title = string.IsNullOrWhiteSpace(title) ? "New Chat" : title.Trim()
            };
            await SaveConversationAsync(created, cancellationToken).ConfigureAwait(false);
            return created;
        }

        public async Task<ChatConversation?> GetConversationAsync(string conversationId, CancellationToken cancellationToken)
        {
            string path = ConversationPath(conversationId);
            if (!File.Exists(path))
                return null;

            string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<ChatConversation>(json, JsonDefaults.Loose);
        }

        public async Task<IReadOnlyList<ChatConversation>> ListConversationsAsync(CancellationToken cancellationToken)
        {
            var list = new List<ChatConversation>();
            string dir = Path.Combine(_rootPath, "conversations");
            foreach (string path in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                var conv = JsonSerializer.Deserialize<ChatConversation>(json, JsonDefaults.Loose);
                if (conv is not null)
                    list.Add(conv);
            }

            return list.OrderByDescending(x => x.LastUpdatedAtUtc).ToList();
        }

        public async Task SaveConversationAsync(ChatConversation conversation, CancellationToken cancellationToken)
        {
            conversation.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
            string path = ConversationPath(conversation.ConversationId);
            string json = JsonSerializer.Serialize(conversation, JsonDefaults.Indented);
            await _fileStore.WriteAllTextAtomicAsync(path, json, cancellationToken).ConfigureAwait(false);
        }

        public async Task AppendEnvelopeAsync(ChatConversation conversation, ReliableChatEnvelope envelope, CancellationToken cancellationToken)
        {
            await using var lease = await _keyedLock.AcquireAsync(conversation.ConversationId, cancellationToken).ConfigureAwait(false);
            var loaded = await GetConversationAsync(conversation.ConversationId, cancellationToken).ConfigureAwait(false) ?? conversation;
            int existingIndex = loaded.Messages.FindIndex(x => string.Equals(x.MessageId, envelope.MessageId, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
                loaded.Messages[existingIndex] = envelope;
            else
                loaded.Messages.Add(envelope);

            loaded.NextSequenceNumber = Math.Max(loaded.NextSequenceNumber, envelope.SequenceNumber + 1);
            await SaveConversationAsync(loaded, cancellationToken).ConfigureAwait(false);
            conversation.Messages = loaded.Messages;
            conversation.NextSequenceNumber = loaded.NextSequenceNumber;

            string stateFolder = envelope.DeliveryStatus switch
            {
                ChatDeliveryStatus.Delivered or ChatDeliveryStatus.Acknowledged => "inbox",
                ChatDeliveryStatus.DeadLetter => "dead-letter",
                _ => "outbox"
            };

            string statePath = Path.Combine(_rootPath, stateFolder, $"{envelope.MessageId}.json");
            string json = JsonSerializer.Serialize(envelope, JsonDefaults.Indented);
            await _fileStore.WriteAllTextAtomicAsync(statePath, json, cancellationToken).ConfigureAwait(false);
        }

        public ReliableChatEnvelope? FindByIdempotencyKey(ChatConversation conversation, string? idempotencyKey)
        {
            if (string.IsNullOrWhiteSpace(idempotencyKey))
                return null;

            return conversation.Messages
                .OrderByDescending(x => x.SequenceNumber)
                .FirstOrDefault(x => string.Equals(x.IdempotencyKey, idempotencyKey, StringComparison.OrdinalIgnoreCase));
        }

        private string ConversationPath(string conversationId) => Path.Combine(_rootPath, "conversations", $"{conversationId}.json");
    }

    public sealed class ReliableChatService : IReliableChatService
    {
        private readonly ReliableChatStore _store;
        private readonly ILogger<ReliableChatService> _logger;
        private readonly IRuntimeObservability _observability;

        public ReliableChatService(
            IHostEnvironment hostEnvironment,
            IAtomicFileStore fileStore,
            IAsyncKeyedLock keyedLock,
            ILogger<ReliableChatService> logger,
            IRuntimeObservability observability)
        {
            string root = Path.Combine(hostEnvironment.ContentRootPath, "runtime", "chat");
            _store = new ReliableChatStore(root, fileStore, keyedLock);
            _logger = logger;
            _observability = observability;
        }

        public async Task<ReliableChatResult> SendAsync(ReliableChatSendRequest request, CancellationToken cancellationToken = default)
        {
            ValidateRequest(request);
            _observability.RecordStart("minichat-send");

            ChatConversation conversation = await _store.GetOrCreateConversationAsync(request.ConversationId, request.Title, cancellationToken).ConfigureAwait(false);
            ReliableChatEnvelope? existing = _store.FindByIdempotencyKey(conversation, request.IdempotencyKey);
            if (existing is not null)
            {
                ReliableChatEnvelope? paired = conversation.Messages.FirstOrDefault(x => x.ParentMessageId == existing.MessageId && x.Role == "assistant");
                return new ReliableChatResult
                {
                    Conversation = conversation,
                    UserEnvelope = existing,
                    AssistantEnvelope = paired ?? new ReliableChatEnvelope(),
                    Response = paired?.ParsedResponse ?? new ParsedLlmJsonResponse { Status = LlmJsonParseStatus.Partial, Answer = paired?.Content }
                };
            }

            var userEnvelope = CreateEnvelope(conversation, "user", "user", "assistant", request.UserMessage, request.IdempotencyKey, parentMessageId: null);
            userEnvelope.DeliveryStatus = ChatDeliveryStatus.Acknowledged;
            userEnvelope.DeliveredAtUtc = DateTimeOffset.UtcNow;
            userEnvelope.AcknowledgedAtUtc = DateTimeOffset.UtcNow;
            await _store.AppendEnvelopeAsync(conversation, userEnvelope, cancellationToken).ConfigureAwait(false);

            var assistantEnvelope = CreateEnvelope(conversation, "assistant", "assistant", "user", string.Empty, null, userEnvelope.MessageId);
            assistantEnvelope.DeliveryStatus = ChatDeliveryStatus.Queued;
            await _store.AppendEnvelopeAsync(conversation, assistantEnvelope, cancellationToken).ConfigureAwait(false);

            List<object> contexts = new();
            ParsedLlmJsonResponse response = new() { Status = LlmJsonParseStatus.Failed };

            int maxAttempts = Math.Max(1, request.MaxDeliveryAttempts);
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                assistantEnvelope.RetryCount = attempt - 1;
                assistantEnvelope.LastAttemptAtUtc = DateTimeOffset.UtcNow;
                assistantEnvelope.DeliveryStatus = ChatDeliveryStatus.Processing;
                await _store.AppendEnvelopeAsync(conversation, assistantEnvelope, cancellationToken).ConfigureAwait(false);

                try
                {
                    using var client = new LmStudioClient(NormalizeLmStudioOptions(request.LmStudio));
                    contexts = await ResolveContextsAsync(client, request.Rag, request.UserMessage, cancellationToken).ConfigureAwait(false);
                    response = await client.ChatAsJsonAsync(
                        BuildPromptMessages(conversation, request.UserMessage, contexts),
                        "minimal_chat_reply",
                        cancellationToken).ConfigureAwait(false);

                    assistantEnvelope.Content = response.Answer ?? string.Empty;
                    assistantEnvelope.ParsedResponse = response;
                    assistantEnvelope.DeliveryStatus = ChatDeliveryStatus.Acknowledged;
                    assistantEnvelope.DeliveredAtUtc = DateTimeOffset.UtcNow;
                    assistantEnvelope.AcknowledgedAtUtc = DateTimeOffset.UtcNow;
                    assistantEnvelope.ChecksumSha256 = ComputeChecksum(assistantEnvelope.Content);
                    await _store.AppendEnvelopeAsync(conversation, assistantEnvelope, cancellationToken).ConfigureAwait(false);

                    _observability.RecordSuccess("minichat-send", assistantEnvelope.MessageId);
                    return new ReliableChatResult
                    {
                        Conversation = conversation,
                        UserEnvelope = userEnvelope,
                        AssistantEnvelope = assistantEnvelope,
                        Response = response,
                        Contexts = contexts
                    };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    assistantEnvelope.Error = ex.Message;
                    assistantEnvelope.DeliveryStatus = attempt >= maxAttempts ? ChatDeliveryStatus.DeadLetter : ChatDeliveryStatus.Failed;
                    await _store.AppendEnvelopeAsync(conversation, assistantEnvelope, cancellationToken).ConfigureAwait(false);
                    _logger.LogWarning(ex, "Reliable chat attempt {Attempt} failed for conversation {ConversationId}", attempt, conversation.ConversationId);

                    if (attempt < maxAttempts)
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(8, attempt * 2)), cancellationToken).ConfigureAwait(false);
                }
            }

            _observability.RecordFailure("minichat-send", new InvalidOperationException("Message delivery exhausted retries."));
            return new ReliableChatResult
            {
                Conversation = conversation,
                UserEnvelope = userEnvelope,
                AssistantEnvelope = assistantEnvelope,
                Response = response,
                Contexts = contexts
            };
        }

        public async Task<OpenAiStyleChatResponse> CompleteAsync(ChatCompletionRequest request, CancellationToken cancellationToken = default)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            string userMessage = request.Messages.LastOrDefault(x => string.Equals(x.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content ?? string.Empty;
            var send = new ReliableChatSendRequest
            {
                ConversationId = request.ConversationId,
                UserMessage = userMessage,
                IdempotencyKey = request.IdempotencyKey,
                LmStudio = request.LmStudio is null
                    ? new LmStudioOptions { ChatModel = request.Model, Temperature = request.Temperature, MaxTokens = request.MaxTokens }
                    : NormalizeLmStudioOptions(request.LmStudio, request.Model, request.Temperature, request.MaxTokens),
                Rag = request.Rag
            };

            var result = await SendAsync(send, cancellationToken).ConfigureAwait(false);
            return new OpenAiStyleChatResponse
            {
                Model = send.LmStudio.ChatModel,
                ConversationId = result.Conversation.ConversationId,
                Delivery = new DeliveryReceipt
                {
                    MessageId = result.AssistantEnvelope.MessageId,
                    ConversationId = result.Conversation.ConversationId,
                    Status = result.AssistantEnvelope.DeliveryStatus.ToString(),
                    RetryCount = result.AssistantEnvelope.RetryCount,
                    DeliveredAtUtc = result.AssistantEnvelope.DeliveredAtUtc,
                    AcknowledgedAtUtc = result.AssistantEnvelope.AcknowledgedAtUtc
                },
                Parsed = result.Response,
                Contexts = result.Contexts,
                Choices = new List<OpenAiStyleChoice>
                {
                    new()
                    {
                        Index = 0,
                        Message = new OpenAiStyleMessage
                        {
                            Role = "assistant",
                            Content = result.Response.Answer ?? result.AssistantEnvelope.Content
                        }
                    }
                }
            };
        }

        public Task<IReadOnlyList<ChatConversation>> ListConversationsAsync(CancellationToken cancellationToken = default) =>
            _store.ListConversationsAsync(cancellationToken);

        public Task<ChatConversation?> GetConversationAsync(string conversationId, CancellationToken cancellationToken = default) =>
            _store.GetConversationAsync(conversationId, cancellationToken);

        private static void ValidateRequest(ReliableChatSendRequest request)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.UserMessage)) throw new ArgumentException("UserMessage vacío.");
        }

        private static ReliableChatEnvelope CreateEnvelope(
            ChatConversation conversation,
            string role,
            string sender,
            string recipient,
            string content,
            string? idempotencyKey,
            string? parentMessageId)
        {
            int seq = conversation.NextSequenceNumber++;
            return new ReliableChatEnvelope
            {
                MessageId = $"msg-{Guid.NewGuid():N}",
                ConversationId = conversation.ConversationId,
                Role = role,
                Sender = sender,
                Recipient = recipient,
                Content = content,
                ParentMessageId = parentMessageId,
                CorrelationId = Guid.NewGuid().ToString("N"),
                IdempotencyKey = idempotencyKey,
                SequenceNumber = seq,
                ChecksumSha256 = ComputeChecksum(content)
            };
        }

        private static string ComputeChecksum(string content)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content ?? string.Empty));
            return Convert.ToHexString(bytes);
        }

        private static LmStudioOptions NormalizeLmStudioOptions(LmStudioOptions options, string? model = null, double? temperature = null, int? maxTokens = null)
        {
            return new LmStudioOptions
            {
                BaseUrl = options.BaseUrl,
                ApiKey = options.ApiKey,
                ChatModel = string.IsNullOrWhiteSpace(model) ? options.ChatModel : model,
                EmbeddingModel = options.EmbeddingModel,
                VisionModel = options.VisionModel,
                Temperature = temperature ?? options.Temperature,
                MaxTokens = maxTokens ?? options.MaxTokens,
                TimeoutSeconds = options.TimeoutSeconds,
                SemanticRagIndexPath = options.SemanticRagIndexPath
            };
        }

        private static IEnumerable<LmStudioChatMessage> BuildPromptMessages(ChatConversation conversation, string currentUserMessage, List<object> contexts)
        {
            var messages = new List<LmStudioChatMessage>
            {
                new()
                {
                    Role = "system",
                    Content = BuildSystemPrompt(contexts)
                }
            };

            foreach (var message in conversation.Messages.OrderBy(x => x.SequenceNumber).TakeLast(12))
            {
                if (string.IsNullOrWhiteSpace(message.Content))
                    continue;

                messages.Add(new LmStudioChatMessage
                {
                    Role = message.Role,
                    Content = message.Content
                });
            }

            if (!string.Equals(messages.LastOrDefault()?.Content, currentUserMessage, StringComparison.Ordinal))
            {
                messages.Add(new LmStudioChatMessage
                {
                    Role = "user",
                    Content = currentUserMessage
                });
            }

            return messages;
        }

        private static string BuildSystemPrompt(List<object> contexts)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Eres un asistente conversacional de SUCA.");
            sb.AppendLine("Responde en JSON con question, answer, confidence y format.");
            sb.AppendLine("Mantén continuidad de conversación.");

            if (contexts.Count > 0)
            {
                sb.AppendLine("Contexto recuperado para RAG:");
                foreach (var context in contexts)
                    sb.AppendLine(JsonSerializer.Serialize(context, JsonDefaults.Default));
            }

            return sb.ToString();
        }

        private static async Task<List<object>> ResolveContextsAsync(LmStudioClient client, ChatRagOptions? rag, string query, CancellationToken cancellationToken)
        {
            var contexts = new List<object>();
            if (rag is null || string.IsNullOrWhiteSpace(rag.SourceDirectory) || string.IsNullOrWhiteSpace(rag.SemanticIndexPath))
                return contexts;

            if (rag.RefreshIndexBeforeQuery || !File.Exists(rag.SemanticIndexPath))
            {
                await LmStudioSucaBridge.IndexDirectoryAsync(
                    client,
                    rag.SourceDirectory,
                    rag.SemanticIndexPath,
                    rag.Recursive,
                    rag.ChunkSize,
                    rag.ChunkOverlap,
                    rag.Extensions,
                    cancellationToken).ConfigureAwait(false);
            }

            float[] queryEmbedding = await client.CreateEmbeddingAsync(query, cancellationToken).ConfigureAwait(false);
            var index = LmStudioSemanticRagIndex.Load(rag.SemanticIndexPath);
            foreach (var hit in index.Search(queryEmbedding, rag.TopK))
            {
                contexts.Add(new
                {
                    score = hit.Score,
                    sourcePath = hit.Item.SourcePath,
                    text = hit.Item.Text,
                    metadata = hit.Item.Metadata
                });
            }

            return contexts;
        }
    }
}
