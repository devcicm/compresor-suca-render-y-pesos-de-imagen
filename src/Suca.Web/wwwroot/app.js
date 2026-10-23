const state = {
  conversationId: null,
  conversations: [],
  busy: false
};

const defaults = {
  lmStudio: {
    baseUrl: "http://127.0.0.1:1234/v1",
    chatModel: "",
    embeddingModel: "",
    temperature: 0,
    maxTokens: 700
  },
  rag: {
    sourceDirectory: "",
    semanticIndexPath: "",
    refreshIndexBeforeQuery: true,
    recursive: true,
    topK: 4,
    chunkSize: 1200,
    chunkOverlap: 180
  }
};

document.addEventListener("DOMContentLoaded", () => {
  bindEvents();
  void initialize();
});

async function initialize() {
  setComposerStatus("Loading defaults...");

  await Promise.all([
    loadDefaults(),
    loadModels(),
    loadConversations()
  ]);

  setComposerStatus("Ready");
}

function bindEvents() {
  element("composer-form").addEventListener("submit", onSendMessage);
  element("new-chat-button").addEventListener("click", resetConversation);
  element("refresh-conversations-button").addEventListener("click", () => void loadConversations());
  element("reload-models-button").addEventListener("click", () => void loadModels());

  [
    "base-url",
    "chat-model",
    "embedding-model",
    "temperature",
    "max-tokens",
    "source-directory",
    "semantic-index-path",
    "top-k",
    "chunk-size",
    "chunk-overlap",
    "max-delivery-attempts",
    "rag-enabled",
    "recursive",
    "refresh-index"
  ].forEach((id) => {
    element(id).addEventListener("change", saveSettings);
    element(id).addEventListener("input", saveSettings);
  });
}

async function loadDefaults() {
  try {
    const response = await fetchJson("/api/minichat/defaults");
    defaults.lmStudio = response.lmStudio || defaults.lmStudio;
    defaults.rag = response.rag || defaults.rag;
  } catch (error) {
    console.error(error);
  }

  applySettings(loadSettings());
}

async function loadModels() {
  try {
    const models = await fetchJson("/api/lmstudio/models");
    fillModelList(
      "chat-model-list",
      models.filter((item) => !String(item.id || "").toLowerCase().includes("embedding"))
    );
    fillModelList(
      "embedding-model-list",
      models.filter((item) => String(item.id || "").toLowerCase().includes("embedding"))
    );

    if (!element("chat-model").value && models.length > 0) {
      const preferred = models.find((item) => String(item.id).includes("deepseek")) || models[0];
      element("chat-model").value = preferred.id;
    }

    const embeddingPreferred = models.find((item) => String(item.id || "").toLowerCase().includes("embedding"));
    if (!element("embedding-model").value && embeddingPreferred) {
      element("embedding-model").value = embeddingPreferred.id;
    }

    saveSettings();
  } catch (error) {
    setResponseSummary(`Could not read LM Studio models.\n${toErrorMessage(error)}`);
  }
}

async function loadConversations() {
  try {
    state.conversations = await fetchJson("/api/minichat/conversations");
    renderConversationList();
  } catch (error) {
    setResponseSummary(`Could not load conversations.\n${toErrorMessage(error)}`);
  }
}

async function loadConversation(conversationId) {
  try {
    const conversation = await fetchJson(`/api/minichat/conversations/${encodeURIComponent(conversationId)}`);
    state.conversationId = conversation.conversationId;
    renderConversation(conversation);
    renderConversationList();
  } catch (error) {
    setResponseSummary(`Could not load conversation ${conversationId}.\n${toErrorMessage(error)}`);
  }
}

