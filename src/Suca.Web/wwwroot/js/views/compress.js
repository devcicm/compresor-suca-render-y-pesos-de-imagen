import {
  state, el, escapeHtml, formatBytes, toast,
  fetchJson, uploadWithProgress, correlationId, copyTextToClipboard,
  announce, checkServerHealth
} from "../main.js";
import { t } from "../i18n.js";
import { openOverlay, closeOverlay, enableListNavigation } from "../a11y.js";
import { pesoObjetivoChip, integrityChips } from "../ui/chips.js";
import { confirmDialog, promptDialog } from "../ui/dialog.js";
import { readOptions, applyOptions, overridesFromOptions, appendOptionsToForm } from "../ui/options.js";

const queue = [];           // { key, file, previewUrl, state, percent, phase, record, error, cancelFn }
let converting = false;
let globalPaused = false;
let initialized = false;
let optionsPinned = false;  // el usuario edito opciones o aplico un preset
let lastOptimized = null;   // { record, options } de la ultima conversion optimizada

export function initCompressView() {
  if (initialized) return;
  initialized = true;

  const dropzone = el("dropzone");
  const input = el("file-input");

  dropzone.addEventListener("click", (event) => {
    if (event.target.id !== "browse-button") input.click();
  });
  dropzone.addEventListener("keydown", (event) => {
    if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      input.click();
    }
  });

  el("browse-button").addEventListener("click", () => input.click());
  input.addEventListener("change", () => {
    addFiles(input.files);
    input.value = "";
  });

  // Pegar imagenes desde el portapapeles (Ctrl+V) en cualquier punto de la pagina.
  document.addEventListener("paste", (event) => {
    const target = event.target;
    if (target instanceof Element && target.matches("input, textarea")) return;

    const items = event.clipboardData?.items || [];
    const files = [];
    for (const item of items) {
      if (item.kind === "file" && item.type.startsWith("image/")) {
        const file = item.getAsFile();
        if (file) files.push(file);
      }
    }

    if (files.length === 0) return;
    event.preventDefault();
    addFiles(files);
    toast(`Se pegaron ${files.length} imagen(es).`, "info");
  });

  ["dragenter", "dragover"].forEach((name) =>
    dropzone.addEventListener(name, (event) => {
      event.preventDefault();
      dropzone.setAttribute("data-dragover", "true");
    }));

  ["dragleave", "drop"].forEach((name) =>
    dropzone.addEventListener(name, (event) => {
      event.preventDefault();
      dropzone.setAttribute("data-dragover", "false");
    }));

  dropzone.addEventListener("drop", (event) => {
    void handleDrop(event.dataTransfer);
  });

  el("convert-button").addEventListener("click", () => void convertQueue());
  el("clear-button").addEventListener("click", clearQueue);
  el("pause-all-button").addEventListener("click", toggleGlobalPause);
  el("select-all-button").addEventListener("click", toggleSelectAll);
  el("apply-to-selected-button").addEventListener("click", applyOptionsToSelected);
  el("remove-selected-button").addEventListener("click", removeSelected);
  el("retry-failed-button").addEventListener("click", retryFailed);
  el("clear-finished-button").addEventListener("click", clearFinished);
  el("cancel-all-button").addEventListener("click", cancelAllItems);

  el("preset-select").addEventListener("change", onPresetSelected);
  el("preset-apply").addEventListener("click", () => applyPresetToAll());
  el("preset-apply-selected").addEventListener("click", () => applyPresetToSelected());
  el("preset-save").addEventListener("click", () => void savePresetFromOptions());
  el("preset-delete").addEventListener("click", () => void deletePreset());

  // Panel de valores optimos: reutilizar lo que eligio el optimizador.
  el("optimal-apply").addEventListener("click", applyOptimalToPending);
  el("optimal-save-profile").addEventListener("click", saveOptimalAsProfile);

  el("browse-folder-button").addEventListener("click", () => void openFolderBrowser());
  el("order-validate").addEventListener("click", () => void validateOrder());
  el("folder-close").addEventListener("click", closeFolderBrowser);
  el("folder-up").addEventListener("click", () => {
    if (folderState.parent !== null && folderState.parent !== undefined) {
      void loadFolder(folderState.parent);
    }
  });
  el("folder-select").addEventListener("click", selectFolder);
  el("folder-overlay").addEventListener("click", (event) => {
    if (event.target === el("folder-overlay")) closeFolderBrowser();
  });

  // Atajos globales: abrir archivos, convertir y cancelar.
  document.addEventListener("suca:action", (event) => {
    const action = event.detail?.action;
    if (action === "open-files") {
      el("file-input").click();
    } else if (action === "convert") {
      void convertQueue();
    } else if (action === "cancel-all") {
      cancelAllItems();
    }
  });

  enableListNavigation(el("queue-list"), ".queue-item");

  // Los ajustes llegan despues del arranque: la lista de presets se repinta y
  // solo se aplican los valores por defecto si el usuario no toco las opciones.
  document.addEventListener("suca:settings", () => {
    renderPresetList();
    if (!optionsPinned) applyDefaults();
  });
  document.addEventListener("suca:collections", fillCollectionList);
  refreshCollectionList();
  renderPresetList();

  // Cualquier edicion manual fija las opciones: applyDefaults deja de pisarlas.
  ["engine", "format", "quality", "max-width", "max-height", "collection", "remove-metadata"]
    .forEach((id) => {
      const control = el(id);
      if (!control) return;
      control.addEventListener("input", pinOptions);
      control.addEventListener("change", pinOptions);
    });

  el("quality").addEventListener("input", () => {
    el("quality-out").textContent = el("quality").value;
  });

  el("format").addEventListener("change", () => {
    const isPng = el("format").value === "png";
    el("quality").disabled = isPng;
  });

  el("order-submit").addEventListener("click", () => void submitOrder());
}

