import { el, escapeHtml, toast, fetchJson } from "../main.js";

let initialized = false;
let filterText = "";

export function initActivityView() {
  if (initialized) return;
  initialized = true;

  el("refresh-activity-button").addEventListener("click", () => void load());
  el("activity-filter").addEventListener("input", () => {
    filterText = el("activity-filter").value.trim().toLowerCase();
    void loadEvents();
  });

  document.addEventListener("suca:orders-dirty", () => void load());
  void load();
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
        <span class="order-id">${escapeHtml(order.orderId)}</span>
        <span>${order.completed}/${order.totalItems} ok</span>
        ${order.failed ? `<span class="chip" data-kind="bad">${order.failed} fallos</span>` : ""}
        ${order.cancelled ? `<span class="chip" data-kind="warn">${order.cancelled} cancel.</span>` : ""}
        <span class="progress" style="min-width:120px"><span class="progress-fill" style="width:${order.progressPercent}%"></span></span>
        <span>${order.progressPercent}%</span>`;

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

    rows.innerHTML = "";

    for (const event of filtered.slice(0, 120)) {
      const tr = document.createElement("tr");
      tr.innerHTML = `
        <td class="cell-mono">${escapeHtml(new Date(event.timestampUtc).toLocaleTimeString())}</td>
        <td data-level="${escapeHtml(event.level)}">${escapeHtml(event.level)}</td>
        <td>${escapeHtml(event.name)}</td>
        <td>${escapeHtml(event.message)}</td>
        <td class="cell-mono">${escapeHtml(event.correlationId || "")}</td>`;
      rows.appendChild(tr);
    }

    if (filtered.length === 0) {
      rows.innerHTML = `<tr><td colspan="5"><div class="empty-state">Sin eventos que coincidan con el filtro.</div></td></tr>`;
    }
  } catch (error) {
    toast(`No se pudieron cargar los eventos: ${error.message}`, "error");
  }
}