async function onSendMessage(event) {
  event.preventDefault();
  if (state.busy) {
    return;
  }

  const input = element("message-input");
  const content = input.value.trim();
  if (!content) {
    setComposerStatus("Write a message first");
    return;
  }

  state.busy = true;
  setBusy(true);
  setComposerStatus("Sending...");
  appendOptimisticMessage("user", content);
  input.value = "";

  try {
    const payload = buildRequest(content);
    const result = await postJson("/api/minichat/messages/send", payload);

    state.conversationId = result.conversationId;
    await loadConversations();
    await loadConversation(result.conversationId);

    const delivery = result.assistant?.deliveryStatus || result.assistant?.delivery?.status || "Acknowledged";
    setDeliveryState(delivery);
    setResponseSummary(formatResponseSummary(result));
    setComposerStatus("Delivered");
  } catch (error) {
    setDeliveryState("Failed");
    setComposerStatus("Request failed");
    setResponseSummary(toErrorMessage(error));
    await loadConversations();
  } finally {
    state.busy = false;
    setBusy(false);
    input.focus();
  }
}

function buildRequest(userMessage) {
  const ragEnabled = element("rag-enabled").checked;
  const request = {
    conversationId: state.conversationId,
    userMessage,
    title: state.conversationId ? null : "New Chat",
    idempotencyKey: self.crypto?.randomUUID ? self.crypto.randomUUID() : `msg-${Date.now()}`,
    lmStudio: {
      baseUrl: element("base-url").value.trim(),
      chatModel: element("chat-model").value.trim(),
      embeddingModel: element("embedding-model").value.trim(),
      temperature: Number(element("temperature").value || 0),
      maxTokens: Number(element("max-tokens").value || 700)
    },
    rag: null,
    maxDeliveryAttempts: Number(element("max-delivery-attempts").value || 3)
  };

  if (ragEnabled) {
    request.rag = {
      sourceDirectory: element("source-directory").value.trim(),
      semanticIndexPath: element("semantic-index-path").value.trim(),
      refreshIndexBeforeQuery: element("refresh-index").checked,
      recursive: element("recursive").checked,
      topK: Number(element("top-k").value || 4),
      chunkSize: Number(element("chunk-size").value || 1200),
      chunkOverlap: Number(element("chunk-overlap").value || 180)
    };
  }

  return request;
}

function renderConversationList() {
  const container = element("conversation-list");
  container.innerHTML = "";

  if (!state.conversations.length) {
    container.innerHTML = `<div class="empty-state">No conversations yet.</div>`;
    return;
  }

  for (const conversation of state.conversations) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = `conversation-item${conversation.conversationId === state.conversationId ? " active" : ""}`;
    button.innerHTML = `
      <strong>${escapeHtml(conversation.title || "New Chat")}</strong>
      <span>${escapeHtml(formatConversationMeta(conversation))}</span>
    `;
    button.addEventListener("click", () => void loadConversation(conversation.conversationId));
    container.appendChild(button);
  }
}

function renderConversation(conversation) {
  element("conversation-title").textContent = conversation.title || "New Chat";
  element("conversation-meta").textContent = `${conversation.messages?.length || 0} messages`;

  const messageList = element("message-list");
  messageList.innerHTML = "";

  const messages = Array.isArray(conversation.messages) ? conversation.messages : [];
  if (!messages.length) {
    messageList.innerHTML = `<div class="empty-state">Send your first message.</div>`;
    return;
  }

  for (const message of messages) {
    messageList.appendChild(buildMessageNode(message.role, message.content, message));
  }

  messageList.scrollTop = messageList.scrollHeight;

  const lastAssistant = [...messages].reverse().find((item) => item.role === "assistant");
  if (lastAssistant) {
    setDeliveryState(lastAssistant.deliveryStatus || "Acknowledged");
    setResponseSummary(formatAssistantEnvelope(lastAssistant));
  }
}

function appendOptimisticMessage(role, content) {
  const messageList = element("message-list");
  if (messageList.querySelector(".empty-state")) {
    messageList.innerHTML = "";
  }

  messageList.appendChild(buildMessageNode(role, content, { createdAtUtc: new Date().toISOString() }));
  messageList.scrollTop = messageList.scrollHeight;
}