/* Marca que las opciones visibles ya no siguen a los ajustes por defecto. */
function pinOptions() {
  optionsPinned = true;
}

/* Atajo "cancelar todo": detiene lo que este en curso y deja el resto en espera. */
function cancelAllItems() {
  let affected = 0;

  for (const item of queue) {
    if (["uploading", "processing"].includes(item.state)) {
      cancelItem(item);
      affected += 1;
    } else if (item.state === "pending" || item.state === "waiting") {
      item.state = "cancelled";
      item.error = "Cancelada";
      affected += 1;
    }
  }

  renderQueue();
  toast(
    affected > 0 ? `Se cancelaron ${affected} conversion(es) en curso.` : "No hay conversiones en curso.",
    affected > 0 ? "warn" : "info"
  );
}

function applyDefaults() {
  const conversion = state.settings.values.conversion || {};
  const web = state.settings.values.web || {};

  // Un unico punto aplica valores a los controles (js/ui/options.js): evita
  // repetir el mapeo campo por campo en cada vista.
  applyOptions({
    engine: conversion.engine,
    format: conversion.format,
    quality: typeof conversion.quality === "number" ? conversion.quality : null,
    maxWidth: conversion.maxWidth ?? null,
    maxHeight: conversion.maxHeight ?? null,
    removeMetadata: conversion.removeMetadata ?? true,
    collection: null,
    profile: null,
    pesoObjetivo: null,
    allowLossy: true,
    optimize: false
  });

  el("quality-out").textContent = el("quality").value;

  if (typeof web.convertParallelism === "number") {
    el("parallel-info").textContent = web.convertParallelism;
  }
}

async function refreshCollectionList() {
  try {
    state.collections = await fetchJson("/api/web/collections");
    fillCollectionList();
  } catch { /* sin colecciones aun */ }
}

function fillCollectionList() {
  const list = el("collection-list");
  if (!list) return;
  list.innerHTML = "";
  for (const entry of state.collections) {
    const option = document.createElement("option");
    option.value = entry.collection;
    list.appendChild(option);
  }
}

/* ============================== Cola ============================== */

const IMAGE_EXTENSIONS = [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tiff"];

function addFiles(fileList) {
  const files = Array.from(fileList || []).filter(looksLikeImage);
  if (files.length === 0) {
    toast("No se reconocio ninguna imagen en la seleccion.", "warn");
    return;
  }

  for (const file of files) {
    queue.push({
      key: `${Date.now()}-${Math.random().toString(36).slice(2)}`,
      file,
      previewUrl: URL.createObjectURL(file),
      state: "pending",       // pending | waiting | uploading | processing | done | paused | error | cancelled
      percent: 0,
      phase: "",
      record: null,
      error: null,
      cancelFn: null,
      selected: false,
      overrides: null         // { engine, format, quality, maxWidth, maxHeight, collection }
    });
  }

  renderQueue();
  void convertQueue();
}

function looksLikeImage(file) {
  if (file.type && file.type.startsWith("image/")) return true;
  const name = (file.name || "").toLowerCase();
  return IMAGE_EXTENSIONS.some((ext) => name.endsWith(ext));
}

function pendingCount() {
  return queue.filter((item) => item.state === "pending" || item.state === "waiting").length;
}

async function convertQueue() {
  if (converting) return;
  converting = true;

  const parallelism = Math.max(1, Number(state.settings.values.web?.convertParallelism) || 3);
  el("pause-all-button").hidden = false;
  el("aggregate-bar").hidden = false;

  let index = 0;
  const workers = Array.from({ length: Math.min(parallelism, queue.length) }, async () => {
    while (true) {
      const item = nextItem();
      if (!item) return;

      item.state = "uploading";
      renderQueue();

      const correlation = correlationId();
      item.correlation = correlation;

      try {
        item.record = await uploadWithProgress(item.file, buildForm(item, correlation), {
          correlation,
          registerCancel: (cancelFn) => { item.cancelFn = cancelFn; },
          onProgress: (percent, phase) => {
            item.percent = percent;
            item.phase = phase;
            if (phase === "processing") item.state = "processing";
            renderQueue();
          }
        });
        item.state = "done";
        item.percent = 100;

        // Guarda el resultado optimizado para el panel "valores optimos".
        if (item.record?.optimized) {
          lastOptimized = { record: item.record, options: readOptions() };
        }
      } catch (error) {
        if (error.cancelled || error.errorCode === "cancelled") {
          // La pausa intencional deja el estado en "paused" (toggleGlobalPause
          // marco antes de abortar). Solo marcar cancelada si nadie la pauso.
          if (item.state !== "paused") {
            item.state = "cancelled";
            item.error = "Cancelada";
          }
        } else {
          item.state = "error";
          item.error = error.message;
          toast(`${item.file.name}: ${error.message}`, "error");
        }
      }

      renderQueue();
    }
  });

  function nextItem() {
    while (index < queue.length) {
      const candidate = queue[index];
      index += 1;
      if (candidate.state === "pending") return candidate;
    }
    return null;
  }

  await Promise.all(workers);

  converting = false;
  renderQueue();
  refreshCollectionList();
}

function toggleGlobalPause() {
  globalPaused = !globalPaused;
  el("pause-all-button").textContent = globalPaused ? "Reanudar todo" : "Pausar todo";

  for (const item of queue) {
    if (globalPaused) {
      if (item.state === "pending" || item.state === "waiting") item.state = "paused";
      else if ((item.state === "uploading" || item.state === "processing") && item.cancelFn) {
        const wasProcessing = item.state === "processing";
        item.cancelFn();
        item.state = "paused";
        item.percent = 0;
        if (wasProcessing && item.correlation) void cancelOnServer(item);
      }
    } else if (item.state === "paused") {
      item.state = "pending";
    }
  }

  renderQueue();
  if (!globalPaused && !converting) void convertQueue();
}

function cancelItem(item) {
  if (item.state === "uploading") {
    if (item.cancelFn) item.cancelFn();
    return;
  }

  // Durante el procesamiento la peticion ya llego al servidor: la cancelacion
  // real pasa por el endpoint de cancelacion (mapa correlacion -> CTS).
  if (item.state === "processing") {
    item.state = "cancelled";
    item.error = "Cancelada";
    item.percent = 0;
    void cancelOnServer(item);
    renderQueue();
    return;
  }

  if (!["done", "error", "cancelled"].includes(item.state)) {
    item.state = "cancelled";
    item.error = "Cancelada";
    renderQueue();
  }
}

async function cancelOnServer(item) {
  if (!item.correlation) return;
  try {
    await fetch("/api/web/convert/cancel", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ correlationId: item.correlation })
    });
  } catch { /* el item ya esta marcado cancelado en la cola */ }
}

