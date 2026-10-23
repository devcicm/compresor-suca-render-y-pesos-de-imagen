import {
  state, el, escapeHtml, formatBytes, toast,
  fetchJson, copyTextToClipboard
} from "../main.js";

let initialized = false;
let activeCollection = "";
let detailId = null;
let lastItems = [];

export function initGalleryView() {
  if (initialized) return;
  initialized = true;

  el("refresh-gallery-button").addEventListener("click", () => void loadGallery());
  el("reveal-storage-button").addEventListener("click", () => void revealStorage());
  el("copy-paths-button").addEventListener("click", () => void copyVisiblePaths());
  el("create-collection-button").addEventListener("click", () => void createCollection());
  el("detail-close").addEventListener("click", closeDetail);
  el("detail-overlay").addEventListener("click", (event) => {
    if (event.target === el("detail-overlay")) closeDetail();
  });
  el("copy-base64-converted").addEventListener("click", () => void copyBase64(detailId, "converted"));
  el("copy-base64-original").addEventListener("click", () => void copyBase64(detailId, "original"));
  el("detail-copy-path").addEventListener("click", () => void copyPath(detailId, "converted"));
  el("detail-reveal").addEventListener("click", () => void revealFile(detailId, "converted"));
  el("show-base64").addEventListener("click", () => void showBase64Preview());
  el("detail-delete").addEventListener("click", () => void deleteFromDetail());

  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") closeDetail();
  });

  document.addEventListener("suca:gallery-dirty", () => {
    void loadGallery();
  });

  // Apertura profunda: /studio/galeria.html?detalle={id}
  const requested = new URLSearchParams(window.location.search).get("detalle");
  if (requested) {
    void (async () => {
      await loadGallery();
      await openDetail(requested);
    })();
    return;
  }

  void loadGallery();
}

async function loadGallery() {
  try {
    const url = activeCollection
      ? `/api/web/images?collection=${encodeURIComponent(activeCollection)}`
      : "/api/web/images";
    const data = await fetchJson(url);
    renderCollections();
    renderGrid(data.items || []);
  } catch (error) {
    toast(`No se pudo cargar la galeria: ${error.message}`, "error");
  }
}

function renderCollections() {
  void (async () => {
    try {
      state.collections = await fetchJson("/api/web/collections");
    } catch { /* mantiene lo previo */ }

    const chips = el("collection-chips");
    chips.innerHTML = "";

    const all = document.createElement("button");
    all.type = "button";
    all.className = "chip";
    all.setAttribute("data-kind", "selectable");
    all.setAttribute("aria-pressed", String(activeCollection === ""));
    all.textContent = `Todas (${state.collections.reduce((sum, c) => sum + c.total, 0)})`;
    all.addEventListener("click", () => {
      activeCollection = "";
      void loadGallery();
    });
    chips.appendChild(all);

    for (const entry of state.collections) {
      const chip = document.createElement("button");
      chip.type = "button";
      chip.className = "chip";
      chip.setAttribute("data-kind", "selectable");
      chip.setAttribute("aria-pressed", String(activeCollection === entry.collection));
      chip.textContent = `${entry.collection} (${entry.total})`;
      chip.title = `${formatBytes(entry.originalBytes)} -> ${formatBytes(entry.convertedBytes)} (-${entry.savingPercent.toFixed(1)}%)`;
      chip.addEventListener("click", () => {
        activeCollection = activeCollection === entry.collection ? "" : entry.collection;
        void loadGallery();
      });
      chips.appendChild(chip);
    }

    renderCollectionsAdmin();
  })();
}

