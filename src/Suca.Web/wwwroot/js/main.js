import { renderLayout } from "./layout.js";
import { initI18n, applyI18n, t, onLanguageChange } from "./i18n.js";
import { initShortcuts } from "./shortcuts.js";
import { announce } from "./a11y.js";

/* ============================== Estado global ============================== */

export const state = {
  settings: { values: {}, sources: {} },
  collections: [],
  server: { state: "checking", checkedAt: null }
};

/* ============================== Arranque ============================== */

document.addEventListener("DOMContentLoaded", async () => {
  initI18n();
  renderLayout();
  applyI18n();
  initShortcuts();
  initServerStatus();

  await loadServerSettings();

  // Cada pagina notifica a su vista que los ajustes estan listos.
  document.dispatchEvent(new CustomEvent("suca:settings"));
});

async function loadServerSettings() {
  try {
    state.settings = await fetchJson("/api/settings/effective");
  } catch (error) {
    toast(`No se pudieron cargar los ajustes: ${toErrorMessage(error)}`, "error");
  }
}

/* ============================== Estado del servidor ============================== */

let statusTimer = 0;

function paintServerStatus(nextState) {
  const chip = document.getElementById("server-status");
  if (!chip) return;

  state.server.state = nextState;
  chip.setAttribute("data-state", nextState);
  chip.setAttribute("data-tip-title", t(`status.${nextState}`));
  chip.setAttribute("data-tip", t(`status.${nextState}`));
  chip.setAttribute("title", t(`status.${nextState}`));

  const labels = { ok: "\u25CF", degraded: "\u25B2", down: "\u2715", checking: "\u22EF" };
  chip.textContent = labels[nextState] ?? labels.checking;
}

async function checkServerHealth() {
  const previous = state.server.state;
  let nextState = "down";

  try {
    const response = await fetch("/health", { cache: "no-store" });
    if (response.ok) {
      const body = await response.json().catch(() => null);
      const statusText = String(body?.status ?? "").toLowerCase();
      nextState = statusText === "degraded" || statusText === "unhealthy" ? "degraded" : "ok";
    }
  } catch {
    nextState = "down";
  }

  state.server.checkedAt = new Date();
  paintServerStatus(nextState);

  if (previous !== nextState) {
    if (nextState === "down" && previous !== "checking") {
      toast(t("status.lost"), "error", 6000);
      announce(t("status.down"));
    } else if (previous === "down" && nextState !== "down") {
      toast(t("status.restored"), "info");
      announce(t("status.ok"));
    }
  }

  return nextState;
}

function initServerStatus() {
  paintServerStatus("checking");
  void checkServerHealth();
  window.clearInterval(statusTimer);
  statusTimer = window.setInterval(() => void checkServerHealth(), 10000);

  onLanguageChange(() => {
    paintServerStatus(state.server.state);
  });

  // Al volver a la pestaña se reintenta de inmediato, sin esperar al sondeo.
  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "visible") void checkServerHealth();
  });
  window.addEventListener("focus", () => void checkServerHealth());

  // Clic en el chip = comprobacion inmediata.
  document.addEventListener("click", (event) => {
    if (event.target instanceof Element && event.target.closest("#server-status")) {
      void checkServerHealth();
    }
  });
}

export { checkServerHealth, announce };

/* ============================== Toasts ============================== */

export function toast(message, kind = "info", ttlMs = 4200) {
  const container = document.getElementById("toast-container");
  if (!container) return;

  const node = document.createElement("div");
  node.className = "toast";
  node.setAttribute("data-kind", kind);
  node.textContent = message;
  container.appendChild(node);

  window.setTimeout(() => node.remove(), ttlMs);
}

/* ============================== Utilidades compartidas ============================== */

export function el(id) {
  return document.getElementById(id);
}

export function escapeHtml(text) {
  return String(text ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

export function formatBytes(bytes) {
  const value = Number(bytes) || 0;
  if (value < 1024) return `${value} B`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`;
  return `${(value / (1024 * 1024)).toFixed(2)} MB`;
}

export function toErrorMessage(error) {
  return error && error.message ? error.message : String(error);
}

export async function fetchJson(url, options) {
  const response = await fetch(url, options);
  return parseResponse(response);
}

export async function parseResponse(response) {
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    let errorCode = "";
    try {
      const body = await response.json();
      if (body && body.error) message = body.error;
      if (body && body.errorCode) errorCode = body.errorCode;
    } catch { /* respuesta sin JSON */ }
    const error = new Error(message);
    error.errorCode = errorCode;
    throw error;
  }

  const text = await response.text();
  return text ? JSON.parse(text) : null;
}

export function correlationId() {
  return self.crypto?.randomUUID
    ? self.crypto.randomUUID().replace(/-/g, "")
    : `c${Date.now()}${Math.random().toString(16).slice(2, 10)}`;
}

export async function copyTextToClipboard(text) {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    try {
      const textarea = document.createElement("textarea");
      textarea.value = text;
      textarea.style.position = "fixed";
      textarea.style.opacity = "0";
      document.body.appendChild(textarea);
      textarea.select();
      document.execCommand("copy");
      textarea.remove();
      return true;
    } catch {
      return false;
    }
  }
}

/**
 * Sube un archivo con progreso real de bytes (XHR) y cancelacion.
 * onProgress(percent, phase) con phase: "uploading" | "processing".
 * registerCancel(cancelFn) entrega la funcion para abortar la subida.
 */
export function uploadWithProgress(file, form, { onProgress, correlation, registerCancel } = {}) {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open("POST", "/api/web/convert");
    xhr.setRequestHeader("X-Correlation-Id", correlation);
    xhr.responseType = "json";

    xhr.upload.onprogress = (event) => {
      if (event.lengthComputable && onProgress) {
        onProgress(Math.round((event.loaded / event.total) * 60), "uploading");
      }
    };

    xhr.upload.onload = () => {
      if (onProgress) onProgress(62, "processing");
    };

    xhr.onload = () => {
      const body = xhr.response || {};
      if (xhr.status >= 200 && xhr.status < 300) {
        if (onProgress) onProgress(100, "done");
        resolve(body);
      } else {
        const error = new Error(body.error || `HTTP ${xhr.status}`);
        error.errorCode = body.errorCode || "";
        reject(error);
      }
    };

    xhr.onerror = () => reject(new Error("Fallo de red al subir el archivo."));
    xhr.onabort = () => {
      const error = new Error("Conversion cancelada.");
      error.cancelled = true;
      reject(error);
    };

    if (registerCancel) registerCancel(() => xhr.abort());
    xhr.send(form);
  });
}
