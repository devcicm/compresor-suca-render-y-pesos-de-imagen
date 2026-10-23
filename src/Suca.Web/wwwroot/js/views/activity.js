import { el, escapeHtml, toast, fetchJson } from "../main.js";
import { formatRelative } from "../i18n.js";
import { openOverlay, closeOverlay } from "../a11y.js";

let initialized = false;
let filterText = "";
let eventsView = localStorage.getItem("suca.activity.view") || "table";

export function initActivityView() {
  if (initialized) return;
  initialized = true;

  el("refresh-activity-button").addEventListener("click", () => void load());
  el("activity-filter").addEventListener("input", () => {
    filterText = el("activity-filter").value.trim().toLowerCase();
    void loadEvents();
  });

  el("events-table-view").addEventListener("click", () => setEventsView("table"));
  el("events-timeline-view").addEventListener("click", () => setEventsView("timeline"));
  el("order-detail-close").addEventListener("click", () => closeOverlay(el("order-detail-overlay")));

  applyEventsView();
  document.addEventListener("suca:orders-dirty", () => void load());
  void load();
}

function setEventsView(view) {
  eventsView = view;
  localStorage.setItem("suca.activity.view", view);
  applyEventsView();
  void loadEvents();
}

function applyEventsView() {
  const isTimeline = eventsView === "timeline";
  el("activity-rows").closest(".table-wrap").hidden = isTimeline;
  el("activity-timeline").hidden = !isTimeline;
  el("events-table-view").setAttribute("aria-pressed", String(!isTimeline));
  el("events-table-view").setAttribute("data-active", String(!isTimeline));
  el("events-timeline-view").setAttribute("aria-pressed", String(isTimeline));
  el("events-timeline-view").setAttribute("data-active", String(isTimeline));
}

async function load() {
  await Promise.all([loadOrders(), loadEvents()]);
}

async function loadOrders() {
  const container = el("orders-list");
  if (!container) return;

  try {
    const orders = await fetchJson("/api/web/orders");
    container.innerHTML = "";

    if (!orders.length) {
      container.innerHTML = `<div class="empty-state">Sin ordenes orquestadas. Envialas desde Compresor &gt; Orden orquestada, via MCP (convert-order) o consola (--order).</div>`;
      return;
    }

    for (const order of orders) {
      const row = document.createElement("div");
      row.className = "order-row";
      row.innerHTML = `
        <span class="badge" data-state="${escapeHtml(order.status)}">${escapeHtml(order.status)}${order.paused ? " (pausada)" : ""}</span>
        <button type="button" class="btn order-id-link" data-detail-order="${escapeHtml(order.orderId)}"
                title="Ver detalle de la orden">${escapeHtml(order.orderId)}</button>
        <span>${order.completed}/${order.totalItems} ok</span>
        ${order.failed ? `<span class="chip" data-kind="bad">${order.failed} fallos</span>` : ""}
        ${order.cancelled ? `<span class="chip" data-kind="warn">${order.cancelled} cancel.</span>` : ""}
        <span class="progress order-progress"><span class="progress-fill" style="width:${order.progressPercent}%"></span></span>
        <span>${order.progressPercent}%</span>`;

      row.querySelector("[data-detail-order]")?.addEventListener("click", () => void openOrderDetail(order.orderId));

      const pauseBtn = document.createElement("button");
      pauseBtn.type = "button";
      pauseBtn.className = "btn";
      pauseBtn.textContent = "Pausar";
      pauseBtn.addEventListener("click", () => void orderAction(order.orderId, "pause"));
      row.appendChild(pauseBtn);

      const resumeBtn = document.createElement("button");
      resumeBtn.type = "button";
      resumeBtn.className = "btn";
      resumeBtn.textContent = "Reanudar";
      resumeBtn.addEventListener("click", () => void orderAction(order.orderId, "resume"));
      row.appendChild(resumeBtn);

      const cancelBtn = document.createElement("button");
      cancelBtn.type = "button";
      cancelBtn.className = "btn btn-danger";
      cancelBtn.textContent = "Cancelar";
      cancelBtn.addEventListener("click", () => void orderAction(order.orderId, "cancel"));
      row.appendChild(cancelBtn);

      container.appendChild(row);
    }
  } catch (error) {
    toast(`No se pudieron cargar las ordenes: ${error.message}`, "error");
  }
}

