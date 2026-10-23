import {
  state, el, escapeHtml, formatBytes, toast,
  fetchJson, copyTextToClipboard, settingsReady
} from "../main.js";
import { openOverlay, closeOverlay, enableListNavigation } from "../a11y.js";

let initialized = false;
let activeCollection = "";
let detailId = null;
let lastItems = [];
const selectedIds = new Set();
let currentPage = 1;
let loadedItems = [];

export function initGalleryView() {
  if (initialized) return;
  initialized = true;

  el("refresh-gallery-button").addEventListener("click", () => void loadGallery());
  el("gallery-load-more").addEventListener("click", () => void loadGallery(true));
  el("reveal-storage-button").addEventListener("click", () => void revealStorage());
  el("copy-paths-button").addEventListener("click", () => void copyVisiblePaths());
  el("create-collection-button").addEventListener("click", () => void createCollection());

  // Buscador, orden y vistas
  let searchTimer = 0;
  el("gallery-search").addEventListener("input", () => {
    window.clearTimeout(searchTimer);
    searchTimer = window.setTimeout(() => renderGrid(applyFiltersAndSort(loadedItems)), 200);
  });
  el("gallery-sort").addEventListener("change", () => renderGrid(applyFiltersAndSort(loadedItems)));

  el("view-grid").addEventListener("click", () => {
    localStorage.setItem("suca.gallery.view", "grid");
    applyViewState();
    renderGrid(applyFiltersAndSort(loadedItems));
  });
  el("view-list").addEventListener("click", () => {
    localStorage.setItem("suca.gallery.view", "list");
    applyViewState();
    renderGrid(applyFiltersAndSort(loadedItems));
  });
  el("density-toggle").addEventListener("click", () => {
    const next = document.body.getAttribute("data-gallery-compact") !== "true";
    localStorage.setItem("suca.gallery.compact", String(next));
    applyViewState();
  });

  // Seleccion en lote
  el("select-page-button").addEventListener("click", () => {
    applyFiltersAndSort(loadedItems).forEach((item) => selectedIds.add(item.id));
    renderGrid(applyFiltersAndSort(loadedItems));
  });
  el("selection-clear").addEventListener("click", clearSelection);
  el("selection-download-zip").addEventListener("click", () => void downloadSelectionZip());
  el("selection-copy-paths").addEventListener("click", () => void copySelectionPaths());
  el("selection-move").addEventListener("click", () => void moveSelection());
  el("selection-delete").addEventListener("click", () => void deleteSelection());

  // Comparador y lightbox
  el("compare-close").addEventListener("click", () => closeOverlay(el("compare-overlay")));
  el("compare-range").addEventListener("input", onCompareRange);
  el("lightbox-close").addEventListener("click", () => closeOverlay(el("lightbox-overlay")));
  el("lightbox-prev").addEventListener("click", () => moveLightbox(-1));
  el("lightbox-next").addEventListener("click", () => moveLightbox(1));

  // Navegacion con teclado dentro de la galeria.
  document.addEventListener("keydown", (event) => {
    if (event.target instanceof Element && event.target.matches("input, textarea, select")) return;
    if (!el("lightbox-overlay").hidden) {
      if (event.key === "ArrowLeft") moveLightbox(-1);
      if (event.key === "ArrowRight") moveLightbox(1);
    }
  });
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
      await settingsReady;
      await loadGallery();
      await openDetail(requested);
    })();
    return;
  }

  // La galeria depende de web.galleryPageSize: espera a que los ajustes esten
  // cargados para no traer todos los registros y luego recargar.
  void settingsReady.then(() => loadGallery());
  enableListNavigation(el("gallery-grid"), ".gallery-card");
}