function resumeItem(item) {
  if (item.state === "paused") {
    item.state = "pending";
    renderQueue();
    if (!converting) void convertQueue();
  }
}

function clearQueue() {
  for (const item of queue) {
    if (item.cancelFn && ["uploading", "processing"].includes(item.state)) item.cancelFn();
    URL.revokeObjectURL(item.previewUrl);
  }
  queue.length = 0;
  el("aggregate-bar").hidden = true;
  el("pause-all-button").hidden = true;
  renderQueue();
}

/* ============================== Render ============================== */

function renderQueue() {
  const list = el("queue-list");
  if (!list) return;
  list.innerHTML = "";

  renderOptimalPanel();

  const editableCount = queue.filter(editableState).length;
  el("select-all-button").hidden = editableCount === 0;
  el("apply-to-selected-button").hidden = editableCount === 0;
  el("remove-selected-button").hidden = editableCount === 0;

  const failedCount = queue.filter((item) => item.state === "error").length;
  el("retry-failed-button").hidden = failedCount === 0;

  const finishedCount = queue.filter((item) => ["done", "cancelled", "error"].includes(item.state)).length;
  el("clear-finished-button").hidden = finishedCount === 0;

  el("cancel-all-button").hidden = !queue.some((item) =>
    ["pending", "waiting", "uploading", "processing"].includes(item.state));

  for (const item of queue) {
    const li = document.createElement("li");
    li.className = "queue-item";
    li.setAttribute("tabindex", "0");
    li.setAttribute("data-nav-item", "true");
    li.setAttribute("aria-label", item.file.name);
    li.setAttribute("data-state", item.state === "done" ? "done" : item.state === "error" ? "error" : "");

    if (editableState(item)) {
      const select = document.createElement("input");
      select.type = "checkbox";
      select.checked = item.selected;
      select.setAttribute("aria-label", "Seleccionar para edicion en lote");
      select.addEventListener("change", () => { item.selected = select.checked; });
      li.appendChild(select);
    } else {
      li.appendChild(document.createElement("span"));
    }

    const img = document.createElement("img");
    img.src = item.previewUrl;
    img.alt = "";
    li.appendChild(img);

    const body = document.createElement("div");
    body.innerHTML = `
      <div class="q-name">${escapeHtml(item.file.name)}</div>
      <div class="q-meta">${formatBytes(item.file.size)} &middot; ${escapeHtml(item.file.type || "tipo desconocido")}</div>`;

    if (item.overrides) {
      body.innerHTML += `<div class="q-override-note">override: ${escapeHtml(describeOverrides(item.overrides))}</div>`;
    }

    const progress = document.createElement("div");
    progress.className = "progress q-progress";
    const fill = document.createElement("div");
    fill.className = "progress-fill";
    fill.style.width = `${item.percent}%`;
    if (item.phase === "processing") fill.setAttribute("data-indeterminate", "true");
    progress.appendChild(fill);
    body.appendChild(progress);

    if (item.record) {
      const r = item.record;
      body.innerHTML += `
        <div class="q-meta">
          <span class="chip" data-kind="accent">${escapeHtml(String(r.format).toUpperCase())} q${r.quality ?? "-"}</span>
          <span class="chip">${r.originalWidth}&times;${r.originalHeight} &rarr; ${r.convertedWidth}&times;${r.convertedHeight}</span>
          <span class="chip">${formatBytes(r.originalBytes)} &rarr; ${formatBytes(r.convertedBytes)}</span>
          <span class="chip" data-kind="ok">-${Math.abs(r.savingPercent).toFixed(1)}%</span>
          <span class="chip">${r.outputBitsPerPixel} bpp</span>
          <span class="chip">${r.outputCompressionRatio}:1</span>
          <span class="chip">${r.durationMs} ms</span>
          <span class="chip">${escapeHtml(r.collection)}</span>
          ${pesoObjetivoChip(r)}
          ${integrityChips(r)}
        </div>
        <div class="q-meta q-links">
          <a href="/api/web/images/${r.id}/file?variant=converted" download>Descargar</a> ·
          <a href="#" data-copy="${r.id}">Copiar base64</a> ·
          <a href="#" data-copy-path="${r.id}">Copiar ruta</a> ·
          <a href="#" data-detail="${r.id}">Detalle</a>
        </div>`;
    }

    if (item.error) {
      body.innerHTML += `<div class="q-meta" data-kind="bad">${escapeHtml(item.error)}</div>`;
    }

    if (editableState(item)) {
      body.appendChild(buildItemEditor(item));
    }

    li.appendChild(body);

    const actions = document.createElement("div");
    actions.className = "q-actions";

    const badge = document.createElement("span");
    badge.className = "badge";
    badge.setAttribute("data-state", item.state === "done" ? "done" : item.state === "error" ? "error" : "");
    badge.textContent = stateLabel(item.state);
    actions.appendChild(badge);

    if (item.state === "paused") {
      const resume = document.createElement("button");
      resume.type = "button";
      resume.textContent = "\u25B6";
      resume.setAttribute("aria-label", "Reanudar");
      resume.addEventListener("click", () => resumeItem(item));
      actions.appendChild(resume);
    }

    if (!["done", "cancelled"].includes(item.state)) {
      const cancel = document.createElement("button");
      cancel.type = "button";
      cancel.textContent = "\u2715";
      cancel.setAttribute("aria-label", "Cancelar");
      cancel.addEventListener("click", () => cancelItem(item));
      actions.appendChild(cancel);
    }

    if (editableState(item)) {
      const remove = document.createElement("button");
      remove.type = "button";
      remove.textContent = "\u2718";
      remove.title = "Quitar de la cola";
      remove.addEventListener("click", () => removeItem(item));
      actions.appendChild(remove);
    }

    li.appendChild(actions);
    list.appendChild(li);
  }

  list.querySelectorAll("[data-copy]").forEach((link) => {
    link.addEventListener("click", (event) => {
      event.preventDefault();
      void copyResultBase64(link.dataset.copy);
    });
  });

  list.querySelectorAll("[data-copy-path]").forEach((link) => {
    link.addEventListener("click", (event) => {
      event.preventDefault();
      void copyResultPath(link.dataset.copyPath);
    });
  });

  list.querySelectorAll("[data-detail]").forEach((link) => {
    link.addEventListener("click", (event) => {
      event.preventDefault();
      window.location.href = `/studio/galeria.html?detalle=${encodeURIComponent(link.dataset.detail)}`;
    });
  });

  const pending = pendingCount();
  const total = queue.length;
  const done = queue.filter((x) => x.state === "done").length;
  const aggregate = total === 0 ? 0 : Math.round(((done + (total - pending - done)) / total) * 100);

  el("convert-button").disabled = converting || pending === 0;
  el("aggregate-fill").style.width = `${aggregate}%`;
  el("aggregate-text").textContent = `${done}/${total} listas · ${aggregate}%`;

  el("queue-status").textContent = total === 0
    ? t("queue.empty")
    : `${total} archivo(s), ${pending} pendientes, ${done} completadas.`;

  announce(t("queue.live", { done, total }));
}

