import { el, escapeHtml, toast, fetchJson } from "../main.js";

const chatState = {
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

let initialized = false;

export function initChatView() {
  if (initialized) return;
  initialized = true;

  el("composer-form").addEventListener("submit", (event) => void onSendMessage(event));
  el("new-chat-button").addEventListener("click", resetConversation);
  el("refresh-conversations-button").addEventListener("click", () => void loadConversations());

  [
    "base-url", "chat-model", "embedding-model", "temperature", "max-tokens",
    "source-directory", "semantic-index-path", "top-k", "chunk-size", "chunk-overlap",
    "max-delivery-attempts", "rag-enabled", "recursive", "refresh-index"
  ].forEach((id) => {
    el(id).addEventListener("change", saveLocalSettings);
    el(id).addEventListener("input", saveLocalSettings);
  });

  void initialize();
}

async function initialize() {
  setComposerStatus("Cargando valores por defecto...");

  try {
    const serverDefaults = await fetchJson("/api/minichat/defaults");
    if (serverDefaults.lmStudio) defaults.lmStudio = serverDefaults.lmStudio;
    if (serverDefaults.rag) defaults.rag = serverDefaults.rag;
  } catch { /* usa valores locales */ }

  applyLocalSettings(loadLocalSettings());
  await Promise.all([loadModels(), loadConversations()]);
  setComposerStatus("Listo");
}

async function loadModels() {
  try {
    const models = await fetchJson("/api/lmstudio/models");
    fillModelList("chat-model-list", models.filter((item) => !String(item.id || "").toLowerCase().includes("embedding")));
    fillModelList("embedding-model-list", models.filter((item) => String(item.id || "").toLowerCase().includes("embedding")));

    if (!el("chat-model").value && models.length > 0) {
      const preferred = models.find((item) => String(item.id).includes("deepseek")) || models[0];
      el("chat-model").value = preferred.id;
    }

    const embeddingPreferred = models.find((item) => String(item.id || "").toLowerCase().includes("embedding"));
    if (!el("embedding-model").value && embeddingPreferred) {
      el("embedding-model").value = embeddingPreferred.id;
    }

    saveLocalSettings();
  } catch (error) {
    setResponseSummary(`No se pudieron leer los modelos de LM Studio.\n${error.message}`);
  }
}

async function loadConversations() {
  try {
    chatState.conversations = await fetchJson("/api/minichat/conversations");
    renderConversationList();
  } catch (error) {
    setResponseSummary(`No se pudieron cargar las conversaciones.\n${error.message}`);
  }
}

async function loadConversation(conversationId) {
  try {
    const conversation = await fetchJson(`/api/minichat/conversations/${encodeURIComponent(conversationId)}`);
    chatState.conversationId = conversation.conversationId;
    renderConversation(conversation);
    renderConversationList();
  } catch (error) {
    setResponseSummary(`No se pudo cargar la conversacion ${conversationId}.\n${error.message}`);
  }
}

async function onSendMessage(event) {
  event.preventDefault();
  if (chatState.busy) return;

  const input = el("message-input");
  const content = input.value.trim();
  if (!content) {
    setComposerStatus("Escribe un mensaje primero");
    return;
  }

  chatState.busy = true;
  setBusy(true);
  setComposerStatus("Enviando...");
  appendOptimisticMessage("user", content);
  input.value = "";

  try {
    const result = await fetchJson("/api/minichat/messages/send", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(buildRequest(content))
    });

    chatState.conversationId = result.conversationId;
    await loadConversations();
    await loadConversation(result.conversationId);

    const delivery = result.assistant?.deliveryStatus || result.assistant?.delivery?.status || "Acknowledged";
    setDeliveryState(delivery);
    setResponseSummary(formatResponseSummary(result));
    setComposerStatus("Entregado");
  } catch (error) {
    setDeliveryState("Fallo");
    setComposerStatus("La solicitud fallo");
    setResponseSummary(error.message);
    await loadConversations();
  } finally {
    chatState.busy = false;
    setBusy(false);
    input.focus();
  }
}