async function loadGallery(append = false) {
  try {
    const pageSize = Number(state.settings.values.web?.galleryPageSize) || 24;
    const url = activeCollection
      ? `/api/web/images?collection=${encodeURIComponent(activeCollection)}&page=${append ? currentPage + 1 : 1}&pageSize=${pageSize}`
      : `/api/web/images?page=${append ? currentPage + 1 : 1}&pageSize=${pageSize}`;
    const data = await fetchJson(url);
    const items = data.items || [];
    const total = Number(data.total) || items.length;

    if (!append) {
      currentPage = 1;
      loadedItems = items;
    } else {
      currentPage += 1;
      // Deduplica por id: si la pagina llega repetida no inflamos la lista.
      const known = new Set(loadedItems.map((item) => item.id));
      loadedItems = loadedItems.concat(items.filter((item) => !known.has(item.id)));
    }

    renderCollections();
    applyViewState();
    renderGrid(applyFiltersAndSort(loadedItems));

    const remaining = Math.max(0, total - loadedItems.length);
    el("gallery-load-more").hidden = remaining === 0;
    el("gallery-load-more").textContent = `Cargar mas (${remaining} restantes)`;
  } catch (error) {
    toast(`No se pudo cargar la galeria: ${error.message}`, "error");
  }
}

/* ============================== Busqueda, orden y vistas ============================== */

function applyFiltersAndSort(items) {
  const query = el("gallery-search").value.trim().toLowerCase();
  let filtered = query
    ? items.filter((item) => (item.originalFileName || "").toLowerCase().includes(query))
    : items.slice();

  const sort = el("gallery-sort").value;
  filtered.sort((a, b) => {
    switch (sort) {
      case "saving":
        return (a.savingPercent || 0) - (b.savingPercent || 0);
      case "size":
        return (b.convertedBytes || 0) - (a.convertedBytes || 0);
      case "name":
        return String(a.originalFileName || "").localeCompare(String(b.originalFileName || ""));
      default:
        return new Date(b.createdAtUtc || 0) - new Date(a.createdAtUtc || 0);
    }
  });

  return filtered;
}

function applyViewState() {
  const body = document.body;
  const view = localStorage.getItem("suca.gallery.view") || "grid";
  const compact = localStorage.getItem("suca.gallery.compact") === "true";

  body.setAttribute("data-gallery-view", view);
  body.setAttribute("data-gallery-compact", String(compact));

  el("view-grid").setAttribute("aria-pressed", String(view === "grid"));
  el("view-grid").setAttribute("data-active", String(view === "grid"));
  el("view-list").setAttribute("aria-pressed", String(view === "list"));
  el("view-list").setAttribute("data-active", String(view === "list"));
  el("density-toggle").setAttribute("aria-pressed", String(compact));
}