function stateLabel(state) {
  const label = t(`queue.state.${state}`);
  return label.startsWith("queue.state.") ? state : label;
}

async function copyResultBase64(id) {
  try {
    const payload = await fetchJson(`/api/web/images/${id}/base64?variant=converted`);
    const copied = await copyTextToClipboard(payload.base64);
    if (copied) toast(`Base64 copiado: ${formatBytes(payload.byteCount)}.`, "info");
    else toast("El navegador bloqueo el portapapeles.", "error");
  } catch (error) {
    toast(`No se pudo copiar el base64: ${error.message}`, "error");
  }
}

async function copyResultPath(id) {
  try {
    const payload = await fetchJson(`/api/web/images/${id}/path?variant=converted`);
    const copied = await copyTextToClipboard(payload.path);
    if (copied) toast(`Ruta copiada: ${payload.fileName}`, "info");
    else toast("El navegador bloqueo el portapapeles.", "error");
  } catch (error) {
    toast(`No se pudo copiar la ruta: ${error.message}`, "error");
  }
}

/* ============================== Orden orquestada ============================== */

async function submitOrder() {
  const statusEl = el("order-status");
  let request;

  const rawJson = el("order-json").value.trim();
  if (rawJson) {
    try {
      request = JSON.parse(rawJson);
    } catch (error) {
      toast(`JSON de orden invalido: ${error.message}`, "error");
      return;
    }
  } else {
    const directory = el("order-directory").value.trim();
    if (!directory) {
      toast("Indica una carpeta del servidor o pega el JSON de la orden.", "warn");
      return;
    }

    request = {
      sourceDirectory: directory,
      recursive: true,
      defaults: {
        engine: el("engine").value,
        format: el("format").value,
        quality: Number(el("quality").value) || undefined,
        removeMetadata: el("remove-metadata").checked,
        collection: el("collection").value.trim() || undefined
      },
      parallelism: Number(state.settings.values.web?.convertParallelism) || 3
    };
  }

  try {
    statusEl.textContent = "Enviando orden...";
    const response = await fetch("/api/web/orders", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(request)
    });

    const body = await response.json();
    if (!response.ok) {
      throw new Error(body.error || `HTTP ${response.status}`);
    }

    statusEl.innerHTML = `Orden <span class="cell-mono">${escapeHtml(body.orderId)}</span> aceptada con ${body.totalItems} imagen(es). ` +
      `<a href="#/actividad">Ver progreso en Actividad</a>`;
    toast(`Orden aceptada: ${body.totalItems} imagen(es).`, "info");
    document.dispatchEvent(new CustomEvent("suca:orders-dirty"));
  } catch (error) {
    statusEl.textContent = "";
    toast(`No se pudo enviar la orden: ${error.message}`, "error");
  }
}