async function orderAction(orderId, action) {
  try {
    const response = await fetch(`/api/web/orders/${orderId}/${action}`, { method: "POST" });
    if (!response.ok && response.status !== 202) {
      const body = await response.json().catch(() => ({}));
      throw new Error(body.error || `HTTP ${response.status}`);
    }
    toast(`Orden ${action}: ${orderId.slice(0, 8)}...`, "info");
    await load();
  } catch (error) {
    toast(`No se pudo ${action} la orden: ${error.message}`, "error");
  }
}

async function loadEvents() {
  const rows = el("activity-rows");
  if (!rows) return;

  try {
    const events = await fetchJson("/api/observability/events");
    const filtered = filterText
      ? events.filter((event) =>
          JSON.stringify(event).toLowerCase().includes(filterText))
      : events;

    renderEventsTable(filtered);
    renderTimeline(filtered);
  } catch (error) {
    toast(`No se pudieron cargar los eventos: ${error.message}`, "error");
  }
}

function renderEventsTable(events) {
  const rows = el("activity-rows");
  rows.innerHTML = "";

  for (const event of events.slice(0, 120)) {
    const tr = document.createElement("tr");
    tr.innerHTML = `
      <td class="cell-mono">${escapeHtml(new Date(event.timestampUtc).toLocaleTimeString())}</td>
      <td data-level="${escapeHtml(event.level)}">${escapeHtml(event.level)}</td>
      <td>${escapeHtml(event.name)}</td>
      <td>${escapeHtml(event.message)}</td>
      <td class="cell-mono">${escapeHtml(event.correlationId || "")}</td>`;
    rows.appendChild(tr);
  }

  if (events.length === 0) {
    rows.innerHTML = `<tr><td colspan="5"><div class="empty-state">Sin eventos que coincidan con el filtro.</div></td></tr>`;
  }
}

/* ============================== Timeline por correlacion ============================== */

/** Agrupa por correlacion base (los items cuelgan de .{itemId8}). */
function baseCorrelation(correlationId) {
  const value = String(correlationId || "sin-correlacion");
  const dot = value.indexOf(".");
  return dot === -1 ? value : value.slice(0, dot);
}

function renderTimeline(events) {
  const container = el("activity-timeline");
  if (!container) return;

  container.innerHTML = "";

  const groups = new Map();
  for (const event of events) {
    const key = baseCorrelation(event.correlationId);
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(event);
  }

  if (groups.size === 0) {
    container.innerHTML = `<div class="empty-state">Sin eventos que coincidan con el filtro.</div>`;
    return;
  }

  const timeOf = (event) => new Date(event.timestampUtc).getTime();
  const valid = events.filter((event) => Number.isFinite(timeOf(event)));
  const min = valid.length ? Math.min(...valid.map(timeOf)) : 0;
  const max = valid.length ? Math.max(...valid.map(timeOf)) : min + 1;
  const span = Math.max(1, max - min);

  for (const [key, group] of groups) {
    const sorted = group.slice().sort((a, b) => timeOf(a) - timeOf(b));
    const start = timeOf(sorted[0]);
    const end = timeOf(sorted[sorted.length - 1]);
    const offset = ((start - min) / span) * 100;
    const width = Math.max(2, ((end - start) / span) * 100);
    const failures = group.filter((event) => String(event.level).toLowerCase() === "error").length;
    const level = failures > 0 ? "error" : (group.some((e) => String(e.level).toLowerCase() === "warning") ? "warning" : "info");

    const section = document.createElement("section");
    section.className = "timeline-group";

    const header = document.createElement("header");
    header.className = "timeline-head";
    header.innerHTML = `
      <span class="cell-mono">${escapeHtml(key)}</span>
      <span class="muted-note">${group.length} evento(s)</span>
      <span class="muted-note">${escapeHtml(formatRelative(new Date(start)))}</span>
      <span class="chip" data-kind="${failures > 0 ? "bad" : "ok"}">${escapeHtml(level)}</span>`;

    const track = document.createElement("div");
    track.className = "timeline-track";

    for (const event of sorted) {
      const left = ((timeOf(event) - min) / span) * 100;
      const marker = document.createElement("span");
      marker.className = "timeline-marker";
      marker.setAttribute("data-level", escapeHtml(event.level));
      marker.style.left = `${Math.min(100, Math.max(0, left))}%`;
      marker.title = `${event.name} · ${event.message}`;
      track.appendChild(marker);
    }

    const bar = document.createElement("span");
    bar.className = "timeline-bar";
    bar.setAttribute("data-level", level);
    bar.style.left = `${offset}%`;
    bar.style.width = `${width}%`;
    bar.title = `${((end - start) / 1000).toFixed(0)} ms entre el primer y el ultimo evento`;
    track.appendChild(bar);

    section.appendChild(header);
    section.appendChild(track);
    container.appendChild(section);
  }
}

