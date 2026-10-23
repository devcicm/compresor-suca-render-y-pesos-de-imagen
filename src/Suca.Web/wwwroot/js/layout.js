/* Layout compartido: construye el aside de servicios y el topbar en cada pagina.
   Fuente unica de navegacion; cada pagina declara data-page, data-title y data-subtitle. */

const NAV_ITEMS = [
  {
    key: "compresor",
    href: "/studio/",
    icon: "\u25A3",
    label: "Compresor SUCA",
    tipTitle: "Compresor SUCA",
    tip: "Vista principal: sube o arrastra imagenes y convertirlas con el pipeline SUCA o re-encodado directo. WebP por defecto."
  },
  {
    key: "galeria",
    href: "/studio/galeria.html",
    icon: "\u25A6",
    label: "Galeria",
    tipTitle: "Galeria",
    tip: "Todas las imagenes convertidas, agrupadas por coleccion. Descarga, base64, rutas en disco y borrado."
  },
  {
    key: "chat",
    href: "/studio/chat.html",
    icon: "\u2709",
    label: "Chat (Minichat)",
    tipTitle: "Chat",
    tip: "Minichat LLM con RAG. Requiere LM Studio activo en la URL configurada."
  },
  {
    key: "ajustes",
    href: "/studio/ajustes.html",
    icon: "\u2699",
    label: "Ajustes",
    tipTitle: "Ajustes",
    tip: "Preferencias de sistema y usuario. Precedencia: CLI > usuario > sistema > por defecto."
  },
  {
    key: "actividad",
    href: "/studio/actividad.html",
    icon: "\u2732",
    label: "Actividad",
    tipTitle: "Actividad",
    tip: "Trazabilidad: ordenes orquestadas y eventos recientes del servidor con su correlacion."
  }
];

const SECONDARY_LINKS = [
  { href: "/health", icon: "\u2665", label: "Salud", external: true },
  { href: "/mcp/tools", icon: "\u2699", label: "MCP", external: true }
];

function escapeHtml(text) {
  return String(text ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

export function renderLayout() {
  const body = document.body;
  const page = body.getAttribute("data-page") || "compresor";
  const title = body.getAttribute("data-title") || "SUCA Studio";
  const subtitle = body.getAttribute("data-subtitle") || "";

  const nav = document.getElementById("svc-nav");
  if (nav) {
    nav.innerHTML = `
      <div class="svc-brand">
        <span class="svc-brand-mark" aria-hidden="true">S</span>
        <span class="svc-brand-text">
          <strong>SUCA Studio</strong>
          <small>Conversion de imagenes</small>
        </span>
      </div>
      <button class="svc-collapse" id="nav-toggle" type="button" aria-expanded="true" aria-controls="svc-menu"
              data-tip="Contraer o expandir el menu" data-tip-pos="right">
        <span class="svc-icon" aria-hidden="true">&#9776;</span>
        <span class="svc-label">Contraer</span>
      </button>
      <nav class="svc-menu" id="svc-menu">
        ${NAV_ITEMS.map((item) => `
        <a class="svc-item${item.key === page ? " active" : ""}" href="${item.href}"${item.key === page ? ' aria-current="page"' : ""}>
          <span class="svc-icon" aria-hidden="true">${item.icon}</span>
          <span class="svc-label">${escapeHtml(item.label)}</span>
          <span class="tip-dot" data-tip-title="${escapeHtml(item.tipTitle)}" data-tip="${escapeHtml(item.tip)}" data-tip-pos="right" tabindex="0" role="note">?</span>
        </a>`).join("")}
      </nav>
      <div class="svc-secondary">
        ${SECONDARY_LINKS.map((link) => `
        <a href="${link.href}" target="_blank" rel="noopener">
          <span class="svc-icon" aria-hidden="true">${link.icon}</span>
          <span class="svc-label">${escapeHtml(link.label)}</span>
        </a>`).join("")}
      </div>`;
  }

  const topbar = document.getElementById("view-title");
  if (topbar) topbar.textContent = title;

  const subtitleEl = document.getElementById("view-subtitle");
  if (subtitleEl) subtitleEl.textContent = subtitle;

  document.title = `${title} — SUCA Studio`;

  initNavState();
  initTooltips();
}

function initNavState() {
  const body = document.body;
  const stored = localStorage.getItem("suca.nav.collapsed");
  if (stored === "true") {
    body.setAttribute("data-nav-collapsed", "true");
    const toggle = document.getElementById("nav-toggle");
    if (toggle) toggle.setAttribute("aria-expanded", "false");
  }

  const toggle = document.getElementById("nav-toggle");
  if (toggle) {
    toggle.addEventListener("click", () => {
      const collapsed = body.getAttribute("data-nav-collapsed") === "true";
      const next = String(!collapsed);
      body.setAttribute("data-nav-collapsed", next);
      localStorage.setItem("suca.nav.collapsed", next);
      toggle.setAttribute("aria-expanded", collapsed ? "true" : "false");
      const label = toggle.querySelector(".svc-label");
      if (label) label.textContent = collapsed ? "Contraer" : "Expandir";
    });
  }

  const burger = document.getElementById("nav-burger");
  const backdrop = document.getElementById("nav-backdrop");
  if (burger) {
    burger.addEventListener("click", () => {
      const open = body.getAttribute("data-nav-open") === "true";
      body.setAttribute("data-nav-open", String(!open));
      if (backdrop) backdrop.hidden = open;
    });
  }

  if (backdrop) {
    backdrop.addEventListener("click", () => {
      body.removeAttribute("data-nav-open");
      backdrop.hidden = true;
    });
  }
}

/* Tooltips: hover/foco en escritorio; toque alterna en pantallas tactiles. */
function initTooltips() {
  document.addEventListener("click", (event) => {
    const dot = event.target.closest(".tip-dot");
    document.querySelectorAll(".tip-dot[data-open='true']").forEach((open) => {
      if (open !== dot) open.removeAttribute("data-open");
    });

    if (dot && window.matchMedia("(hover: none)").matches) {
      event.preventDefault();
      dot.setAttribute("data-open", dot.getAttribute("data-open") === "true" ? "false" : "true");
    }
  });

  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") {
      document.querySelectorAll(".tip-dot[data-open='true']").forEach((dot) => dot.removeAttribute("data-open"));
    }
  });
}
