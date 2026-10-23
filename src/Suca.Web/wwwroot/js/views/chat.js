import { el, escapeHtml, toast, fetchJson, copyTextToClipboard } from "../main.js";
import { renderMarkdown } from "../vendor/markdown.js";
import { confirmDialog, promptDialog } from "../ui/dialog.js";

const chatState = {
  conversationId: null,
  conversations: [],
  busy: false,
  streaming: false,
  streamAbort: null
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

  // Estado de LM Studio, busqueda y gestion de conversaciones.
  el("lmstudio-retry").addEventListener("click", () => void checkLmStudio());
  el("conversation-search").addEventListener("input", debounce(() => void loadConversations(), 250));
  el("rename-conversation-button").addEventListener("click", () => void renameCurrentConversation());
  el("delete-conversation-button").addEventListener("click", () => void deleteCurrentConversation());
  el("stop-button").addEventListener("click", stopStreaming);

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
  await Promise.all([loadModels(), loadConversations(), checkLmStudio()]);
  setComposerStatus("Listo");
}

/* ============================== Estado de LM Studio ============================== */

async function checkLmStudio() {
  const banner = el("lmstudio-banner");
  banner.hidden = false;
  banner.setAttribute("data-state", "checking");
  el("lmstudio-banner-text").textContent = "Comprobando LM Studio...";

  try {
    const health = await fetchJson("/api/lmstudio/health");
    if (health.status === "available") {
      banner.setAttribute("data-state", "ok");
      el("lmstudio-banner-text").textContent = `LM Studio disponible en ${health.baseUrl}`;
      window.setTimeout(() => {
        banner.hidden = true;
      }, 2500);
      return true;
    }

    banner.setAttribute("data-state", "down");
    el("lmstudio-banner-text").textContent =
      `LM Studio no responde en ${health.baseUrl}${health.detail ? ` (${health.detail})` : ""}. Arrancala para usar el chat.`;
    return false;
  } catch (error) {
    banner.setAttribute("data-state", "down");
    el("lmstudio-banner-text").textContent = `No se pudo comprobar LM Studio: ${error.message}`;
    return false;
  }
}

function debounce(fn, ms) {
  let timer = 0;
  return (...args) => {
    window.clearTimeout(timer);
    timer = window.setTimeout(() => fn(...args), ms);
  };
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
    // El banner ya explica la causa; aqui solo dejamos constancia en el resumen.
    setResponseSummary(`No se pudieron leer los modelos de LM Studio.\n${error.message}`);
  }
}

async function loadConversations() {
  try {
    const search = el("conversation-search").value.trim();
    const url = search
      ? `/api/minichat/conversations?search=${encodeURIComponent(search)}`
      : "/api/minichat/conversations";
    chatState.conversations = await fetchJson(url);
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
    await streamCompletion(content);
  } catch (error) {
    if (error.name === "AbortError") {
      setComposerStatus("Generacion detenida");
      setDeliveryState("Cancelada");
    } else {
      setDeliveryState("Fallo");
      setComposerStatus("La solicitud fallo");
      setResponseSummary(error.message);
      toast(`Chat: ${error.message}`, "error");
    }
  } finally {
    chatState.busy = false;
    chatState.streaming = false;
    chatState.streamAbort = null;
    setBusy(false);
    input.focus();
    await loadConversations();
  }
}