function buildRequest(userMessage) {
  const ragEnabled = el("rag-enabled").checked;
  const request = {
    conversationId: chatState.conversationId,
    userMessage,
    title: chatState.conversationId ? null : "Nueva conversacion",
    idempotencyKey: self.crypto?.randomUUID ? self.crypto.randomUUID() : `msg-${Date.now()}`,
    lmStudio: {
      baseUrl: el("base-url").value.trim(),
      chatModel: el("chat-model").value.trim(),
      embeddingModel: el("embedding-model").value.trim(),
      temperature: Number(el("temperature").value || 0),
      maxTokens: Number(el("max-tokens").value || 700)
    },
    rag: null,
    maxDeliveryAttempts: Number(el("max-delivery-attempts").value || 3)
  };

  if (ragEnabled) {
    request.rag = {
      sourceDirectory: el("source-directory").value.trim(),
      semanticIndexPath: el("semantic-index-path").value.trim(),
      refreshIndexBeforeQuery: el("refresh-index").checked,
      recursive: el("recursive").checked,
      topK: Number(el("top-k").value || 4),
      chunkSize: Number(el("chunk-size").value || 1200),
      chunkOverlap: Number(el("chunk-overlap").value || 180)
    };
  }

  return request;
}

function renderConversationList() {
  const container = el("conversation-list");
  container.innerHTML = "";

  if (!chatState.conversations.length) {
    container.innerHTML = `<div class="empty-state">Sin conversaciones todavia.</div>`;
    return;
  }

  for (const conversation of chatState.conversations) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = `conversation-item${conversation.conversationId === chatState.conversationId ? " active" : ""}`;
    button.innerHTML = `
      <strong>${escapeHtml(conversation.title || "Nueva conversacion")}</strong>
      <span>${escapeHtml(String(conversation.messages?.length || 0))} mensajes</span>`;
    button.addEventListener("click", () => void loadConversation(conversation.conversationId));
    container.appendChild(button);
  }
}