function renderGrid(items) {
  const grid = el("gallery-grid");
  grid.innerHTML = "";
  lastItems = items;

  el("gallery-empty").hidden = items.length > 0;
  el("gallery-count").textContent = items.length === 0
    ? ""
    : `${items.length} imagen(es)${activeCollection ? ` en ${activeCollection}` : ""}`;

  for (const record of items) {
    const card = document.createElement("article");
    card.className = "gallery-card";
    card.setAttribute("tabindex", "0");
    card.setAttribute("data-nav-item", "true");
    card.setAttribute("data-id", record.id);
    if (selectedIds.has(record.id)) card.setAttribute("data-selected", "true");
    card.setAttribute("aria-label", record.originalFileName);
    card.innerHTML = `
      <div class="g-select">
        <input type="checkbox" data-select="${record.id}"${selectedIds.has(record.id) ? " checked" : ""} aria-label="Seleccionar ${escapeHtml(record.originalFileName)}">
      </div>
      <img src="/api/web/images/${record.id}/file?variant=converted" alt="${escapeHtml(record.originalFileName)}" data-open="${record.id}" loading="lazy" decoding="async">
      <div class="g-body">
        <div class="g-name">${escapeHtml(record.originalFileName)}</div>
        <div class="chip-row">
          <span class="chip" data-kind="accent">${escapeHtml(String(record.format).toUpperCase())}</span>
          <span class="chip" data-kind="ok">-${Math.abs(record.savingPercent).toFixed(1)}%</span>
          <span class="chip">${escapeHtml(record.collection)}</span>
          ${record.integritySsim === null || record.integritySsim === undefined
            ? ""
            : record.integrityBitwiseEqual
              ? '<span class="chip" data-kind="ok" title="Sin perdida: SHA-256 identico">sin perdida</span>'
              : `<span class="chip" data-kind="${record.integrityVerdict === "ACEPTABLE ESTRUCTURALMENTE" ? "ok" : "warn"}" title="${escapeHtml(record.integrityVerdict ?? "")}">SSIM ${Number(record.integritySsim).toFixed(2)}</span>`}
        </div>
        <div class="g-meta">${formatBytes(record.originalBytes)} &rarr; ${formatBytes(record.convertedBytes)} \u00b7 ${escapeHtml(new Date(record.createdAtUtc).toLocaleString())}</div>
        <div class="btn-row">
          <button type="button" class="btn" data-copy="${record.id}">Base64</button>
          <button type="button" class="btn" data-path="${record.id}" data-tip-title="Copiar ruta" data-tip="Copia la ruta completa del archivo convertido en disco." data-tip-pos="top" tabindex="0" role="note">Ruta</button>
          <button type="button" class="btn" data-reveal="${record.id}">Abrir</button>
          <button type="button" class="btn" data-compare="${record.id}">Comparar</button>
          <button type="button" class="btn" data-zoom="${record.id}">Ampliar</button>
          <a class="btn" href="/api/web/images/${record.id}/file?variant=converted" download>Descargar</a>
          <button type="button" class="btn btn-danger" data-delete="${record.id}">Eliminar</button>
        </div>
      </div>`;

    grid.appendChild(card);
  }

  grid.querySelectorAll("img[data-open]").forEach((img) => {
    img.addEventListener("click", () => void openDetail(img.dataset.open));
  });
  grid.querySelectorAll("input[data-select]").forEach((input) => {
    input.addEventListener("change", () => {
      const id = input.dataset.select;
      if (input.checked) {
        selectedIds.add(id);
      } else {
        selectedIds.delete(id);
      }
      input.closest(".gallery-card")?.setAttribute("data-selected", String(input.checked));
      renderSelectionBar();
    });
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
  grid.querySelectorAll("button[data-compare]").forEach((button) => {
    button.addEventListener("click", () => void openCompare(button.dataset.compare));
  });
  grid.querySelectorAll("button[data-zoom]").forEach((button) => {
    button.addEventListener("click", () => openLightbox(button.dataset.zoom));
  });
  grid.querySelectorAll("button[data-delete]").forEach((button) => {
    button.addEventListener("click", () => void deleteImage(button.dataset.delete));
  });

  renderSelectionBar();
}

/* ============================== Seleccion en lote ============================== */

function renderSelectionBar() {
  const bar = el("selection-bar");
  bar.hidden = selectedIds.size === 0;
  el("selection-count").textContent = `${selectedIds.size} seleccionada(s)`;
}

function clearSelection() {
  selectedIds.clear();
  document.querySelectorAll("#gallery-grid input[data-select]").forEach((input) => {
    input.checked = false;
  });
  document.querySelectorAll("#gallery-grid .gallery-card").forEach((card) => {
    card.removeAttribute("data-selected");
  });
  renderSelectionBar();
}

async function downloadSelectionZip() {
  const ids = Array.from(selectedIds);
  if (ids.length === 0) return;

  toast(`Generando ZIP de ${ids.length} imagen(es)...`, "info");
  try {
    const response = await fetch("/api/web/images/export", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ids, variant: "converted" })
    });

    if (!response.ok) {
      const body = await response.json().catch(() => null);
      throw new Error(body?.error || `HTTP ${response.status}`);
    }

    const blob = await response.blob();
    const disposition = response.headers.get("content-disposition") || "";
    const match = disposition.match(/filename\*=UTF-8''([^;]+)/i) || disposition.match(/filename=([^;]+)/i);
    const fileName = match ? decodeURIComponent(match[1].trim().replace(/"/g, "")) : "suca.zip";

    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    link.click();
    URL.revokeObjectURL(url);

    toast(`ZIP descargado: ${fileName}`, "info");
  } catch (error) {
    toast(`No se pudo generar el ZIP: ${error.message}`, "error");
  }
}

async function copySelectionPaths() {
  const ids = Array.from(selectedIds);
  if (ids.length === 0) return;

  try {
    const paths = await fetchJson("/api/web/images/paths", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ids, variant: "converted" })
    });

    const list = Array.isArray(paths) ? paths : paths.paths || [];
    const text = list.map((item) => item.path).join("\n");
    const ok = await copyTextToClipboard(text);
    toast(ok ? `Rutas copiadas (${list.length}).` : "No se pudo copiar al portapapeles.", ok ? "info" : "error");
  } catch (error) {
    toast(`No se pudieron copiar las rutas: ${error.message}`, "error");
  }
}