/* Envia el mensaje y consume el stream SSE: los fragmentos se pintan al vuelo. */
async function streamCompletion(content) {
  const controller = new AbortController();
  chatState.streamAbort = controller;
  chatState.streaming = true;
  el("stop-button").hidden = false;
  setComposerStatus("Generando...");

  const assistantNode = appendStreamingMessage();
  let accumulated = "";
  let streamFailed = false;

  try {
    const response = await fetch("/api/minichat/chat/stream", {
      method: "POST",
      headers: { "Content-Type": "application/json", Accept: "text/event-stream" },
      body: JSON.stringify(buildChatCompletionRequest(content)),
      signal: controller.signal
    });

    if (!response.ok || !response.body) {
      const body = await response.json().catch(() => null);
      throw new Error(body?.error || `HTTP ${response.status}`);
    }

    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let buffer = "";

    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;

      buffer += decoder.decode(value, { stream: true });
      const lines = buffer.split("\n");
      buffer = lines.pop() ?? "";

      for (const line of lines) {
        const trimmed = line.trim();
        if (!trimmed.startsWith("data:")) continue;

        const payload = trimmed.slice(5).trim();
        if (payload === "[DONE]") {
          finalizeStreamingMessage(assistantNode, accumulated, streamFailed ? "Incompleta" : "Entregado");
          setComposerStatus(streamFailed ? "La peticion fallo" : "Entregado");
          return;
        }

        let event;
        try {
          event = JSON.parse(payload);
        } catch {
          continue;
        }

        if (event.type === 4) streamFailed = true;
        handleStreamEvent(event, assistantNode, (text) => {
          accumulated += text;
          paintStreamingMessage(assistantNode, accumulated);
        });
      }
    }

    finalizeStreamingMessage(assistantNode, accumulated, streamFailed ? "Incompleta" : "Entregado");
    setComposerStatus(streamFailed ? "La peticion fallo" : "Entregado");
  } catch (error) {
    finalizeStreamingMessage(assistantNode, accumulated, "Incompleta");
    throw error;
  }
}

function handleStreamEvent(event, node, appendDelta) {
  switch (event.type) {
    case 0: // Started
      chatState.conversationId = event.conversationId || chatState.conversationId;
      setDeliveryState("Entregando");
      break;
    case 1: // Context (cita RAG)
      addCitation(node, event.context);
      break;
    case 2: // Delta
      appendDelta(event.text || "");
      break;
    case 3: // Completed
      setDeliveryState("Entregado");
      setResponseSummary(formatCompletedSummary(event));
      if (event.conversationId) chatState.conversationId = event.conversationId;
      break;
    case 4: // Error
      setDeliveryState("Fallo");
      setComposerStatus("La peticion fallo");
      toast(`Chat: ${event.text || "error desconocido"}`, "error");
      break;
    default:
      break;
  }
}

function formatCompletedSummary(event) {
  return [
    `mensaje: ${event.messageId || "-"}`,
    `conversacion: ${event.conversationId || "-"}`,
    `caracteres: ${(event.text || "").length}`
  ].join("\n");
}

function stopStreaming() {
  if (!chatState.streamAbort) return;
  chatState.streamAbort.abort();
  chatState.streamAbort = null;
  el("stop-button").hidden = true;
  setComposerStatus("Deteniendo...");
}

function buildChatCompletionRequest(userMessage) {
  const request = buildRequest(userMessage);
  return {
    conversationId: request.conversationId,
    idempotencyKey: request.idempotencyKey,
    model: request.lmStudio.chatModel,
    temperature: request.lmStudio.temperature,
    maxTokens: request.lmStudio.maxTokens,
    lmStudio: request.lmStudio,
    rag: request.rag,
    messages: [{ role: "user", content: userMessage }]
  };
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

  const search = el("conversation-search").value.trim();
  const hasConversations = chatState.conversations.length > 0;

  el("rename-conversation-button").hidden = !chatState.conversationId;
  el("delete-conversation-button").hidden = !chatState.conversationId;

  if (!hasConversations) {
    container.innerHTML = `<div class="empty-state">${search ? "Ninguna conversacion coincide con la busqueda." : "Sin conversaciones todavia."}</div>`;
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
  body.className = "message-body";

  if (role === "assistant") {
    // Markdown con boton de copiar en los bloques de codigo.
    body.innerHTML = renderMarkdown(content || "_(vacio)_");
    bindCopyButtons(body, content || "");
  } else {
    body.textContent = content || "(vacio)";
  }

  article.appendChild(meta);
  article.appendChild(body);
  return article;
}

function bindCopyButtons(body, rawText) {
  body.querySelectorAll("pre.md-code").forEach((block, index) => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "btn md-copy";
    button.textContent = "Copiar";
    button.addEventListener("click", async () => {
      const code = block.querySelector("code")?.textContent || "";
      const ok = await copyTextToClipboard(code);
      button.textContent = ok ? "Copiado" : "Error";
      window.setTimeout(() => {
        button.textContent = "Copiar";
      }, 1600);
    });
    block.appendChild(button);
    block.dataset.index = String(index);
  });

  if (rawText.trim().length > 0) {
    const copyAll = document.createElement("button");
    copyAll.type = "button";
    copyAll.className = "btn md-copy-all";
    copyAll.textContent = "Copiar respuesta";
    copyAll.addEventListener("click", async () => {
      const ok = await copyTextToClipboard(rawText);
      copyAll.textContent = ok ? "Copiado" : "Error";
      window.setTimeout(() => {
        copyAll.textContent = "Copiar respuesta";
      }, 1600);
    });
    body.appendChild(copyAll);
  }
}