function renderCollectionsAdmin() {
  const container = el("collections-admin-list");
  if (!container) return;
  container.innerHTML = "";

  for (const entry of state.collections) {
    const isDefault = entry.collection === "general";
    const row = document.createElement("div");
    row.className = "order-row";
    row.innerHTML = `
      <span class="chip" data-kind="accent">${escapeHtml(entry.collection)}</span>
      <span>${entry.total} imagen(es)</span>
      <span class="muted-note">${formatBytes(entry.originalBytes)} -&gt; ${formatBytes(entry.convertedBytes)} (-${entry.savingPercent.toFixed(1)}%)</span>`;

    const renameInput = document.createElement("input");
    renameInput.type = "text";
    renameInput.placeholder = "nuevo nombre";
    renameInput.disabled = isDefault;
    renameInput.className = "collection-rename-input";
    row.appendChild(renameInput);

    const renameBtn = document.createElement("button");
    renameBtn.type = "button";
    renameBtn.className = "btn";
    renameBtn.textContent = "Renombrar";
    renameBtn.disabled = isDefault;
    if (isDefault) renameBtn.title = "La coleccion general no se puede renombrar";
    renameBtn.addEventListener("click", () => void renameCollection(entry.collection, renameInput.value.trim()));
    row.appendChild(renameBtn);

    const deleteBtn = document.createElement("button");
    deleteBtn.type = "button";
    deleteBtn.className = "btn btn-danger";
    deleteBtn.textContent = "Disolver";
    deleteBtn.disabled = isDefault;
    if (isDefault) deleteBtn.title = "La coleccion general no se puede disolver";
    deleteBtn.addEventListener("click", () => void dissolveCollection(entry.collection));
    row.appendChild(deleteBtn);

    container.appendChild(row);
  }

  if (state.collections.length === 0) {
    container.innerHTML = `<div class="empty-state">Sin colecciones. Cree una arriba o convierta imagenes.</div>`;
  }
}

async function createCollection() {
  const name = el("new-collection-name").value.trim();
  if (!name) {
    toast("Escriba un nombre para la coleccion.", "warn");
    return;
  }

  try {
    const response = await fetch("/api/web/collections", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name })
    });
    const body = await response.json();
    if (!response.ok) throw new Error(body.error || `HTTP ${response.status}`);

    el("new-collection-name").value = "";
    toast(`Coleccion '${body.collection}' creada.`, "info");
    await loadGallery();
  } catch (error) {
    toast(`No se pudo crear la coleccion: ${error.message}`, "error");
  }
}

