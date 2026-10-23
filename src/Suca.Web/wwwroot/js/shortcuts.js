/* Atajos globales del shell.
   No interfieren con la escritura: se ignoran cuando el foco esta en un campo.
   Las acciones de pagina se emiten como evento "suca:action" para que cada vista
   decida que hacer (convertir, abrir archivos, cancelar). */

import { t, onLanguageChange } from "./i18n.js";
import { openOverlay, closeOverlay, anyOverlayOpen } from "./a11y.js";

function isTypingTarget(target) {
  if (!target) return false;
  const tag = target.tagName;
  return tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT" || target.isContentEditable;
}

function emit(action) {
  document.dispatchEvent(new CustomEvent("suca:action", { detail: { action } }));
}

const BINDINGS = [
  { combo: "ctrl+o", key: "o", labelKey: "shortcuts.open", action: "open-files" },
  { combo: "ctrl+enter", key: "Enter", labelKey: "shortcuts.convert", action: "convert" },
  { combo: "ctrl+shift+c", key: "C", labelKey: "shortcuts.cancelAll", action: "cancel-all" },
  { combo: "shift+?", labelKey: "shortcuts.help", action: "help" }
];

const seenCombos = new Set();

function matches(event, binding) {
  const parts = binding.combo.split("+");
  const key = parts[parts.length - 1];
  const needsCtrl = parts.includes("ctrl");
  const needsShift = parts.includes("shift");

  if (needsCtrl && !(event.ctrlKey || event.metaKey)) return false;
  if (needsShift && !event.shiftKey) return false;
  if (!needsShift && event.shiftKey && key !== "?") return false;

  const pressed = event.key.length === 1 ? event.key.toLowerCase() : event.key;
  const expected = key.length === 1 ? key.toLowerCase() : key;
  return pressed === expected;
}

function comboLabel(combo) {
  const isMac = /Mac|iPhone|iPad/.test(navigator.platform || navigator.userAgent);
  return combo
    .split("+")
    .map((part) => {
      if (part === "ctrl") return isMac ? "Cmd" : "Ctrl";
      if (part === "shift") return "Shift";
      if (part === "enter") return "Enter";
      if (part === "/") return "?";
      return part.length === 1 ? part.toUpperCase() : part;
    })
    .join(" + ");
}

export function initShortcuts() {
  ensureHelpOverlay();
  window.__sucaShortcutsReady = true;

  document.addEventListener("keydown", (event) => {
    // Escape siempre cierra el overlay abierto (tambien lo hace a11y.js).
    if (event.key === "Escape") return;

    if (isTypingTarget(event.target)) return;

    const binding = BINDINGS.find((candidate) => matches(event, candidate));
    if (!binding) return;

    event.preventDefault();
    if (binding.action === "help") {
      if (anyOverlayOpen()) return;
      openOverlay(document.getElementById("shortcut-overlay"), () => {
        seenCombos.clear();
      });
      return;
    }

    emit(binding.action);
  });
}

function ensureHelpOverlay() {
  if (document.getElementById("shortcut-overlay")) return;

  const overlay = document.createElement("div");
  overlay.id = "shortcut-overlay";
  overlay.className = "overlay";
  overlay.hidden = true;
  overlay.setAttribute("data-i18n-overlay", "shortcuts.title");

  const card = document.createElement("div");
  card.className = "card detail-card";

  const header = document.createElement("header");
  header.className = "card-head";

  const heading = document.createElement("h3");
  heading.setAttribute("data-i18n", "shortcuts.title");

  const close = document.createElement("button");
  close.type = "button";
  close.className = "btn";
  close.setAttribute("data-i18n", "common.close");
  close.addEventListener("click", () => closeOverlay(overlay));

  header.appendChild(heading);
  header.appendChild(close);
  card.appendChild(header);

  const list = document.createElement("dl");
  list.className = "shortcut-list";
  card.appendChild(list);
  overlay.appendChild(card);
  document.body.appendChild(overlay);

  const renderRows = () => {
    list.innerHTML = "";
    BINDINGS.forEach((binding) => {
      const term = document.createElement("dt");
      const code = document.createElement("kbd");
      code.textContent = comboLabel(binding.combo);
      term.appendChild(code);

      const description = document.createElement("dd");
      description.textContent = t(binding.labelKey);

      list.appendChild(term);
      list.appendChild(description);
    });

    const escapeTerm = document.createElement("dt");
    const escapeCode = document.createElement("kbd");
    escapeCode.textContent = "Esc";
    escapeTerm.appendChild(escapeCode);

    const escapeDescription = document.createElement("dd");
    escapeDescription.textContent = t("shortcuts.escape");

    list.appendChild(escapeTerm);
    list.appendChild(escapeDescription);
  };

  renderRows();
  onLanguageChange(renderRows);
  document.addEventListener("suca:language-changed", renderRows);
}