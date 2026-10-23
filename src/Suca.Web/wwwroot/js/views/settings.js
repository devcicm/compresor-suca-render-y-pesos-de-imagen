import { state, el, toast, escapeHtml, fetchJson } from "../main.js";

let initialized = false;
const dirtyPaths = new Set();

export function initSettingsView() {
  if (initialized) return;
  initialized = true;

  document.querySelectorAll("[data-setting]").forEach((input) => {
    input.addEventListener("input", () => {
      dirtyPaths.add(input.dataset.setting);
      markDirtyLabels();
      validateField(input);
    });
    input.addEventListener("change", () => {
      dirtyPaths.add(input.dataset.setting);
      markDirtyLabels();
      validateField(input);
    });
  });

  el("save-user-settings").addEventListener("click", () => void saveSettings("user"));
  el("save-system-settings").addEventListener("click", () => void saveSettings("system"));
  el("reset-user-settings").addEventListener("click", () => void resetSettings("user"));
  el("reset-system-settings").addEventListener("click", () => void resetSettings("system"));

  // Fase E: buscador, cambios sin guardar e import/export.
  el("settings-filter").addEventListener("input", () => applyFilter(el("settings-filter").value.trim()));
  el("dirty-save-button").addEventListener("click", () => void saveSettings("user"));
  el("dirty-discard-button").addEventListener("click", discardChanges);
  el("export-settings-button").addEventListener("click", exportSettings);
  el("import-settings-button").addEventListener("click", () => el("import-settings-file").click());
  el("import-settings-file").addEventListener("change", (event) => void importSettings(event));

  document.addEventListener("suca:settings", () => {
    fillForm();
    renderPresets();
  });
}

/* ============================== Validacion en linea ============================== */

function validateField(input) {
  const label = input.closest("label[data-path]");
  if (!label) return true;

  let error = "";
  const raw = String(input.value ?? "").trim();

  if (raw !== "") {
    const min = input.min !== "" ? Number(input.min) : null;
    const max = input.max !== "" ? Number(input.max) : null;

    if (input.type === "number") {
      const value = Number(raw);
      if (!Number.isFinite(value)) error = "Debe ser un numero.";
      else if (min !== null && value < min) error = `El minimo es ${min}.`;
      else if (max !== null && value > max) error = `El maximo es ${max}.`;
    } else if (input.type === "url" && raw !== "" && !/^https?:\/\//i.test(raw)) {
      error = "Debe empezar por http:// o https://";
    }
  }

  input.setAttribute("aria-invalid", String(Boolean(error)));
  label.setAttribute("data-invalid", String(Boolean(error)));

  let note = label.querySelector(".field-error");
  if (error) {
    if (!note) {
      note = document.createElement("span");
      note.className = "field-error";
      note.setAttribute("role", "alert");
      label.appendChild(note);
    }
    note.textContent = error;
  } else if (note) {
    note.remove();
  }

  return !error;
}

function invalidFields() {
  return Array.from(document.querySelectorAll("[data-setting]")).filter((input) => !validateField(input));
}

/* ============================== Buscador de ajustes ============================== */

function applyFilter(query) {
  const needle = query.toLowerCase();
  let visible = 0;

  document.querySelectorAll("label[data-path]").forEach((label) => {
    const path = label.dataset.path || "";
    const text = label.textContent || "";
    const matches = !needle
      || path.toLowerCase().includes(needle)
      || text.toLowerCase().includes(needle);

    label.hidden = !matches;
    if (matches) visible += 1;
  });

  document.querySelectorAll(".card").forEach((card) => {
    const options = card.querySelectorAll("label[data-path]");
    if (options.length > 0) card.hidden = Array.from(options).every((label) => label.hidden);
  });

  el("settings-status").textContent = needle
    ? `${visible} ajuste(s) coinciden con "${query}".`
    : "";
}

/* ============================== Cambios sin guardar ============================== */

function markDirtyLabels() {
  document.querySelectorAll("label[data-path]").forEach((label) => {
    label.setAttribute("data-dirty", String(dirtyPaths.has(label.dataset.path)));
  });

  const bar = el("dirty-bar");
  if (!bar) return;

  bar.hidden = dirtyPaths.size === 0;
  el("dirty-summary").textContent = `${dirtyPaths.size} campo(s) modificado(s)`;
}

function discardChanges() {
  if (dirtyPaths.size === 0) return;
  dirtyPaths.clear();
  fillForm();
  el("settings-status").textContent = "Cambios descartados.";
}

/* ============================== Exportar / importar ============================== */

function exportSettings() {
  const payload = {
    exportedAtUtc: new Date().toISOString(),
    values: state.settings.values,
    sources: state.settings.sources
  };

  const blob = new Blob([JSON.stringify(payload, null, 2)], { type: "application/json" });
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = `suca-ajustes-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, "-")}.json`;
  link.click();
  URL.revokeObjectURL(url);
  el("settings-status").textContent = "Ajustes exportados como JSON.";
}

async function importSettings(event) {
  const file = event.target.files?.[0];
  if (!file) return;

  try {
    const text = await file.text();
    const parsed = JSON.parse(text);
    const values = parsed.values ?? parsed;
    if (!values || typeof values !== "object") {
      throw new Error("El archivo no contiene un objeto de ajustes.");
    }

    if (!window.confirm(`Importar estos ajustes en el scope usuario?\n\n${file.name}`)) return;

    await fetchJson("/api/settings/user", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(values)
    });

    toast("Ajustes importados.", "info");
    dirtyPaths.clear();
    await refreshFromServer();
    fillForm();
    document.dispatchEvent(new CustomEvent("suca:settings"));
  } catch (error) {
    toast(`No se pudo importar: ${error.message}`, "error");
  } finally {
    event.target.value = "";
  }
}

/* Listado informative de los presets guardados (la edicion vive en Compresor). */
function renderPresets() {
  const container = el("presets-readonly");
  if (!container) return;

  const presets = Array.isArray(state.settings.values.conversion?.presets)
    ? state.settings.values.conversion.presets
    : [];

  container.innerHTML = "";

  if (presets.length === 0) {
    container.innerHTML = '<div class="empty-state">Sin presets. Crea uno en la pantalla Compresor.</div>';
    return;
  }

  presets.forEach((preset) => {
    const parts = [
      preset.format ? `formato=${preset.format}` : null,
      preset.quality ? `calidad=${preset.quality}` : null,
      preset.maxWidth ? `ancho=${preset.maxWidth}` : null,
      preset.maxHeight ? `alto=${preset.maxHeight}` : null,
      preset.collection ? `coleccion=${preset.collection}` : null,
      preset.removeMetadata === false ? "conserva metadatos" : null
    ].filter(Boolean);

    const row = document.createElement("div");
    row.className = "preset-row-readonly";
    row.innerHTML = `
      <span class="chip" data-kind="accent">${escapeHtml(preset.name)}</span>
      <span class="muted-note">${escapeHtml(parts.join(", ") || "sin valores definidos")}</span>`;
    container.appendChild(row);
  });
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

async function saveSettings(scope) {
  const invalid = invalidFields();
  if (invalid.length > 0) {
    const first = invalid[0];
    el("settings-status").textContent = `Revisa ${invalid.length} campo(s) con valor fuera de rango: ${first.dataset.setting}.`;
    toast(`Campo invalido: ${first.dataset.setting}`, "warn");
    first.focus();
    return;
  }

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