function buildMessageNode(role, content, envelope) {
  const article = document.createElement("article");
  article.className = `message ${role === "assistant" ? "assistant" : "user"}`;

  const meta = document.createElement("div");
  meta.className = "message-meta";
  meta.textContent = buildEnvelopeMeta(role, envelope);

  const body = document.createElement("div");
  body.textContent = content || "(empty)";

  article.appendChild(meta);
  article.appendChild(body);
  return article;
}

function buildEnvelopeMeta(role, envelope) {
  const when = envelope?.createdAtUtc ? new Date(envelope.createdAtUtc).toLocaleString() : new Date().toLocaleString();
  const status = envelope?.deliveryStatus ? ` | ${envelope.deliveryStatus}` : "";
  return `${role}${status} | ${when}`;
}

function formatAssistantEnvelope(envelope) {
  const parsed = envelope.parsedResponse || null;
  if (parsed) {
    return formatParsed(parsed);
  }

  const payload = {
    messageId: envelope.messageId,
    deliveryStatus: envelope.deliveryStatus,
    retryCount: envelope.retryCount,
    content: envelope.content
  };
  return JSON.stringify(payload, null, 2);
}

function formatResponseSummary(result) {
  const response = result.response || {};
  const summary = [
    formatParsed(response),
    "",
    `delivery: ${result.assistant?.deliveryStatus || "Acknowledged"}`,
    `contexts: ${Array.isArray(result.contexts) ? result.contexts.length : 0}`
  ];

  if (Array.isArray(result.contexts) && result.contexts.length > 0) {
    summary.push("");
    summary.push("retrieved contexts:");
    result.contexts.slice(0, 3).forEach((item, index) => {
      const sourcePath = item.sourcePath || item.item?.sourcePath || "unknown";
      summary.push(`${index + 1}. ${sourcePath}`);
    });
  }

  return summary.join("\n");
}

function formatParsed(parsed) {
  if (!parsed) {
    return "No structured response.";
  }

  return [
    `status: ${parsed.status || "Unknown"}`,
    `format: ${parsed.format || "text"}`,
    `question: ${parsed.question || "-"}`,
    `answer: ${parsed.answer || "-"}`,
    `confidence: ${parsed.confidence ?? "-"}`,
    parsed.warnings?.length ? `warnings: ${parsed.warnings.join(" | ")}` : null
  ].filter(Boolean).join("\n");
}

function applySettings(settings) {
  const merged = {
    lmStudio: { ...defaults.lmStudio, ...(settings.lmStudio || {}) },
    rag: { ...defaults.rag, ...(settings.rag || {}) },
    maxDeliveryAttempts: settings.maxDeliveryAttempts || 3
  };

  element("base-url").value = merged.lmStudio.baseUrl || defaults.lmStudio.baseUrl;
  element("chat-model").value = merged.lmStudio.chatModel || defaults.lmStudio.chatModel;
  element("embedding-model").value = merged.lmStudio.embeddingModel || defaults.lmStudio.embeddingModel;
  element("temperature").value = String(merged.lmStudio.temperature ?? defaults.lmStudio.temperature);
  element("max-tokens").value = String(merged.lmStudio.maxTokens ?? defaults.lmStudio.maxTokens);
  element("source-directory").value = merged.rag.sourceDirectory || defaults.rag.sourceDirectory;
  element("semantic-index-path").value = merged.rag.semanticIndexPath || defaults.rag.semanticIndexPath;
  element("top-k").value = String(merged.rag.topK ?? defaults.rag.topK);
  element("chunk-size").value = String(merged.rag.chunkSize ?? defaults.rag.chunkSize);
  element("chunk-overlap").value = String(merged.rag.chunkOverlap ?? defaults.rag.chunkOverlap);
  element("max-delivery-attempts").value = String(merged.maxDeliveryAttempts);
  element("rag-enabled").checked = settings.ragEnabled ?? true;
  element("recursive").checked = merged.rag.recursive ?? true;
  element("refresh-index").checked = merged.rag.refreshIndexBeforeQuery ?? true;
}