/* ============================== Edicion de la cola ============================== */

function editableState(item) {
  return ["pending", "waiting", "paused"].includes(item.state);
}

function buildItemEditor(item) {
  const ov = item.overrides || {};
  const details = document.createElement("details");
  details.className = "q-edit";

  const engine = ov.engine ?? el("engine").value;
  const format = ov.format ?? el("format").value;
  const quality = ov.quality ?? el("quality").value;

  details.innerHTML = `
    <summary>Editar opciones de esta imagen</summary>
    <div class="q-edit-form">
      <label>Motor
        <select data-q="engine">
          <option value="suca"${engine === "suca" ? " selected" : ""}>SUCA</option>
          <option value="direct"${engine === "direct" ? " selected" : ""}>Directo</option>
        </select>
      </label>
      <label>Formato
        <select data-q="format">
          <option value="webp"${format === "webp" ? " selected" : ""}>WebP</option>
          <option value="jpeg"${format === "jpeg" ? " selected" : ""}>JPEG</option>
          <option value="png"${format === "png" ? " selected" : ""}>PNG</option>
        </select>
      </label>
      <label>Calidad
        <input type="number" min="1" max="100" data-q="quality" value="${Number(quality) || 80}">
      </label>
      <label>Coleccion
        <input type="text" data-q="collection" value="${escapeHtml(ov.collection ?? "")}" placeholder="(global)">
      </label>
      <label>Ancho max.
        <input type="number" min="0" data-q="maxWidth" value="${ov.maxWidth ? Number(ov.maxWidth) : ""}" placeholder="(global)">
      </label>
      <label>Alto max.
        <input type="number" min="0" data-q="maxHeight" value="${ov.maxHeight ? Number(ov.maxHeight) : ""}" placeholder="(global)">
      </label>
      <button type="button" class="btn" data-apply-edit>Guardar</button>
      ${item.overrides ? '<button type="button" class="btn" data-clear-edit>Quitar override</button>' : ""}
    </div>`;

  details.querySelector("[data-apply-edit]").addEventListener("click", () => {
    const value = (key) => details.querySelector(`[data-q="${key}"]`).value;
    item.overrides = {
      engine: value("engine"),
      format: value("format"),
      quality: Number(value("quality")) || undefined,
      maxWidth: value("maxWidth") ? Number(value("maxWidth")) : undefined,
      maxHeight: value("maxHeight") ? Number(value("maxHeight")) : undefined,
      collection: value("collection").trim() || undefined
    };
    renderQueue();
    toast(`Opciones guardadas para ${item.file.name}.`, "info");
  });

  const clearButton = details.querySelector("[data-clear-edit]");
  if (clearButton) {
    clearButton.addEventListener("click", () => {
      item.overrides = null;
      renderQueue();
    });
  }

  return details;
}

function describeOverrides(ov) {
  const parts = [];
  if (ov.engine) parts.push(`motor=${ov.engine}`);
  if (ov.format) parts.push(`formato=${ov.format}`);
  if (ov.quality) parts.push(`calidad=${ov.quality}`);
  if (ov.maxWidth) parts.push(`ancho=${ov.maxWidth}`);
  if (ov.maxHeight) parts.push(`alto=${ov.maxHeight}`);
  if (ov.collection) parts.push(`coleccion=${ov.collection}`);
  if (ov.profile) parts.push(`perfil=${ov.profile.a}${ov.profile.b}${ov.profile.c}${ov.profile.d}`);
  if (ov.pesoObjetivo) parts.push(`techo=${ov.pesoObjetivo}KB`);
  if (ov.allowLossy === false) parts.push("sin perdida");
  if (ov.optimize) parts.push("optimizar");
  return parts.join(", ");
}

function removeItem(item) {
  if (item.cancelFn && ["uploading", "processing"].includes(item.state)) item.cancelFn();
  URL.revokeObjectURL(item.previewUrl);

  const index = queue.indexOf(item);
  if (index >= 0) queue.splice(index, 1);

  renderQueue();
}

function buildForm(item, correlation) {
  const form = new FormData();
  form.append("file", item.file, item.file.name);
  form.append("correlationId", correlation);
  appendOptionsToForm(form, readOptions(), item.overrides || {});
  return form;
}

function toggleSelectAll() {
  const allSelected = queue.filter(editableState).every((item) => item.selected);
  queue.forEach((item) => {
    if (editableState(item)) item.selected = !allSelected;
  });
  renderQueue();
}

