import { renderLayout } from "./layout.js";

/* ============================== Estado global ============================== */

export const state = {
  settings: { values: {}, sources: {} },
  collections: []
};

/* ============================== Arranque ============================== */

document.addEventListener("DOMContentLoaded", async () => {
  renderLayout();

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
