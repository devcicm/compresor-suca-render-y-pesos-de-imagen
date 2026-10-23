import { state, el, toast } from "../main.js";

let initialized = false;
const dirtyPaths = new Set();

export function initSettingsView() {
  if (initialized) return;
  initialized = true;

  document.querySelectorAll("[data-setting]").forEach((input) => {
    input.addEventListener("input", () => {
      dirtyPaths.add(input.dataset.setting);
      markDirtyLabels();
    });
  });

  el("save-user-settings").addEventListener("click", () => void saveSettings("user"));
  el("save-system-settings").addEventListener("click", () => void saveSettings("system"));
  el("reset-user-settings").addEventListener("click", () => void resetSettings("user"));
  el("reset-system-settings").addEventListener("click", () => void resetSettings("system"));

  document.addEventListener("suca:settings", fillForm);
}

async function refreshFromServer() {
  try {
    state.settings = await fetchJson("/api/settings/effective");
  } catch (error) {
    toast(`No se pudieron cargar los ajustes: ${error.message}`, "error");
  }
}

function fillForm() {
  document.querySelectorAll("[data-setting]").forEach((input) => {
    input.value = getPathValue(state.settings.values, input.dataset.setting) ?? "";
  });

  document.querySelectorAll("label[data-path]").forEach((label) => {
    const path = label.dataset.path;
    const source = (state.settings.sources || {})[path] || "default";
    let badge = label.querySelector(".source-badge");
    if (!badge) {
      badge = document.createElement("span");
      badge.className = "chip source-badge";
      label.querySelector(".field-label")?.appendChild(badge);
    }
    badge.textContent = source;
    badge.setAttribute("data-kind", source === "user" ? "accent" : source === "system" ? "warn" : "");
  });

  dirtyPaths.clear();
  markDirtyLabels();
  el("settings-status").textContent = "Ajustes cargados.";
}

function markDirtyLabels() {
  document.querySelectorAll("label[data-path]").forEach((label) => {
    const isDirty = dirtyPaths.has(label.dataset.path);
    label.style.color = isDirty ? "var(--color-accent)" : "";
  });
}

async function saveSettings(scope) {
  const body = {};
  for (const path of dirtyPaths) {
    const input = document.querySelector(`[data-setting="${path}"]`);
    if (!input || String(input.value).trim() === "") continue;

    const raw = input.value;
    const asNumber = Number(raw);
    const value = raw === "true" || raw === "false"
      ? raw === "true"
      : Number.isFinite(asNumber) && raw.trim() !== ""
        ? asNumber
        : raw;

    setPathValue(body, path.split("."), value);
  }

  if (Object.keys(body).length === 0) {
    el("settings-status").textContent = "No hay campos modificados para guardar.";
    return;
  }

  try {
    const response = await fetch(`/api/settings/${scope}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body)
    });

    if (!response.ok) {
      const body2 = await response.json().catch(() => ({}));
      throw new Error(body2.error || `HTTP ${response.status}`);
    }

    toast(`Ajustes guardados en el scope ${scope}.`, "info");
    dirtyPaths.clear();
    await refreshFromServer();
    fillForm();
    document.dispatchEvent(new CustomEvent("suca:settings"));
  } catch (error) {
    toast(`Error al guardar: ${error.message}`, "error");
  }
}

async function resetSettings(scope) {
  if (!window.confirm(`Restablecer el scope ${scope} a los valores por defecto?`)) return;

  try {
    const response = await fetch(`/api/settings/${scope}/reset`, { method: "POST" });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);

    toast(`Scope ${scope} restablecido.`, "info");
    dirtyPaths.clear();
    await refreshFromServer();
    fillForm();
    document.dispatchEvent(new CustomEvent("suca:settings"));
  } catch (error) {
    toast(`Error al restablecer: ${error.message}`, "error");
  }
}

function getPathValue(source, path) {
  return path.split(".").reduce((acc, key) => (acc == null ? undefined : acc[key]), source);
}

function setPathValue(target, segments, value) {
  let node = target;
  for (let i = 0; i < segments.length - 1; i++) {
    node[segments[i]] = node[segments[i]] || {};
    node = node[segments[i]];
  }
  node[segments[segments.length - 1]] = value;
}