async function moveSelection() {
  const ids = Array.from(selectedIds);
  const collection = el("selection-move-name").value.trim();
  if (ids.length === 0 || !collection) {
    toast("Selecciona imagenes y escribe la coleccion destino.", "warn");
    return;
  }

  try {
    const result = await fetchJson("/api/web/images/collection", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ids, collection })
    });

    toast(`${result.moved} imagen(es) movidas a ${result.collection}.`, "info");
    el("selection-move-name").value = "";
    clearSelection();
    await loadGallery();
  } catch (error) {
    toast(`No se pudo mover: ${error.message}`, "error");
  }
}

async function deleteSelection() {
  const ids = Array.from(selectedIds);
  if (ids.length === 0) return;
  if (!window.confirm(`Eliminar ${ids.length} imagen(es) de la galeria? Esta accion no se puede deshacer.`)) return;

  let removed = 0;
  for (const id of ids) {
    const response = await fetch(`/api/web/images/${id}`, { method: "DELETE" });
    if (response.ok || response.status === 404) removed += 1;
  }

  toast(`${removed} imagen(es) eliminadas.`, "info");
  clearSelection();
  await loadGallery();
}

/* ============================== Comparador ============================== */

async function openCompare(id) {
  const record = lastItems.find((item) => item.id === id);
  if (!record) return;

  el("compare-original").src = `/api/web/images/${id}/file?variant=original`;
  el("compare-converted").src = `/api/web/images/${id}/file?variant=converted`;
  el("compare-range").value = "50";
  el("compare-original").style.clipPath = "inset(0 50% 0 0)";
  el("compare-metrics").textContent =
    `${record.originalWidth}x${record.originalHeight} -> ${record.convertedWidth}x${record.convertedHeight} \u00b7 ` +
    `${formatBytes(record.originalBytes)} -> ${formatBytes(record.convertedBytes)} \u00b7 -${Math.abs(record.savingPercent).toFixed(1)}%`;

  openOverlay(el("compare-overlay"));
}

function onCompareRange() {
  const value = Number(el("compare-range").value);
  // El deslizador recorta la original por la derecha para revelar la convertida.
  el("compare-original").style.clipPath = `inset(0 ${100 - value}% 0 0)`;
}

/* ============================== Lightbox ============================== */

let lightboxIndex = -1;

function openLightbox(id) {
  if (lastItems.length === 0) return;
  const index = lastItems.findIndex((item) => item.id === id);
  lightboxIndex = index >= 0 ? index : 0;
  paintLightbox();
  openOverlay(el("lightbox-overlay"));
}