function applyOptionsToSelected() {
  const selected = queue.filter((item) => item.selected && editableState(item));
  if (selected.length === 0) {
    toast("Marque al menos una imagen pendiente de la cola.", "warn");
    return;
  }

  const overrides = overridesFromOptions(readOptions());
  for (const item of selected) item.overrides = overrides;

  renderQueue();
  toast(`Opciones aplicadas a ${selected.length} imagen(es).`, "info");
}

function removeSelected() {
  for (const item of queue) {
    if (item.selected && (item.state === "uploading" || item.state === "processing") && item.cancelFn) {
      item.cancelFn();
    }
    if (item.selected) URL.revokeObjectURL(item.previewUrl);
  }

  for (let i = queue.length - 1; i >= 0; i--) {
    if (queue[i].selected) queue.splice(i, 1);
  }

  renderQueue();
  toast("Imagenes quitadas de la cola.", "info");
}

/* ============================== Explorador de carpetas ============================== */

const folderState = { path: "", parent: null };

function openFolderBrowser() {
  el("folder-error").textContent = "";
  openOverlay(el("folder-overlay"));
  void loadFolder("");
}

function closeFolderBrowser() {
  closeOverlay(el("folder-overlay"));
}

async function loadFolder(path) {
  const list = el("folder-items");
  list.innerHTML = `<div class="empty-state">${escapeHtml(t("common.loading"))}</div>`;
  el("folder-error").textContent = "";

  try {
    const url = path
      ? `/api/web/folders?path=${encodeURIComponent(path)}`
      : "/api/web/folders";
    const data = await fetchJson(url);

    folderState.path = data.path || "";
    folderState.parent = data.parent;
    el("folder-current").textContent = data.mode === "drives"
      ? t("folder.drives")
      : (data.path || "(raiz)");

    list.innerHTML = "";
    if (data.items.length === 0) {
      list.innerHTML = `<div class="empty-state">${escapeHtml(t("folder.empty"))}</div>`;
      return;
    }

    for (const entry of data.items) {
      const row = document.createElement("div");
      row.className = "folder-entry";
      row.setAttribute("role", "button");
      row.setAttribute("tabindex", "0");
      row.innerHTML = `
        <span>&#128193; ${escapeHtml(entry.name)}</span>
        ${entry.imageCount !== null && entry.imageCount !== undefined
          ? `<span class="f-count">${escapeHtml(t("folder.count", { count: entry.imageCount }))}</span>`
          : ""}`;
      row.addEventListener("click", () => void loadFolder(entry.path));
      row.addEventListener("keydown", (event) => {
        if (event.key === "Enter" || event.key === " ") {
          event.preventDefault();
          void loadFolder(entry.path);
        }
      });
      list.appendChild(row);
    }

    enableListNavigation(list, ".folder-entry");
  } catch (error) {
    list.innerHTML = "";
    el("folder-error").textContent = error.message;
  }
}

function selectFolder() {
  if (!folderState.path) {
    toast("Entre a una unidad y seleccione una carpeta.", "warn");
    return;
  }

  el("order-directory").value = folderState.path;
  closeFolderBrowser();
  void validateOrder();
}

/* ============================== Validacion de la orden ============================== */

async function validateOrder() {
  const statusEl = el("order-status");
  const directory = el("order-directory").value.trim();
  if (!directory) {
    statusEl.textContent = "Indique una carpeta del servidor para validar.";
    return;
  }

  statusEl.textContent = "Validando...";

  try {
    const request = {
      sourceDirectory: directory,
      recursive: true,
      parallelism: Number(state.settings.values.web?.convertParallelism) || 3,
      defaults: {
        engine: el("engine").value,
        format: el("format").value,
        collection: el("collection").value.trim() || undefined
      }
    };

    const preview = await fetchJson("/api/web/orders/preview", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(request)
    });

    statusEl.innerHTML = `Valida: <strong>${preview.itemCount}</strong> imagen(es)` +
      ` (${formatBytes(preview.totalBytes)}), ${preview.parallelism} en paralelo. Ejemplos: ` +
      escapeHtml(preview.firstItems.slice(0, 3).join(", "));
    toast(`Carpeta valida: ${preview.itemCount} imagen(es).`, "info");
  } catch (error) {
    statusEl.textContent = "";
    toast(`Carpeta invalida: ${error.message}`, "error");
  }
}

/* ============================== Valores optimos ============================== */

/* Panel con lo que eligio el optimizador: permite reutilizarlo sin re-teclearlo. */
function renderOptimalPanel() {
  const panel = el("optimal-panel");
  if (!panel) return;

  if (!lastOptimized) {
    panel.hidden = true;
    return;
  }

  const { record } = lastOptimized;
  const legible = record.legibilityAcceptable === true
    ? '<span class="chip" data-kind="ok">legible</span>'
    : record.legibilityAcceptable === false
      ? '<span class="chip" data-kind="bad">legibilidad en riesgo</span>'
      : "";

  el("optimal-chips").innerHTML = `
    <span class="chip" data-kind="accent">${record.convertedWidth}x${record.convertedHeight}</span>
    <span class="chip">q${record.optimizedQuality ?? record.quality ?? "-"}</span>
    <span class="chip">escala ${Number(record.optimizedScale ?? 0).toFixed(2)}</span>
    <span class="chip">${record.candidatesEvaluated} candidato(s)</span>
    <span class="chip">score ${Number(record.qualityScore ?? 0).toFixed(3)}</span>
    <span class="chip">${Number(record.convertedBytes / 1024).toFixed(1)} KB</span>
    <span class="chip">techo ${Number(record.pesoObjetivoKb ?? 0).toFixed(0)} KB</span>
    ${legible}`;

  panel.hidden = false;
}