function saveSettings() {
  const payload = {
    lmStudio: {
      baseUrl: element("base-url").value.trim(),
      chatModel: element("chat-model").value.trim(),
      embeddingModel: element("embedding-model").value.trim(),
      temperature: Number(element("temperature").value || 0),
      maxTokens: Number(element("max-tokens").value || 700)
    },
    ragEnabled: element("rag-enabled").checked,
    rag: {
      sourceDirectory: element("source-directory").value.trim(),
      semanticIndexPath: element("semantic-index-path").value.trim(),
      topK: Number(element("top-k").value || 4),
      chunkSize: Number(element("chunk-size").value || 1200),
      chunkOverlap: Number(element("chunk-overlap").value || 180),
      recursive: element("recursive").checked,
      refreshIndexBeforeQuery: element("refresh-index").checked
    },
    maxDeliveryAttempts: Number(element("max-delivery-attempts").value || 3)
  };

  localStorage.setItem("suca-minichat-settings", JSON.stringify(payload));
}

function loadSettings() {
  const raw = localStorage.getItem("suca-minichat-settings");
  if (!raw) {
    return {
      lmStudio: defaults.lmStudio,
      ragEnabled: true,
      rag: defaults.rag,
      maxDeliveryAttempts: 3
    };
  }

  try {
    return JSON.parse(raw);
  } catch (error) {
    console.error(error);
    return {
      lmStudio: defaults.lmStudio,
      ragEnabled: true,
      rag: defaults.rag,
      maxDeliveryAttempts: 3
    };
  }
}

function resetConversation() {
  state.conversationId = null;
  element("conversation-title").textContent = "New Chat";
  element("conversation-meta").textContent = "No active conversation";
  element("message-list").innerHTML = `<div class="empty-state">Send your first message.</div>`;
  setDeliveryState("Idle");
  setResponseSummary("Ready.");
  renderConversationList();
}

function fillModelList(id, items) {
  const dataList = element(id);
  dataList.innerHTML = "";
  for (const item of items) {
    const option = document.createElement("option");
    option.value = item.id;
    dataList.appendChild(option);
  }
}

async function fetchJson(url) {
  const response = await fetch(url, {
    headers: { "Accept": "application/json" }
  });
  return await parseResponse(response);
}

async function postJson(url, payload) {
  const response = await fetch(url, {
    method: "POST",
    headers: {
      "Accept": "application/json",
      "Content-Type": "application/json"
    },
    body: JSON.stringify(payload)
  });
  return await parseResponse(response);
}

async function parseResponse(response) {
  const text = await response.text();
  const payload = text ? JSON.parse(text) : null;
  if (!response.ok) {
    throw new Error(payload?.error || payload?.detail || text || `HTTP ${response.status}`);
  }

  return payload;
}

function setBusy(isBusy) {
  element("send-button").disabled = isBusy;
  element("reload-models-button").disabled = isBusy;
  element("refresh-conversations-button").disabled = isBusy;
}

function setComposerStatus(value) {
  element("composer-status").textContent = value;
}

function setDeliveryState(value) {
  element("delivery-state").textContent = value;
}

function setResponseSummary(value) {
  element("response-summary").textContent = value;
}

function formatConversationMeta(conversation) {
  const updated = conversation.lastUpdatedAtUtc ? new Date(conversation.lastUpdatedAtUtc).toLocaleString() : "unknown";
  const count = conversation.messages?.length || 0;
  return `${count} msg | ${updated}`;
}

function element(id) {
  return document.getElementById(id);
}

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll("\"", "&quot;")
    .replaceAll("'", "&#39;");
}

function toErrorMessage(error) {
  if (error instanceof Error) {
    return error.message;
  }

  return String(error || "Unknown error");
}
