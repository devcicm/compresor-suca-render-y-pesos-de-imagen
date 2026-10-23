/* Dialogos del shell: reemplazan window.confirm/window.prompt.
   Motivos: los nativos bloquean el hilo, no se pueden estilizar, ignoran el tema,
   no atrapan el foco ni son accesibles. Un unico overlay reutilizable evita
   duplicar markup en cada vista (DRY). */

import { openOverlay, closeOverlay } from "../a11y.js";

let dialog = null;
let settle = null;

function ensureDialog() {
  if (dialog) return dialog;

  const overlay = document.createElement("div");
  overlay.id = "ui-dialog";
  overlay.className = "overlay";
  overlay.hidden = true;

  const card = document.createElement("div");
  card.className = "card dialog-card";
  card.setAttribute("role", "document");

  const title = document.createElement("h3");
  title.className = "dialog-title";

  const message = document.createElement("p");
  message.className = "dialog-message";

  const label = document.createElement("label");
  label.className = "field dialog-field";

  const labelText = document.createElement("span");
  labelText.className = "field-label";

  const input = document.createElement("input");
  input.type = "text";
  input.className = "dialog-input";

  label.appendChild(labelText);
  label.appendChild(input);

  const actions = document.createElement("div");
  actions.className = "btn-row dialog-actions";

  const cancel = document.createElement("button");
  cancel.type = "button";
  cancel.className = "btn";
  cancel.textContent = "Cancelar";

  const accept = document.createElement("button");
  accept.type = "button";
  accept.className = "btn btn-primary";
  accept.textContent = "Confirmar";

  actions.appendChild(cancel);
  actions.appendChild(accept);

  card.appendChild(title);
  card.appendChild(message);
  card.appendChild(label);
  card.appendChild(actions);
  overlay.appendChild(card);
  document.body.appendChild(overlay);

  const finish = (value) => {
    const resolver = settle;
    settle = null;
    closeOverlay(overlay);
    if (resolver) resolver(value);
  };

  cancel.addEventListener("click", () => finish(null));
  accept.addEventListener("click", () => {
    if (input.hidden) {
      finish(true);
      return;
    }

    const value = input.value.trim();
    if (value.length === 0) {
      input.setAttribute("aria-invalid", "true");
      input.focus();
      return;
    }

    input.removeAttribute("aria-invalid");
    finish(value);
  });

  overlay.addEventListener("click", (event) => {
    if (event.target === overlay) finish(null);
  });

  input.addEventListener("keydown", (event) => {
    if (event.key === "Enter") {
      event.preventDefault();
      accept.click();
    }
  });

  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && !overlay.hidden) finish(null);
  });

  dialog = { overlay, title, message, label, labelText, input, cancel, accept };
  return dialog;
}

function open({ title, message, mode, label, value, placeholder, confirmLabel, cancelLabel, danger }) {
  const ui = ensureDialog();

  ui.title.textContent = title;
  ui.message.textContent = message ?? "";
  ui.message.hidden = !message;

  ui.label.hidden = mode !== "prompt";
  ui.labelText.textContent = label ?? "Valor";
  ui.input.hidden = mode !== "prompt";
  ui.input.value = value ?? "";
  ui.input.placeholder = placeholder ?? "";
  ui.input.removeAttribute("aria-invalid");

  ui.accept.textContent = confirmLabel ?? (mode === "prompt" ? "Guardar" : "Confirmar");
  ui.accept.classList.toggle("btn-danger", Boolean(danger));
  ui.accept.classList.toggle("btn-primary", !danger);
  ui.cancel.textContent = cancelLabel ?? "Cancelar";

  return new Promise((resolve) => {
    settle = resolve;
    openOverlay(ui.overlay, () => {
      // Cierre por Escape: a11y cierra el overlay; resolvemos como cancelado.
      if (settle) {
        const resolver = settle;
        settle = null;
        resolver(null);
      }
    });

    // Foco en el campo cuando se pide texto; si no, en el boton de aceptar.
    const target = mode === "prompt" ? ui.input : ui.accept;
    window.setTimeout(() => target.focus(), 0);
    if (mode === "prompt") ui.input.select();
  });
}

/** Confirmacion. Resuelve true/false (nunca null). */
export async function confirmDialog({
  title,
  message,
  confirmLabel = "Confirmar",
  cancelLabel = "Cancelar",
  danger = false
}) {
  const result = await open({ title, message, mode: "confirm", confirmLabel, cancelLabel, danger });
  return result === true;
}

/** Pide un texto. Resuelve el texto o null si se cancela. */
export async function promptDialog({
  title,
  label = "",
  message,
  value = "",
  placeholder = "",
  confirmLabel = "Guardar",
  cancelLabel = "Cancelar"
}) {
  const ui = ensureDialog();
  const result = await open({ title, message, mode: "prompt", label, value, placeholder, confirmLabel, cancelLabel });
  return typeof result === "string" ? result : null;
}