function renderConversation(conversation) {
  el("conversation-title").textContent = conversation.title || "Nueva conversacion";

  const messageList = el("message-list");
  messageList.innerHTML = "";

  const messages = Array.isArray(conversation.messages) ? conversation.messages : [];
  if (!messages.length) {
    messageList.innerHTML = `<div class="empty-state">Envia tu primer mensaje.</div>`;
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
  const messageList = el("message-list");
  if (messageList.querySelector(".empty-state")) messageList.innerHTML = "";

  messageList.appendChild(buildMessageNode(role, content, { createdAtUtc: new Date().toISOString() }));
  messageList.scrollTop = messageList.scrollHeight;
}

function buildMessageNode(role, content, envelope) {
  const article = document.createElement("article");
  article.className = `message ${role === "assistant" ? "assistant" : "user"}`;

  const meta = document.createElement("div");
  meta.className = "message-meta";
  const when = envelope?.createdAtUtc ? new Date(envelope.createdAtUtc).toLocaleString() : new Date().toLocaleString();
  const status = envelope?.deliveryStatus ? ` | ${envelope.deliveryStatus}` : "";
  meta.textContent = `${role}${status} | ${when}`;

  const body = document.createElement("div");
  body.textContent = content || "(vacio)";

  article.appendChild(meta);
  article.appendChild(body);
  return article;
}

function formatAssistantEnvelope(envelope) {
  const parsed = envelope.parsedResponse || null;
  if (parsed) return formatParsed(parsed);

  return JSON.stringify({
    messageId: envelope.messageId,
    deliveryStatus: envelope.deliveryStatus,
    retryCount: envelope.retryCount,
    content: envelope.content
  }, null, 2);
}

function formatResponseSummary(result) {
  const response = result.response || {};
  const summary = [
    formatParsed(response),
    "",
    `entrega: ${result.assistant?.deliveryStatus || "Acknowledged"}`,
    `contextos: ${Array.isArray(result.contexts) ? result.contexts.length : 0}`
  ];

  if (Array.isArray(result.contexts) && result.contexts.length > 0) {
    summary.push("", "contextos recuperados:");
    result.contexts.slice(0, 3).forEach((item, index) => {
      const sourcePath = item.sourcePath || item.item?.sourcePath || "desconocido";
      summary.push(`${index + 1}. ${sourcePath}`);
    });
  }

  return summary.join("\n");
}

function formatParsed(parsed) {
  if (!parsed) return "Sin respuesta estructurada.";

  return [
    `estado: ${parsed.status || "Unknown"}`,
    `formato: ${parsed.format || "text"}`,
    `pregunta: ${parsed.question || "-"}`,
    `respuesta: ${parsed.answer || "-"}`,
    `confianza: ${parsed.confidence ?? "-"}`,
    parsed.warnings?.length ? `avisos: ${parsed.warnings.join(" | ")}` : null
  ].filter(Boolean).join("\n");
}

function applyLocalSettings(settings) {
  const merged = {
    lmStudio: { ...defaults.lmStudio, ...(settings.lmStudio || {}) },
    rag: { ...defaults.rag, ...(settings.rag || {}) },
    maxDeliveryAttempts: settings.maxDeliveryAttempts || 3
  };

  el("base-url").value = merged.lmStudio.baseUrl || defaults.lmStudio.baseUrl;
  el("chat-model").value = merged.lmStudio.chatModel || defaults.lmStudio.chatModel;
  el("embedding-model").value = merged.lmStudio.embeddingModel || defaults.lmStudio.embeddingModel;
  el("temperature").value = String(merged.lmStudio.temperature ?? defaults.lmStudio.temperature);
  el("max-tokens").value = String(merged.lmStudio.maxTokens ?? defaults.lmStudio.maxTokens);
  el("source-directory").value = merged.rag.sourceDirectory || defaults.rag.sourceDirectory;
  el("semantic-index-path").value = merged.rag.semanticIndexPath || defaults.rag.semanticIndexPath;
  el("top-k").value = String(merged.rag.topK ?? defaults.rag.topK);
  el("chunk-size").value = String(merged.rag.chunkSize ?? defaults.rag.chunkSize);
  el("chunk-overlap").value = String(merged.rag.chunkOverlap ?? defaults.rag.chunkOverlap);
  el("max-delivery-attempts").value = String(merged.maxDeliveryAttempts);
  el("rag-enabled").checked = settings.ragEnabled ?? true;
  el("recursive").checked = merged.rag.recursive ?? true;
  el("refresh-index").checked = merged.rag.refreshIndexBeforeQuery ?? true;
}

function saveLocalSettings() {
  const payload = {
    lmStudio: {
      baseUrl: el("base-url").value.trim(),
      chatModel: el("chat-model").value.trim(),
      embeddingModel: el("embedding-model").value.trim(),
      temperature: Number(el("temperature").value || 0),
      maxTokens: Number(el("max-tokens").value || 700)
    },
    ragEnabled: el("rag-enabled").checked,
    rag: {
      sourceDirectory: el("source-directory").value.trim(),
      semanticIndexPath: el("semantic-index-path").value.trim(),
      topK: Number(el("top-k").value || 4),
      chunkSize: Number(el("chunk-size").value || 1200),
      chunkOverlap: Number(el("chunk-overlap").value || 180),
      recursive: el("recursive").checked,
      refreshIndexBeforeQuery: el("refresh-index").checked
    },
    maxDeliveryAttempts: Number(el("max-delivery-attempts").value || 3)
  };

  localStorage.setItem("suca-minichat-settings", JSON.stringify(payload));
}

function loadLocalSettings() {
  const fallback = { lmStudio: defaults.lmStudio, ragEnabled: true, rag: defaults.rag, maxDeliveryAttempts: 3 };
  const raw = localStorage.getItem("suca-minichat-settings");
  if (!raw) return fallback;

  try {
    return JSON.parse(raw);
  } catch {
    return fallback;
  }
}

function resetConversation() {
  chatState.conversationId = null;
  el("conversation-title").textContent = "Nueva conversacion";
  el("message-list").innerHTML = `<div class="empty-state">Envia tu primer mensaje.</div>`;
  setDeliveryState("Idle");
  setResponseSummary("Lista.");
  renderConversationList();
}

function fillModelList(id, items) {
  const dataList = el(id);
  dataList.innerHTML = "";
  for (const item of items) {
    const option = document.createElement("option");
    option.value = item.id;
    dataList.appendChild(option);
  }
}

function setBusy(busy) {
  el("send-button").disabled = busy;
}

function setDeliveryState(value) {
  const node = el("delivery-state");
  node.textContent = value;
  node.setAttribute("data-state", String(value).toLowerCase());
}

function setComposerStatus(value) {
  el("composer-status").textContent = value;
}

function setResponseSummary(value) {
  el("response-summary").textContent = value;
}