async function renameCollection(currentName, newName) {
  if (!newName) {
    toast("Escriba el nuevo nombre en el campo junto al boton.", "warn");
    return;
  }
  if (!window.confirm(`Renombrar '${currentName}' a '${newName}'? Se actualizan todas sus imagenes.`)) return;

  try {
    const response = await fetch(`/api/web/collections/${encodeURIComponent(currentName)}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ newName })
    });
    const body = await response.json();
    if (!response.ok) throw new Error(body.error || `HTTP ${response.status}`);

    if (activeCollection === currentName) activeCollection = body.to;
    toast(`Coleccion renombrada a '${body.to}'.`, "info");
    await loadGallery();
  } catch (error) {
    toast(`No se pudo renombrar: ${error.message}`, "error");
  }
}

async function dissolveCollection(name) {
  if (!window.confirm(`Disolver '${name}'? Las imagenes pasan a 'general' y los archivos NO se borran.`)) return;

  try {
    const response = await fetch(`/api/web/collections/${encodeURIComponent(name)}?confirm=true`, { method: "DELETE" });
    const body = await response.json();
    if (!response.ok) throw new Error(body.error || `HTTP ${response.status}`);

    if (activeCollection === name) activeCollection = "";
    toast(`Coleccion disuelta: ${body.moved} imagen(es) movidas a general.`, "info");
    await loadGallery();
  } catch (error) {
    toast(`No se pudo disolver: ${error.message}`, "error");
  }
}

function renderGrid(items) {
  const grid = el("gallery-grid");
  grid.innerHTML = "";
  el("gallery-empty").hidden = items.length > 0;
  lastItems = items;

  for (const record of items) {
    const card = document.createElement("article");
    card.className = "gallery-card";
    card.innerHTML = `
      <img src="/api/web/images/${record.id}/file?variant=converted" alt="${escapeHtml(record.originalFileName)}" data-open="${record.id}">
      <div class="g-body">
        <div class="g-name">${escapeHtml(record.originalFileName)}</div>
        <div class="chip-row">
          <span class="chip" data-kind="accent">${escapeHtml(String(record.format).toUpperCase())}</span>
          <span class="chip" data-kind="ok">-${Math.abs(record.savingPercent).toFixed(1)}%</span>
          <span class="chip">${escapeHtml(record.collection)}</span>
        </div>
        <div class="g-meta">${formatBytes(record.originalBytes)} &rarr; ${formatBytes(record.convertedBytes)} · ${escapeHtml(new Date(record.createdAtUtc).toLocaleString())}</div>
        <div class="btn-row">
          <button type="button" class="btn" data-copy="${record.id}">Base64</button>
          <button type="button" class="btn" data-path="${record.id}" data-tip-title="Copiar ruta" data-tip="Copia la ruta completa del archivo convertido en disco." data-tip-pos="top" tabindex="0" role="note">Ruta</button>
          <button type="button" class="btn" data-reveal="${record.id}">Abrir</button>
          <a class="btn" href="/api/web/images/${record.id}/file?variant=converted" download>Descargar</a>
          <button type="button" class="btn btn-danger" data-delete="${record.id}">Eliminar</button>
        </div>
      </div>`;

    grid.appendChild(card);
  }

  grid.querySelectorAll("img[data-open]").forEach((img) => {
    img.addEventListener("click", () => void openDetail(img.dataset.open));
  });
  grid.querySelectorAll("button[data-copy]").forEach((button) => {
    button.addEventListener("click", () => void copyBase64(button.dataset.copy, "converted"));
  });
  grid.querySelectorAll("button[data-path]").forEach((button) => {
    button.addEventListener("click", () => void copyPath(button.dataset.path, "converted"));
  });
  grid.querySelectorAll("button[data-reveal]").forEach((button) => {
    button.addEventListener("click", () => void revealFile(button.dataset.reveal, "converted"));
  });
  grid.querySelectorAll("button[data-delete]").forEach((button) => {
    button.addEventListener("click", () => void deleteImage(button.dataset.delete));
  });
}

async function deleteImage(id) {
  if (!window.confirm("Eliminar esta imagen de la galeria?")) return;
  const response = await fetch(`/api/web/images/${id}`, { method: "DELETE" });
  if (response.ok || response.status === 404) {
    toast("Imagen eliminada.", "info");
    if (detailId === id) closeDetail();
    await loadGallery();
  }
}

async function deleteFromDetail() {
  if (!detailId) return;
  await deleteImage(detailId);
  closeDetail();
}

/* ============================== Detalle ============================== */

async function openDetail(id) {
  const record = await fetchJson(`/api/web/images/${id}`);
  detailId = id;
  el("detail-title").textContent = record.originalFileName;
  el("detail-image").src = `/api/web/images/${id}/file?variant=converted`;

  el("detail-input-chips").innerHTML = `
    <span class="chip">${escapeHtml(record.inputFormat || "imagen")}</span>
    <span class="chip">${record.originalWidth}&times;${record.originalHeight}</span>
    <span class="chip">${formatBytes(record.originalBytes)}</span>
    <span class="chip">${(record.originalWidth * record.originalHeight / 1e6).toFixed(2)} MPx</span>
    <span class="chip" data-kind="${record.inputHasExif ? "warn" : "ok"}">EXIF: ${record.inputHasExif ? "si" : "no"}</span>`;

  el("detail-output-chips").innerHTML = `
    <span class="chip" data-kind="accent">${escapeHtml(String(record.format).toUpperCase())}${record.quality ? ` q${record.quality}` : ""}</span>
    <span class="chip">${record.convertedWidth}&times;${record.convertedHeight}</span>
    <span class="chip">${formatBytes(record.convertedBytes)}</span>
    <span class="chip">${record.outputBitsPerPixel} bpp</span>
    <span class="chip">${record.outputCompressionRatio}:1</span>
    <span class="chip" data-kind="ok">-${Math.abs(record.savingPercent).toFixed(1)}%</span>
    <span class="chip">${record.durationMs} ms</span>
    <span class="chip">alpha: ${record.outputHasAlpha ? "si" : "no"}</span>`;

  el("detail-state-chips").innerHTML = `
    <span class="chip">motor: ${escapeHtml(record.engine)}</span>
    <span class="chip">estado: ${escapeHtml(record.state)}</span>
    <span class="chip">coleccion: ${escapeHtml(record.collection)}</span>
    ${record.sucaHash ? `<span class="chip">sucaHash: ${record.sucaHash}</span>` : ""}
    ${record.correlationId ? `<span class="chip cell-mono">corr: ${escapeHtml(record.correlationId)}</span>` : ""}
    <span class="chip">${escapeHtml(new Date(record.createdAtUtc).toLocaleString())}</span>`;

  el("detail-download").href = `/api/web/images/${id}/file?variant=converted`;
  el("detail-download-original").href = `/api/web/images/${id}/file?variant=original`;
  el("base64-preview").hidden = true;
  el("base64-preview").textContent = "";
  el("detail-overlay").hidden = false;
}

function closeDetail() {
  el("detail-overlay").hidden = true;
  detailId = null;
}

async function copyBase64(id, variant) {
  if (!id) return;
  try {
    const payload = await fetchJson(`/api/web/images/${id}/base64?variant=${variant}`);
    await copyTextToClipboard(payload.base64);
    toast(`Base64 (${variant}) copiado: ${formatBytes(payload.byteCount)}.`, "info");
  } catch (error) {
    toast(`No se pudo copiar el base64: ${error.message}`, "error");
  }
}

async function showBase64Preview() {
  if (!detailId) return;
  try {
    const payload = await fetchJson(`/api/web/images/${detailId}/base64?variant=converted`);
    const preview = el("base64-preview");
    const shortBase64 = payload.base64.length > 400
      ? `${payload.base64.slice(0, 400)}... (${payload.base64.length} caracteres)`
      : payload.base64;

    preview.textContent = JSON.stringify({
      id: payload.id,
      variant: payload.variant,
      fileName: payload.fileName,
      contentType: payload.contentType,
      byteCount: payload.byteCount,
      base64: shortBase64,
      dataUrl: `${payload.dataUrl.slice(0, 120)}...`
    }, null, 2);
    preview.hidden = false;
  } catch (error) {
    toast(`No se pudo leer el base64: ${error.message}`, "error");
  }
}

/* ============================== Rutas en disco ============================== */

async function copyPath(id, variant) {
  if (!id) return;
  try {
    const payload = await fetchJson(`/api/web/images/${id}/path?variant=${variant}`);
    await copyTextToClipboard(payload.path);
    toast(`Ruta copiada: ${payload.fileName}`, "info");
  } catch (error) {
    toast(`No se pudo copiar la ruta: ${error.message}`, "error");
  }
}

async function revealFile(id, variant) {
  try {
    const response = await fetch(`/api/web/images/${id}/reveal?variant=${variant}`, { method: "POST" });
    if (!response.ok) {
      const body = await response.json().catch(() => ({}));
      throw new Error(body.error || `HTTP ${response.status}`);
    }
    toast("Carpeta abierta en el Explorador.", "info");
  } catch (error) {
    toast(`No se pudo abrir la carpeta: ${error.message}`, "error");
  }
}

async function revealStorage() {
  try {
    const response = await fetch("/api/web/storage/reveal", { method: "POST" });
    if (!response.ok) {
      const body = await response.json().catch(() => ({}));
      throw new Error(body.error || `HTTP ${response.status}`);
    }
    toast("Carpeta de imagenes convertidas abierta.", "info");
  } catch (error) {
    toast(`No se pudo abrir la carpeta: ${error.message}`, "error");
  }
}

async function copyVisiblePaths() {
  const items = lastItems;
  if (!items.length) {
    toast("No hay imagenes visibles para copiar rutas.", "warn");
    return;
  }

  try {
    let paths;
    if (activeCollection) {
      const payload = await fetchJson(
        `/api/web/collections/${encodeURIComponent(activeCollection)}/paths?variant=converted`);
      paths = payload.items.map((item) => item.path);
    } else {
      const response = await fetch("/api/web/images/paths", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ids: items.map((item) => item.id), variant: "converted" })
      });
      const payload = await response.json();
      if (!response.ok) throw new Error(payload.error || `HTTP ${response.status}`);
      paths = payload.items.map((item) => item.path);
    }

    await copyTextToClipboard(paths.join("\n"));
    toast(`${paths.length} ruta(s) copiada(s) al portapapeles.`, "info");
  } catch (error) {
    toast(`No se pudieron copiar las rutas: ${error.message}`, "error");
  }
}