/* ============================== Detalle de orden ============================== */

/** El endpoint de detalle no incluye `status` (solo el resumen): se deriva de los items. */
function deriveOrderState(order) {
  const items = order.items || [];
  if (items.length === 0) return "pending";
  if (items.some((item) => String(item.state) === "Failed")) return "failed";
  if (items.some((item) => String(item.state) === "Cancelled")) return "cancelled";
  if (items.some((item) => String(item.state) === "Interrupted")) return "interrupted";
  if (items.every((item) => String(item.state) === "Completed")) return "completed";
  if (items.some((item) => ["Running", "Pending", "Queued"].includes(String(item.state)))) return "running";
  return "pending";
}

async function openOrderDetail(orderId) {
  try {
    const order = await fetchJson(`/api/web/orders/${encodeURIComponent(orderId)}`);
    const state = deriveOrderState(order);

    el("order-detail-title").textContent = `Orden ${order.orderId.slice(0, 8)}...`;
    el("order-detail-download").href = `/api/web/orders/${encodeURIComponent(order.orderId)}`;
    el("order-detail-download").setAttribute("download", `orden-${order.orderId}.json`);

    el("order-detail-meta").innerHTML = `
      <span class="chip" data-kind="${state === "completed" ? "ok" : state === "failed" ? "bad" : ""}">${escapeHtml(state)}</span>
      <span class="chip">correlacion: ${escapeHtml(order.correlationId)}</span>
      <span class="chip">${order.items?.length || 0} item(s)</span>
      <span class="chip">paralelismo: ${escapeHtml(String(order.parallelism))}</span>
      <span class="chip">salida: ${escapeHtml(order.outputMode)}</span>`;

    const items = el("order-detail-items");
    items.innerHTML = "";

    for (const item of order.items || []) {
      const card = document.createElement("article");
      card.className = "order-item";
      card.setAttribute("data-state", String(item.state || "").toLowerCase());

      const history = (item.stateHistory || [])
        .map((entry) => `<span class="chip">${escapeHtml(entry.state)} · ${escapeHtml(String(entry.elapsedMs ?? 0))} ms</span>`)
        .join("");

      card.innerHTML = `
        <header class="order-item-head">
          <strong>${escapeHtml(item.fileName || item.path)}</strong>
          <span class="badge" data-state="${escapeHtml(String(item.state || "").toLowerCase())}">${escapeHtml(item.state)}</span>
          ${item.durationMs ? `<span class="muted-note">${escapeHtml(String(item.durationMs))} ms</span>` : ""}
        </header>
        <div class="chip-row">${history}</div>
        ${item.error ? `<p class="field-error">${escapeHtml(item.error)} (${escapeHtml(item.errorCode || "")})</p>` : ""}
        ${item.recordId ? `<p class="muted-note cell-mono">registro: ${escapeHtml(item.recordId)}</p>` : ""}`;

      items.appendChild(card);
    }

    openOverlay(el("order-detail-overlay"));
  } catch (error) {
    toast(`No se pudo abrir la orden: ${error.message}`, "error");
  }
}
