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
  resolveSettingsReady();
});

/* Promesa para esperar a los ajustes: evita que una vista cargue con los valores
   por defecto y luego los recargue al llegar la configuracion real. */
let settingsReadyResolver = null;
const settingsReady = new Promise((resolve) => {
  settingsReadyResolver = resolve;
});

function resolveSettingsReady() {
  if (settingsReadyResolver) {
    settingsReadyResolver();
    settingsReadyResolver = null;
  }
}

export { settingsReady };

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
 * Sube un archivo con progreso real de bytes (XHR), progreso del servidor y cancelacion.
 * onProgress(percent, phase, info) con phase: "uploading" | "processing" | "done".
 * info incluye { heartbeatMs, serverPercent, detail } cuando el servidor informa.
 * registerCancel(cancelFn) entrega la funcion para abortar la subida.
 */
export function uploadWithProgress(file, form, { onProgress, correlation, registerCancel, timeoutMs } = {}) {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open("POST", "/api/web/convert");
    xhr.setRequestHeader("X-Correlation-Id", correlation);
    xhr.responseType = "json";

    // Sin timeout, una conversion pesada podia dejar la cola esperando para siempre.
    xhr.timeout = Number(timeoutMs) > 0 ? Number(timeoutMs) : 195000;

    let pollTimer = 0;
    let watchdogTimer = 0;
    let settled = false;
    let uploadDone = false;
    let lastByteAt = Date.now();
    let lastPollOkAt = 0;

    const stopTimers = () => {
      if (pollTimer) {
        window.clearInterval(pollTimer);
        pollTimer = 0;
      }
      if (watchdogTimer) {
        window.clearInterval(watchdogTimer);
        watchdogTimer = 0;
      }
    };

    const finish = (fn, value) => {
      if (settled) return;
      settled = true;
      stopTimers();
      fn(value);
    };

    // Vigilante: una subida estancada (red caida, proxy colgado) o un servidor
    // que dejo de responder no pueden dejar la cola esperando indefinidamente.
    watchdogTimer = window.setInterval(() => {
      const now = Date.now();
      if (!uploadDone && now - lastByteAt > 30000) {
        const error = new Error("La subida se detuvo hace 30 s sin enviar datos. Revisa la conexion y reintenta.");
        error.errorCode = "stalled";
        finish(reject, error);
        xhr.abort();
        return;
      }

      if (uploadDone && lastPollOkAt && now - lastPollOkAt > 25000) {
        const error = new Error("El servidor dejo de informar progreso hace 25 s. La conversion puede seguir en segundo plano; revisa la galeria.");
        error.errorCode = "stalled";
        finish(reject, error);
        xhr.abort();
      }
    }, 2000);

    // Heartbeat: mientras el servidor procesa, se consulta su progreso real.
    const startPolling = () => {
      if (pollTimer) window.clearInterval(pollTimer);
      pollTimer = window.setInterval(async () => {
        try {
          const response = await fetch(`/api/web/convert/progress?correlationId=${encodeURIComponent(correlation)}`, { cache: "no-store" });
          if (settled || !response.ok) return;

          lastPollOkAt = Date.now();
          const data = await response.json();
          if (settled) return;
          if (onProgress) {
            // El servidor reporta 0-100 propio; se mapea al tramo 55-99 del total.
            const total = 55 + Math.round(Number(data.percent || 0) * 0.44);
            onProgress(Math.min(99, total), "processing", {
              serverPercent: Number(data.percent || 0),
              stageMs: Number(data.sinceUpdateMs || 0),
              heartbeatMs: Number(data.heartbeatMs || 0),
              elapsedMs: Number(data.elapsedMs || 0),
              detail: data.detail || data.phase || ""
            });
          }
        } catch {
          // un fallo de sondeo no cancela la conversion
        }
      }, 500);
    };

    xhr.upload.onprogress = (event) => {
      lastByteAt = Date.now();
      if (event.lengthComputable && onProgress) {
        onProgress(Math.round((event.loaded / event.total) * 55), "uploading");
      }
    };

    xhr.upload.onload = () => {
      uploadDone = true;
      lastPollOkAt = Date.now();
      if (onProgress) onProgress(55, "processing");
      startPolling();
    };

    xhr.onload = () => {
      const body = xhr.response || {};
      if (xhr.status >= 200 && xhr.status < 300) {
        if (onProgress) onProgress(100, "done");
        finish(resolve, body);
      } else {
        const error = new Error(body.error || `HTTP ${xhr.status}`);
        error.errorCode = body.errorCode || "";
        finish(reject, error);
      }
    };

    xhr.ontimeout = () => {
      const error = new Error(
        `El servidor no respondio en ${Math.round(xhr.timeout / 1000)} s. La conversion puede seguir en segundo plano; revisa la galeria antes de reintentar.`
      );
      error.errorCode = "timeout";
      finish(reject, error);
    };

    xhr.onerror = () => {
      finish(reject, new Error("Fallo de red al subir el archivo."));
    };
    xhr.onabort = () => {
      const error = new Error("Conversion cancelada.");
      error.cancelled = true;
      finish(reject, error);
    };

    // El abort dispara xhr.onabort, que rechaza la promesa con cancelled=true.
    if (registerCancel) registerCancel(() => {
      if (settled) return;
      xhr.abort();
    });
    xhr.send(form);
  });
}
