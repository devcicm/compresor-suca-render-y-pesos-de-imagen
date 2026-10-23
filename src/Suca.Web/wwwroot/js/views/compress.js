import {
  state, el, escapeHtml, formatBytes, toast,
  fetchJson, uploadWithProgress, correlationId, copyTextToClipboard
} from "../main.js";

const queue = [];           // { key, file, previewUrl, state, percent, phase, record, error, cancelFn }
let converting = false;
let globalPaused = false;
let initialized = false;

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
    if (event.dataTransfer && event.dataTransfer.files) addFiles(event.dataTransfer.files);
  });

  el("convert-button").addEventListener("click", () => void convertQueue());
  el("clear-button").addEventListener("click", clearQueue);
  el("pause-all-button").addEventListener("click", toggleGlobalPause);
  el("select-all-button").addEventListener("click", toggleSelectAll);
  el("apply-to-selected-button").addEventListener("click", applyOptionsToSelected);
  el("remove-selected-button").addEventListener("click", removeSelected);

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

  el("quality").addEventListener("input", () => {
    el("quality-out").textContent = el("quality").value;
  });

  el("format").addEventListener("change", () => {
    const isPng = el("format").value === "png";
    el("quality").disabled = isPng;
  });

  el("order-submit").addEventListener("click", () => void submitOrder());

  document.addEventListener("suca:settings", applyDefaults);
  document.addEventListener("suca:collections", fillCollectionList);
  refreshCollectionList();
}

function applyDefaults() {
  const conversion = state.settings.values.conversion || {};
  const web = state.settings.values.web || {};

  if (conversion.engine && el("engine").querySelector(`option[value='${conversion.engine}']`)) {
    el("engine").value = conversion.engine;
  }
  if (conversion.format && el("format").querySelector(`option[value='${conversion.format}']`)) {
    el("format").value = conversion.format;
    el("format").dispatchEvent(new Event("change"));
  }
  if (typeof conversion.quality === "number") {
    el("quality").value = conversion.quality;
    el("quality-out").textContent = conversion.quality;
  }
  if (typeof conversion.removeMetadata === "boolean") {
    el("remove-metadata").checked = conversion.removeMetadata;
  }
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

  const editableCount = queue.filter(editableState).length;
  el("select-all-button").hidden = editableCount === 0;
  el("apply-to-selected-button").hidden = editableCount === 0;
  el("remove-selected-button").hidden = editableCount === 0;

  for (const item of queue) {
    const li = document.createElement("li");
    li.className = "queue-item";
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
    ? "Sin archivos en la cola."
    : `${total} archivo(s), ${pending} pendientes, ${done} completadas.`;
}

function stateLabel(state) {
  return {
    pending: "Pendiente",
    waiting: "En cola",
    uploading: "Subiendo",
    processing: "Procesando",
    done: "Lista",
    paused: "Pausada",
    error: "Error",
    cancelled: "Cancelada"
  }[state] || state;
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
  const ov = item.overrides || {};
  const form = new FormData();
  form.append("file", item.file, item.file.name);
  form.append("engine", ov.engine ?? el("engine").value);
  form.append("format", ov.format ?? el("format").value);
  form.append("quality", ov.quality ?? el("quality").value);
  form.append("removeMetadata", el("remove-metadata").checked ? "true" : "false");
  form.append("correlationId", correlation);

  const collection = ov.collection ?? el("collection").value.trim();
  if (collection) form.append("collection", collection);

  const maxWidth = ov.maxWidth ?? el("max-width").value;
  const maxHeight = ov.maxHeight ?? el("max-height").value;
  if (maxWidth) form.append("maxWidth", maxWidth);
  if (maxHeight) form.append("maxHeight", maxHeight);

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

  for (const item of selected) {
    item.overrides = {
      engine: el("engine").value,
      format: el("format").value,
      quality: Number(el("quality").value) || undefined,
      maxWidth: el("max-width").value ? Number(el("max-width").value) : undefined,
      maxHeight: el("max-height").value ? Number(el("max-height").value) : undefined,
      collection: el("collection").value.trim() || undefined
    };
  }

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
  el("folder-overlay").hidden = false;
  el("folder-error").textContent = "";
  void loadFolder("");
}

function closeFolderBrowser() {
  el("folder-overlay").hidden = true;
}

async function loadFolder(path) {
  const list = el("folder-items");
  list.innerHTML = '<div class="empty-state">Cargando...</div>';
  el("folder-error").textContent = "";

  try {
    const url = path
      ? `/api/web/folders?path=${encodeURIComponent(path)}`
      : "/api/web/folders";
    const data = await fetchJson(url);

    folderState.path = data.path || "";
    folderState.parent = data.parent;
    el("folder-current").textContent = data.mode === "drives"
      ? "Unidades disponibles"
      : (data.path || "(raiz)");

    list.innerHTML = "";
    if (data.items.length === 0) {
      list.innerHTML = '<div class="empty-state">Sin subcarpetas.</div>';
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
          ? `<span class="f-count">${entry.imageCount} imagen(es)</span>`
          : ""}`;
      row.addEventListener("click", () => void loadFolder(entry.path));
      row.addEventListener("keydown", (event) => {
        if (event.key === "Enter") void loadFolder(entry.path);
      });
      list.appendChild(row);
    }
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