/* ============================== Mensaje en streaming ============================== */

function appendStreamingMessage() {
  const messageList = el("message-list");
  if (messageList.querySelector(".empty-state")) messageList.innerHTML = "";

  const article = document.createElement("article");
  article.className = "message assistant streaming";

  const meta = document.createElement("div");
  meta.className = "message-meta";
  meta.textContent = `assistant | ${new Date().toLocaleString()}`;

  const body = document.createElement("div");
  body.className = "message-body";
  body.innerHTML = '<span class="typing-dot" aria-hidden="true"></span>';

  article.appendChild(meta);
  article.appendChild(body);
  messageList.appendChild(article);
  messageList.scrollTop = messageList.scrollHeight;

  return article;
}

function paintStreamingMessage(article, text) {
  const body = article.querySelector(".message-body");
  body.innerHTML = renderMarkdown(text);
  body.insertAdjacentHTML("beforeend", '<span class="typing-dot" aria-hidden="true"></span>');
  el("message-list").scrollTop = el("message-list").scrollHeight;
}

function finalizeStreamingMessage(article, text, state) {
  article.classList.remove("streaming");
  const body = article.querySelector(".message-body");
  body.innerHTML = renderMarkdown(text || "_(sin contenido)_");
  bindCopyButtons(body, text || "");
  const meta = article.querySelector(".message-meta");
  if (meta) meta.textContent = `${meta.textContent} | ${state}`;
}

function addCitation(article, context) {
  if (!context) return;

  // Las citas viven FUERA del cuerpo Markdown: al repintar el texto se
  // regenera su innerHTML y se perderian si estuvieran dentro.
  let holder = article.querySelector(".message-citations");
  if (!holder) {
    holder = document.createElement("div");
    holder.className = "message-citations";
    const label = document.createElement("span");
    label.className = "muted-note";
    label.textContent = "Fuentes RAG:";
    holder.appendChild(label);
    article.appendChild(holder);
  }

  const chip = document.createElement("span");
  chip.className = "chip";
  chip.setAttribute("data-kind", "accent");
  const source = context.sourcePath || context.item?.sourcePath || context.source || "documento";
  chip.textContent = String(source).split(/[\\/]/).pop();
  chip.title = String(source);
  holder.appendChild(chip);
}

/* ============================== Gestion de conversaciones ============================== */

async function renameCurrentConversation() {
  if (!chatState.conversationId) return;

  const current = chatState.conversations.find((item) => item.conversationId === chatState.conversationId);
  const suggestion = current?.title || "Nueva conversacion";
  const title = await promptDialog({
    title: "Renombrar conversacion",
    label: "Titulo",
    value: suggestion,
    confirmLabel: "Renombrar"
  });
  if (!title) return;

  try {
    const result = await fetchJson(`/api/minichat/conversations/${encodeURIComponent(chatState.conversationId)}`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ title: title.trim() })
    });

    el("conversation-title").textContent = result.title;
    toast(`Conversacion renombrada a "${result.title}".`, "info");
    await loadConversations();
  } catch (error) {
    toast(`No se pudo renombrar: ${error.message}`, "error");
  }
}

async function deleteCurrentConversation() {
  if (!chatState.conversationId) return;
  if (!await confirmDialog({
    title: "Eliminar conversacion",
    message: "Se eliminara la conversacion y todos sus mensajes. Esta accion no se puede deshacer.",
    confirmLabel: "Eliminar",
    danger: true
  })) return;

  try {
    const response = await fetch(`/api/minichat/conversations/${encodeURIComponent(chatState.conversationId)}`, {
      method: "DELETE"
    });
    if (!response.ok && response.status !== 404) {
      const body = await response.json().catch(() => null);
      throw new Error(body?.error || `HTTP ${response.status}`);
    }

    toast("Conversacion eliminada.", "info");
    resetConversation();
    await loadConversations();
  } catch (error) {
    toast(`No se pudo eliminar: ${error.message}`, "error");
  }
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
  el("stop-button").hidden = !busy || !chatState.streaming;
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