function applyOptimalToPending() {
  if (!lastOptimized) return;

  const overrides = overridesFromOptions(lastOptimized.options);
  const pending = queue.filter(editableState);

  if (pending.length === 0) {
    toast("No hay imagenes pendientes en la cola.", "warn");
    return;
  }

  for (const item of pending) item.overrides = overrides;
  renderQueue();
  toast(`Valores optimos aplicados a ${pending.length} imagen(es).`, "info");
}

function saveOptimalAsProfile() {
  if (!lastOptimized) return;

  // Deja los controles en el estado que produjo el optimo y abre el dialogo de perfil.
  applyOptions(lastOptimized.options);
  pinOptions();
  void savePresetFromOptions();
}

/* ============================== Carpetas y portapapeles ============================== */

/* Acepta archivos sueltos y carpetas completas (recursivo) al arrastrar. */
async function handleDrop(transfer) {
  if (!transfer) return;

  const entries = Array.from(transfer.items || [])
    .map((item) => (item.webkitGetAsEntry ? item.webkitGetAsEntry() : null))
    .filter(Boolean);

  if (entries.length === 0) {
    if (transfer.files) addFiles(transfer.files);
    return;
  }

  const progress = el("dropzone-progress");
  const files = [];

  progress.hidden = false;
  progress.textContent = "Leyendo carpetas...";

  try {
    for (const entry of entries) {
      await collectEntry(entry, files, (count) => {
        progress.textContent = `Leyendo ${count} archivo(s)...`;
      });
    }

    if (files.length === 0) {
      progress.textContent = "";
      toast("La carpeta no contiene imagenes.", "warn");
      return;
    }

    const folders = entries.filter((entry) => entry.isDirectory).length;
    progress.textContent = "";
    progress.hidden = true;
    addFiles(files);
    toast(
      folders > 0
        ? `Carpeta${folders > 1 ? "s" : ""} anadida: ${files.length} imagen(es).`
        : `${files.length} imagen(es) anadidas.`,
      "info"
    );
  } catch (error) {
    progress.hidden = true;
    toast(`No se pudo leer el contenido arrastrado: ${error.message}`, "error");
  }
}

function collectEntry(entry, files, onProgress) {
  return new Promise((resolve) => {
    if (entry.isFile) {
      entry.file((file) => {
        if (looksLikeImage(file)) files.push(file);
        onProgress(files.length);
        resolve();
      }, resolve);
      return;
    }

    if (!entry.isDirectory) {
      resolve();
      return;
    }

    const reader = entry.createReader();
    const readBatch = () => {
      reader.readEntries(async (batch) => {
        if (batch.length === 0) {
          onProgress(files.length);
          resolve();
          return;
        }

        for (const child of batch) {
          await collectEntry(child, files, onProgress);
        }
        readBatch();
      }, resolve);
    };

    readBatch();
  });
}

/* ============================== Reintentos y limpieza ============================== */

function retryFailed() {
  const failed = queue.filter((item) => item.state === "error");
  if (failed.length === 0) {
    toast("No hay imagenes con error.", "info");
    return;
  }

  for (const item of failed) {
    item.state = "pending";
    item.error = null;
    item.percent = 0;
    item.phase = "";
    item.record = null;
  }

  renderQueue();
  toast(`Reintentando ${failed.length} imagen(es).`, "info");
  void convertQueue();
}

function clearFinished() {
  const removable = queue.filter((item) => ["done", "cancelled", "error"].includes(item.state));
  if (removable.length === 0) {
    toast("No hay imagenes terminadas.", "info");
    return;
  }

  for (const item of removable) URL.revokeObjectURL(item.previewUrl);
  const remaining = queue.filter((item) => !removable.includes(item));
  queue.length = 0;
  queue.push(...remaining);

  renderQueue();
  toast(`Se quitaron ${removable.length} imagen(es) de la cola.`, "info");
}

/* ============================== Presets ============================== */

function currentPresets() {
  const conversion = state.settings.values.conversion || {};
  return Array.isArray(conversion.presets) ? conversion.presets : [];
}

function presetByName(name) {
  return currentPresets().find((preset) => (preset.name || "").toLowerCase() === String(name).toLowerCase());
}

function renderPresetList(selected = null) {
  const select = el("preset-select");
  const previous = selected ?? select.value;
  const presets = currentPresets();

  select.innerHTML = "";
  const empty = document.createElement("option");
  empty.value = "";
  empty.textContent = "(sin preset)";
  select.appendChild(empty);

  presets.forEach((preset) => {
    const option = document.createElement("option");
    option.value = preset.name;
    const parts = [preset.format || "webp", preset.quality ? `q${preset.quality}` : null];
    if (preset.pesoObjetivo) parts.push(`${preset.pesoObjetivo}KB`);
    if (preset.optimize) parts.push("opt");
    if (preset.allowLossy === false) parts.push("sin perdida");
    option.textContent = `${preset.name} (${parts.filter(Boolean).join(" ")})`;
    select.appendChild(option);
  });

  select.value = presets.some((preset) => preset.name === previous) ? previous : "";
  onPresetSelected();
}

