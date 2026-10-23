/* Layout compartido: construye el aside de servicios y el topbar en cada pagina.
   Fuente unica de navegacion; cada pagina declara data-page, data-title-key y
   data-subtitle-key. Los textos salen del diccionario de i18n. */

import { t, applyI18n, toggleLang, getLang, onLanguageChange } from "./i18n.js";

const NAV_ITEMS = [
  { key: "compresor", href: "/studio/", icon: "\u25A3", labelKey: "nav.compresor", tipKey: "nav.compresor.tip" },
  { key: "galeria", href: "/studio/galeria.html", icon: "\u25A6", labelKey: "nav.galeria", tipKey: "nav.galeria.tip" },
  { key: "chat", href: "/studio/chat.html", icon: "\u2709", labelKey: "nav.chat", tipKey: "nav.chat.tip" },
  { key: "ajustes", href: "/studio/ajustes.html", icon: "\u2699", labelKey: "nav.ajustes", tipKey: "nav.ajustes.tip" },
  { key: "actividad", href: "/studio/actividad.html", icon: "\u2732", labelKey: "nav.actividad", tipKey: "nav.actividad.tip" }
];

const SECONDARY_LINKS = [
  { href: "/health", icon: "\u2665", labelKey: "nav.health" },
  { href: "/mcp/tools", icon: "\u2699", labelKey: "nav.mcp" }
];

function escapeHtml(text) {
  return String(text ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

function pageKeyFor(page) {
  const known = NAV_ITEMS.find((item) => item.key === page);
  return known ? known.key : "compresor";
}

let navStateBound = false;
let tooltipsBound = false;
let languageBound = false;

export function renderLayout() {
  const body = document.body;
  const page = body.getAttribute("data-page") || "compresor";
  const key = pageKeyFor(page);

  // La clave i18n manda; data-title/data-subtitle quedan como respaldo estatico
  // (util si el modulo de idioma no llega a cargar).
  const titleKey = `page.${key}.title`;
  const subtitleKey = `page.${key}.subtitle`;
  const resolvedTitle = t(titleKey);
  const resolvedSubtitle = t(subtitleKey);

  const title = resolvedTitle.startsWith("page.")
    ? (body.getAttribute("data-title") || resolvedTitle)
    : resolvedTitle;
  const subtitle = resolvedSubtitle.startsWith("page.")
    ? (body.getAttribute("data-subtitle") || resolvedSubtitle)
    : resolvedSubtitle;

  const nav = document.getElementById("svc-nav");
  if (nav) {
    nav.innerHTML = `
      <div class="svc-brand">
        <span class="svc-brand-mark" aria-hidden="true">S</span>
        <span class="svc-brand-text">
          <strong>SUCA Studio</strong>
          <small data-i18n="nav.brand.subtitle">${escapeHtml(t("nav.brand.subtitle"))}</small>
        </span>
      </div>
      <button class="svc-collapse" id="nav-toggle" type="button" aria-expanded="true" aria-controls="svc-menu"
              data-tip-title="${escapeHtml(t("nav.collapse"))}"
              data-tip="${escapeHtml(t("common.keyboardHelp"))}"
              data-tip-pos="right">
        <span class="svc-icon" aria-hidden="true">&#9776;</span>
        <span class="svc-label">${escapeHtml(t("nav.collapse"))}</span>
      </button>
      <nav class="svc-menu" id="svc-menu">
        ${NAV_ITEMS.map((item) => `
        <a class="svc-item${item.key === page ? " active" : ""}" href="${item.href}"${item.key === page ? ' aria-current="page"' : ""}>
          <span class="svc-icon" aria-hidden="true">${item.icon}</span>
          <span class="svc-label">${escapeHtml(t(item.labelKey))}</span>
          <span class="tip-dot" data-tip-title="${escapeHtml(t(item.labelKey))}" data-tip="${escapeHtml(t(item.tipKey))}" data-tip-pos="right" tabindex="0" role="note">?</span>
        </a>`).join("")}
      </nav>
      <div class="svc-secondary">
        ${SECONDARY_LINKS.map((link) => `
        <a href="${link.href}" target="_blank" rel="noopener">
          <span class="svc-icon" aria-hidden="true">${link.icon}</span>
          <span class="svc-label">${escapeHtml(t(link.labelKey))}</span>
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
  renderTopbarTools();

  // Un solo suscriptor de idioma para toda la sesion: registrarlo aqui lo
  // multiplicaria en cada re-render (crecimiento exponencial de listeners).
  if (!languageBound) {
    languageBound = true;
    onLanguageChange(() => {
      renderLayout();
      applyI18n();
    });
  }
}

function renderTopbarTools() {
  const topbar = document.querySelector(".svc-topbar");
  if (!topbar) return;

  let tools = document.getElementById("svc-topbar-tools");
  if (!tools) {
    tools = document.createElement("div");
    tools.id = "svc-topbar-tools";
    tools.className = "svc-topbar-tools";
    topbar.appendChild(tools);
  }

  let status = document.getElementById("server-status");
  if (!status) {
    status = document.createElement("span");
    status.id = "server-status";
    status.className = "svc-status";
    status.setAttribute("role", "status");
    status.setAttribute("aria-live", "polite");
    tools.appendChild(status);
  }

  let langButton = document.getElementById("lang-toggle");
  if (!langButton) {
    langButton = document.createElement("button");
    langButton.type = "button";
    langButton.id = "lang-toggle";
    langButton.className = "svc-tool-btn";
    langButton.addEventListener("click", () => {
      const next = toggleLang();
      document.dispatchEvent(new CustomEvent("suca:language-changed", { detail: { lang: next } }));
    });
    tools.appendChild(langButton);
  }

  langButton.setAttribute("data-tip-title", t("common.language"));
  langButton.setAttribute("data-tip", getLang() === "es" ? "Switch to English" : "Cambiar a espanol");
  langButton.textContent = getLang() === "es" ? "ES" : "EN";
}

function initNavState() {
  const body = document.body;
  const stored = localStorage.getItem("suca.nav.collapsed");
  if (stored === "true") {
    body.setAttribute("data-nav-collapsed", "true");
    const toggle = document.getElementById("nav-toggle");
    if (toggle) toggle.setAttribute("aria-expanded", "false");
  }

  if (navStateBound) return;
  navStateBound = true;

  const toggle = document.getElementById("nav-toggle");
  if (toggle) {
    toggle.addEventListener("click", () => {
      const collapsed = body.getAttribute("data-nav-collapsed") === "true";
      const next = String(!collapsed);
      body.setAttribute("data-nav-collapsed", next);
      localStorage.setItem("suca.nav.collapsed", next);
      toggle.setAttribute("aria-expanded", collapsed ? "true" : "false");
      const label = toggle.querySelector(".svc-label");
      if (label) label.textContent = collapsed ? t("nav.collapse") : t("nav.expand");
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
  if (tooltipsBound) return;
  tooltipsBound = true;

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