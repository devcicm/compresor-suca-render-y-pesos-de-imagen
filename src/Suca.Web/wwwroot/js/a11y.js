/* Accesibilidad del shell: gestion de overlays con foco atrapado, navegacion por
   teclado en listas/grillas y una region viva para anunciar el progreso.
   No usa estilos inline: los estados viajan por atributos y clases. */

const FOCUSABLE = [
  "a[href]",
  "button:not([disabled])",
  "input:not([disabled]):not([type='hidden'])",
  "select:not([disabled])",
  "textarea:not([disabled])",
  "details > summary",
  "[tabindex]:not([tabindex='-1'])"
].join(",");

const FOCUSABLE_HIDDEN = /^(hidden|aria-hidden="true")$/;

export function focusableWithin(container) {
  if (!container) return [];
  return Array.from(container.querySelectorAll(FOCUSABLE)).filter((node) => {
    if (node.closest("[hidden]")) return false;
    if (FOCUSABLE_HIDDEN.test(node.getAttribute("aria-hidden") || "")) return false;
    return node.offsetParent !== null || node === document.activeElement;
  });
}

const openOverlays = new Set();

/* Abre un overlay: guarda el foco previo, mueve el foco dentro y atrapa Tab.
   onClose debe cerrar el overlay (normalmente asignando hidden = true). */
export function openOverlay(overlay, onClose) {
  if (!overlay || openOverlays.has(overlay)) return;

  const entry = { overlay, onClose, previous: document.activeElement };
  openOverlays.add(entry);

  overlay.hidden = false;
  overlay.setAttribute("role", "dialog");
  overlay.setAttribute("aria-modal", "true");

  const targets = focusableWithin(overlay);
  if (targets.length > 0) {
    targets[0].focus();
  } else {
    overlay.setAttribute("tabindex", "-1");
    overlay.focus();
  }
}

export function closeOverlay(overlay, onClose) {
  const entry = Array.from(openOverlays).find((item) => item.overlay === overlay);
  if (!entry) return;

  openOverlays.delete(entry);
  overlay.hidden = true;
  overlay.removeAttribute("role");
  overlay.removeAttribute("aria-modal");

  const closer = onClose || entry.onClose;
  if (typeof closer === "function") closer();

  if (entry.previous && document.contains(entry.previous)) {
    entry.previous.focus();
  }
}

export function isOverlayOpen(overlay) {
  return Array.from(openOverlays).some((item) => item.overlay === overlay);
}

export function anyOverlayOpen() {
  return openOverlays.size > 0;
}

/* Escape cierra el overlay mas reciente. */
document.addEventListener("keydown", (event) => {
  if (event.key !== "Escape") return;

  const entries = Array.from(openOverlays);
  if (entries.length === 0) return;

  const last = entries[entries.length - 1];
  closeOverlay(last.overlay);
});

/* Tab atrapado dentro del overlay abierto. */
document.addEventListener("keydown", (event) => {
  if (event.key !== "Tab" || openOverlays.size === 0) return;

  const last = Array.from(openOverlays)[openOverlays.size - 1];
  const targets = focusableWithin(last.overlay);
  if (targets.length === 0) return;

  const first = targets[0];
  const lastTarget = targets[targets.length - 1];
  const active = document.activeElement;

  if (event.shiftKey && (active === first || !last.overlay.contains(active))) {
    event.preventDefault();
    lastTarget.focus();
  } else if (!event.shiftKey && active === lastTarget) {
    event.preventDefault();
    first.focus();
  }
});

/* Navegacion vertical/horizontal con flechas dentro de un contenedor de items.
   Cada item debe llevar tabindex="0" (o -1) y data-nav-item. */
export function enableListNavigation(container, itemSelector = "[data-nav-item]") {
  if (!container) return;

  container.addEventListener("keydown", (event) => {
    const keys = ["ArrowDown", "ArrowRight", "ArrowUp", "ArrowLeft", "Home", "End"];
    if (!keys.includes(event.key)) return;

    const items = Array.from(container.querySelectorAll(itemSelector))
      .filter((node) => node.offsetParent !== null);
    if (items.length === 0) return;

    const current = items.indexOf(document.activeElement);
    if (current < 0) return;

    event.preventDefault();
    let next = current;
    if (event.key === "ArrowDown" || event.key === "ArrowRight") next = current + 1;
    if (event.key === "ArrowUp" || event.key === "ArrowLeft") next = current - 1;
    if (event.key === "Home") next = 0;
    if (event.key === "End") next = items.length - 1;

    const target = items[Math.max(0, Math.min(items.length - 1, next))];
    if (target) {
      items.forEach((node) => node.setAttribute("tabindex", "-1"));
      target.setAttribute("tabindex", "0");
      target.focus();
    }
  });
}

/* Region viva para anunciar el estado agregado sin saturar al lector de pantalla. */
let liveNode = null;
let liveTimer = 0;

export function announce(message) {
  if (!liveNode) {
    liveNode = document.getElementById("suca-live-region");
    if (!liveNode) {
      liveNode = document.createElement("div");
      liveNode.id = "suca-live-region";
      liveNode.className = "visually-hidden";
      liveNode.setAttribute("role", "status");
      liveNode.setAttribute("aria-live", "polite");
      document.body.appendChild(liveNode);
    }
  }

  window.clearTimeout(liveTimer);
  liveTimer = window.setTimeout(() => {
    liveNode.textContent = message;
  }, 250);
}