function paintLightbox() {
  const record = lastItems[lightboxIndex];
  if (!record) return;

  el("lightbox-image").src = `/api/web/images/${record.id}/file?variant=converted`;
  el("lightbox-image").alt = record.originalFileName;
  el("lightbox-caption").textContent =
    `${record.originalFileName} \u00b7 ${record.originalWidth}x${record.originalHeight} \u00b7 ` +
    `${formatBytes(record.convertedBytes)} \u00b7 -${Math.abs(record.savingPercent).toFixed(1)}% \u00b7 ` +
    `${lightboxIndex + 1}/${lastItems.length}`;
}

function moveLightbox(step) {
  if (lastItems.length === 0) return;
  lightboxIndex = (lightboxIndex + step + lastItems.length) % lastItems.length;
  paintLightbox();
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

  // Integridad perceptual: la prueba medible de "cuanta calidad se conservo".
  const integrity = el("detail-integrity-chips");
  if (integrity) {
    if (record.integritySsim === null || record.integritySsim === undefined) {
      integrity.innerHTML = '<span class="chip">sin reporte (motor directo)</span>';
    } else if (record.integrityBitwiseEqual) {
      integrity.innerHTML = '<span class="chip" data-kind="ok">sin perdida: SHA-256 identico</span>';
    } else {
      const ok = record.integrityVerdict === "ACEPTABLE ESTRUCTURALMENTE";
      integrity.innerHTML = `
        <span class="chip" data-kind="${ok ? "ok" : "warn"}">${escapeHtml(record.integrityVerdict ?? "")}</span>
        <span class="chip">SSIM ${Number(record.integritySsim).toFixed(3)}</span>
        <span class="chip">PSNR ${Number(record.integrityPsnr ?? 0).toFixed(1)} dB</span>
        <span class="chip">bordes ${Number(record.integrityEdgeRetention ?? 0).toFixed(2)}</span>
        <span class="chip">histograma ${Number(record.integrityHistogram ?? 0).toFixed(2)}</span>
        <span class="chip">${escapeHtml(record.integrityMode ?? "")}</span>`;
    }
  }

  el("detail-download").href = `/api/web/images/${id}/file?variant=converted`;
  el("detail-download-original").href = `/api/web/images/${id}/file?variant=original`;
  el("base64-preview").hidden = true;
  el("base64-preview").textContent = "";
  openOverlay(el("detail-overlay"), () => {
    detailId = null;
  });
}

function closeDetail() {
  closeOverlay(el("detail-overlay"), () => {
    detailId = null;
  });
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
      // El servidor devuelve la ruta cuando no puede abrir el Explorador: la
      // copiamos para que el usuario pueda abrirla a mano.
      if (body.path) {
        await copyTextToClipboard(body.path);
        toast(`No se pudo abrir el Explorador. Ruta copiada al portapapeles: ${body.path}`, "warn", 9000);
        return;
      }
      throw new Error(body.error || `HTTP ${response.status}`);
    }
    toast("Archivo abierto en el Explorador.", "info");
  } catch (error) {
    toast(`No se pudo abrir el Explorador: ${error.message}`, "error", 8000);
  }
}

async function revealStorage() {
  try {
    const response = await fetch("/api/web/storage/reveal", { method: "POST" });
    if (!response.ok) {
      const body = await response.json().catch(() => ({}));
      throw new Error(body.error || `HTTP ${response.status}`);
    }
    toast("Carpeta de imagenes convertidas abierta en el Explorador.", "info");
  } catch (error) {
    // Sin Explorador disponible: al menos dejamos la ruta a mano.
    try {
      const info = await fetchJson("/api/web/images?page=1&pageSize=1");
      if (info.items && info.items[0]) {
        const path = await fetchJson(`/api/web/images/${info.items[0].id}/path?variant=converted`);
        if (path?.path) {
          await copyTextToClipboard(path.path);
          toast(`Explorador no disponible. Ruta copiada: ${path.path}`, "warn", 9000);
          return;
        }
      }
    } catch { /* sin datos: se informa el error original */ }

    toast(`No se pudo abrir la carpeta: ${error.message}`, "error", 8000);
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