function onPresetSelected() {
  const name = el("preset-select").value;
  el("preset-delete").disabled = !name;
  const preset = name ? presetByName(name) : null;
  el("preset-status").textContent = preset ? describePreset(preset) : "";
}

function describePreset(preset) {
  const parts = [];
  if (preset.engine) parts.push(`motor=${preset.engine}`);
  if (preset.format) parts.push(`formato=${preset.format}`);
  if (preset.quality) parts.push(`calidad=${preset.quality}`);
  if (preset.maxWidth) parts.push(`ancho=${preset.maxWidth}`);
  if (preset.maxHeight) parts.push(`alto=${preset.maxHeight}`);
  if (preset.collection) parts.push(`coleccion=${preset.collection}`);
  if (preset.removeMetadata === false) parts.push("conservar metadatos");
  return parts.join(", ");
}

function applyPresetToOptions(preset) {
  pinOptions();
  applyOptions({
    engine: preset.engine,
    format: preset.format,
    quality: preset.quality,
    maxWidth: preset.maxWidth ?? null,
    maxHeight: preset.maxHeight ?? null,
    removeMetadata: preset.removeMetadata !== false,
    collection: preset.collection ?? null,
    profile: preset.profile ?? null,
    pesoObjetivo: preset.pesoObjetivo ?? null,
    allowLossy: preset.allowLossy !== false,
    optimize: Boolean(preset.optimize)
  });
}

function applyPresetToAll() {
  const name = el("preset-select").value;
  const preset = name ? presetByName(name) : null;
  if (!preset) {
    toast("Selecciona un preset primero.", "warn");
    return;
  }

  applyPresetToOptions(preset);
  const pending = queue.filter(editableState);
  pending.forEach((item) => {
    item.overrides = presetToOverrides(preset);
  });

  renderQueue();
  toast(`Preset "${name}" aplicado a ${pending.length} imagen(es).`, "info");
}

function applyPresetToSelected() {
  const name = el("preset-select").value;
  const preset = name ? presetByName(name) : null;
  if (!preset) {
    toast("Selecciona un preset primero.", "warn");
    return;
  }

  const selected = queue.filter((item) => item.selected && editableState(item));
  if (selected.length === 0) {
    toast("Marca al menos una imagen pendiente.", "warn");
    return;
  }

  selected.forEach((item) => {
    item.overrides = presetToOverrides(preset);
  });

  renderQueue();
  toast(`Preset "${name}" aplicado a ${selected.length} imagen(es).`, "info");
}

function presetToOverrides(preset) {
  return overridesFromOptions(preset);
}

/** Convierte las opciones actuales en un preset con nombre (formato de ajustes). */
function readOptionsAsPreset(name) {
  const options = readOptions();
  return {
    name,
    engine: options.engine,
    format: options.format,
    quality: options.quality,
    maxWidth: options.maxWidth,
    maxHeight: options.maxHeight,
    removeMetadata: options.removeMetadata,
    collection: options.collection,
    profile: options.profile,
    pesoObjetivo: options.pesoObjetivo,
    allowLossy: options.allowLossy,
    optimize: options.optimize
  };
}

async function persistPresets(presets, message) {
  try {
    await fetchJson("/api/settings/user", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ conversion: { presets } })
    });

    await loadServerSettingsAndRefresh();
    renderPresetList(message ? presets[presets.length - 1]?.name : null);
    el("preset-status").textContent = message || "";
    toast(message || "Presets actualizados.", "info");
  } catch (error) {
    toast(`No se pudo guardar el preset: ${error.message}`, "error");
  }
}

async function loadServerSettingsAndRefresh() {
  try {
    state.settings = await fetchJson("/api/settings/effective");
    document.dispatchEvent(new CustomEvent("suca:settings"));
  } catch { /* mantiene la vista actual */ }
}

async function savePresetFromOptions() {
  const name = await promptDialog({
    title: "Guardar perfil de conversion",
    label: "Nombre del perfil",
    message: "Se guarda con los valores actuales (motor, formato, calidad, perfil A-D, peso y optimizacion). Si el nombre ya existe, se actualiza.",
    value: currentPresets().length === 0 ? "Mi perfil" : "",
    placeholder: "ej. Web 80 KB legible",
    confirmLabel: "Guardar perfil"
  });

  if (!name) return;

  const presets = [...currentPresets()];
  const existingIndex = presets.findIndex((preset) => (preset.name || "").toLowerCase() === name.toLowerCase());

  if (existingIndex >= 0) {
    presets[existingIndex] = readOptionsAsPreset(name);
    await persistPresets(presets, `Preset "${name}" actualizado.`);
  } else {
    presets.push(readOptionsAsPreset(name));
    await persistPresets(presets, `Preset "${name}" guardado.`);
  }
}

async function deletePreset() {
  const name = el("preset-select").value;
  const preset = name ? presetByName(name) : null;
  if (!preset) return;

  const confirmed = await confirmDialog({
    title: "Borrar perfil",
    message: `Se eliminara el perfil "${name}". Las conversiones ya hechas no cambian.`,
    confirmLabel: "Borrar perfil",
    danger: true
  });

  if (!confirmed) return;

  const presets = currentPresets().filter((item) => (item.name || "").toLowerCase() !== name.toLowerCase());
  await persistPresets(presets, `Preset "${name}" borrado.`);
}
