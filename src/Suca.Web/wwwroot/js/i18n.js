/* Internacionalizacion del shell SUCA Studio.
   Español por defecto; inglés disponible con el conmutador del topbar.
   Las claves ausentes devuelven la propia clave (fallback visible) y se registran
   en window.__sucaI18nMissing para poder verificarlas durante el desarrollo. */

const STORAGE_KEY = "suca.lang";
const SUPPORTED = ["es", "en"];

const DICT = {
  es: {
    "common.cancel": "Cancelar",
    "common.close": "Cerrar",
    "common.save": "Guardar",
    "common.apply": "Aplicar",
    "common.retry": "Reintentar",
    "common.confirm": "Confirmar",
    "common.search": "Buscar",
    "common.loading": "Cargando...",
    "common.empty": "Sin resultados.",
    "common.dismiss": "Descartar",
    "common.language": "Idioma",
    "common.keyboardHelp": "Atajos de teclado",

    "nav.compresor": "Compresor SUCA",
    "nav.compresor.tip": "Vista principal: sube o arrastra imagenes y convertirlas con el pipeline SUCA o re-encodado directo. WebP por defecto.",
    "nav.galeria": "Galeria",
    "nav.galeria.tip": "Todas las imagenes convertidas, agrupadas por coleccion. Descarga, base64, rutas en disco y borrado.",
    "nav.chat": "Chat (Minichat)",
    "nav.chat.tip": "Minichat LLM con RAG. Requiere LM Studio activo en la URL configurada.",
    "nav.ajustes": "Ajustes",
    "nav.ajustes.tip": "Preferencias de sistema y usuario. Precedencia: CLI > usuario > sistema > por defecto.",
    "nav.actividad": "Actividad",
    "nav.actividad.tip": "Trazabilidad: ordenes orquestadas y eventos recientes del servidor con su correlacion.",
    "nav.brand.subtitle": "Conversion de imagenes",
    "nav.collapse": "Contraer",
    "nav.expand": "Expandir",
    "nav.health": "Salud",
    "nav.mcp": "MCP",
    "nav.skip": "Saltar al contenido",

    "page.compresor.title": "Compresor SUCA",
    "page.compresor.subtitle": "Conversion orquestada con pipeline SUCA o directo",
    "page.galeria.title": "Galeria",
    "page.galeria.subtitle": "Resultados agrupados por coleccion",
    "page.chat.title": "Chat (Minichat)",
    "page.chat.subtitle": "Conversaciones con RAG sobre LM Studio",
    "page.ajustes.title": "Ajustes",
    "page.ajustes.subtitle": "Preferencias de sistema y usuario",
    "page.actividad.title": "Actividad",
    "page.actividad.subtitle": "Ordenes y eventos con su correlacion",

    "status.ok": "Servidor disponible",
    "status.degraded": "Servidor con avisos",
    "status.down": "Servidor no disponible",
    "status.checking": "Comprobando servidor...",
    "status.lost": "Se perdio la conexion con el servidor. Se reintentara automaticamente.",
    "status.restored": "Conexion con el servidor restablecida.",
    "status.refresh": "Comprobar ahora",

    "queue.state.pending": "Pendiente",
    "queue.state.waiting": "En cola",
    "queue.state.uploading": "Subiendo",
    "queue.state.processing": "Procesando",
    "queue.state.done": "Lista",
    "queue.state.paused": "Pausada",
    "queue.state.error": "Error",
    "queue.state.cancelled": "Cancelada",
    "queue.empty": "Sin archivos en la cola.",
    "queue.live": "{done} de {total} convertidas.",

    "folder.title": "Explorar carpetas del servidor",
    "folder.up": "Arriba",
    "folder.select": "Seleccionar esta carpeta",
    "folder.drives": "Unidades disponibles",
    "folder.empty": "Sin subcarpetas.",
    "folder.count": "{count} imagen(es)",
    "folder.validate": "Validar",

    "shortcuts.title": "Atajos de teclado",
    "shortcuts.open": "Abrir archivos",
    "shortcuts.convert": "Convertir pendientes",
    "shortcuts.cancelAll": "Cancelar conversiones en curso",
    "shortcuts.help": "Mostrar esta ayuda",
    "shortcuts.escape": "Cerrar el dialogo abierto",
    "shortcuts.search": "Enfocar el buscador de la pagina"
  },
  en: {
    "common.cancel": "Cancel",
    "common.close": "Close",
    "common.save": "Save",
    "common.apply": "Apply",
    "common.retry": "Retry",
    "common.confirm": "Confirm",
    "common.search": "Search",
    "common.loading": "Loading...",
    "common.empty": "No results.",
    "common.dismiss": "Discard",
    "common.language": "Language",
    "common.keyboardHelp": "Keyboard shortcuts",

    "nav.compresor": "SUCA Compressor",
    "nav.compresor.tip": "Main view: drop or upload images and convert them with the SUCA pipeline or direct re-encode. WebP by default.",
    "nav.galeria": "Gallery",
    "nav.galeria.tip": "All converted images, grouped by collection. Download, base64, disk paths and delete.",
    "nav.chat": "Chat (Minichat)",
    "nav.chat.tip": "LLM chat with RAG. Requires LM Studio running at the configured URL.",
    "nav.ajustes": "Settings",
    "nav.ajustes.tip": "System and user preferences. Precedence: CLI > user > system > default.",
    "nav.actividad": "Activity",
    "nav.actividad.tip": "Traceability: orchestrated orders and recent server events with their correlation.",
    "nav.brand.subtitle": "Image compression",
    "nav.collapse": "Collapse",
    "nav.expand": "Expand",
    "nav.health": "Health",
    "nav.mcp": "MCP",
    "nav.skip": "Skip to content",

    "page.compresor.title": "SUCA Compressor",
    "page.compresor.subtitle": "Orchestrated conversion with the SUCA or direct pipeline",
    "page.galeria.title": "Gallery",
    "page.galeria.subtitle": "Results grouped by collection",
    "page.chat.title": "Chat (Minichat)",
    "page.chat.subtitle": "Conversations with RAG over LM Studio",
    "page.ajustes.title": "Settings",
    "page.ajustes.subtitle": "System and user preferences",
    "page.actividad.title": "Activity",
    "page.actividad.subtitle": "Orders and events with their correlation",

    "status.ok": "Server available",
    "status.degraded": "Server with warnings",
    "status.down": "Server unavailable",
    "status.checking": "Checking server...",
    "status.lost": "Lost the connection to the server. It will retry automatically.",
    "status.restored": "Server connection restored.",
    "status.refresh": "Check now",

    "queue.state.pending": "Pending",
    "queue.state.waiting": "Waiting",
    "queue.state.uploading": "Uploading",
    "queue.state.processing": "Processing",
    "queue.state.done": "Done",
    "queue.state.paused": "Paused",
    "queue.state.error": "Error",
    "queue.state.cancelled": "Cancelled",
    "queue.empty": "No files in the queue.",
    "queue.live": "{done} of {total} converted.",

    "folder.title": "Browse server folders",
    "folder.up": "Up",
    "folder.select": "Select this folder",
    "folder.drives": "Available drives",
    "folder.empty": "No subfolders.",
    "folder.count": "{count} image(s)",
    "folder.validate": "Validate",

    "shortcuts.title": "Keyboard shortcuts",
    "shortcuts.open": "Open files",
    "shortcuts.convert": "Convert pending files",
    "shortcuts.cancelAll": "Cancel in-flight conversions",
    "shortcuts.help": "Show this help",
    "shortcuts.escape": "Close the open dialog",
    "shortcuts.search": "Focus the page search box"
  }
};

const listeners = new Set();
let current = "es";

function normalize(value) {
  return value === "en" ? "en" : "es";
}

export function getLang() {
  return current;
}

export function availableLangs() {
  return SUPPORTED.slice();
}

export function t(key, vars) {
  let value = DICT[current][key];

  if (value === undefined) {
    value = DICT.es[key];
  }

  if (value === undefined) {
    if (Array.isArray(window.__sucaI18nMissing) && !window.__sucaI18nMissing.includes(key)) {
      window.__sucaI18nMissing.push(key);
    }
    return key;
  }

  if (vars) {
    Object.entries(vars).forEach(([name, replacement]) => {
      value = value.split(`{${name}}`).join(String(replacement));
    });
  }

  return value;
}

export function onLanguageChange(listener) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

function notify() {
  listeners.forEach((listener) => {
    try {
      listener(current);
    } catch {
      /* un suscriptor roto no debe romper el cambio de idioma */
    }
  });
}

export function setLang(lang) {
  const next = normalize(lang);
  if (next === current) return current;

  current = next;
  localStorage.setItem(STORAGE_KEY, current);
  document.documentElement.setAttribute("lang", current);
  applyI18n();
  notify();
  return current;
}

export function toggleLang() {
  return setLang(current === "es" ? "en" : "es");
}

/* Aplica data-i18n* a cualquier subarbol (documento completo por defecto). */
export function applyI18n(root = document) {
  const scope = root.querySelectorAll
    ? root.querySelectorAll("[data-i18n]")
    : [];
  scope.forEach((node) => {
    node.textContent = t(node.getAttribute("data-i18n"));
  });

  const attrs = [
    ["placeholder", "data-i18n-placeholder"],
    ["title", "data-i18n-title"],
    ["aria-label", "data-i18n-aria-label"],
    ["data-tip", "data-i18n-tip"],
    ["data-tip-title", "data-i18n-tip-title"],
    ["data-tip-pos", "data-i18n-tip-pos"]
  ];

  attrs.forEach(([attribute, marker]) => {
    const nodes = root.querySelectorAll ? root.querySelectorAll(`[${marker}]`) : [];
    nodes.forEach((node) => {
      node.setAttribute(attribute, t(node.getAttribute(marker)));
    });
  });
}

/* Formateo localizado: se apoya en Intl en vez de concatenar a mano. */
const LOCALE = () => (current === "en" ? "en-US" : "es-ES");

export function formatDateTime(value) {
  const date = value instanceof Date ? value : new Date(value);
  if (Number.isNaN(date.getTime())) return String(value ?? "");
  return new Intl.DateTimeFormat(LOCALE(), {
    dateStyle: "short",
    timeStyle: "medium"
  }).format(date);
}

export function formatRelative(value) {
  const date = value instanceof Date ? value : new Date(value);
  if (Number.isNaN(date.getTime())) return String(value ?? "");

  const diffSeconds = Math.round((date.getTime() - Date.now()) / 1000);
  const units = [
    ["year", 31536000],
    ["month", 2592000],
    ["day", 86400],
    ["hour", 3600],
    ["minute", 60]
  ];

  const rtf = new Intl.RelativeTimeFormat(LOCALE(), { numeric: "auto" });
  for (const [unit, seconds] of units) {
    if (Math.abs(diffSeconds) >= seconds) {
      return rtf.format(Math.round(diffSeconds / seconds), unit);
    }
  }

  return rtf.format(diffSeconds, "second");
}

export function formatNumber(value, options) {
  const number = Number(value);
  if (!Number.isFinite(number)) return String(value ?? "");
  return new Intl.NumberFormat(LOCALE(), options).format(number);
}

export function formatPercent(value, digits = 1) {
  const number = Number(value);
  if (!Number.isFinite(number)) return String(value ?? "");
  return `${formatNumber(number, {
    minimumFractionDigits: digits,
    maximumFractionDigits: digits
  })} %`;
}

/* Arranque: lee el idioma guardado y deja el documento marcado. */
export function initI18n() {
  window.__sucaI18nMissing = [];
  const stored = normalize(localStorage.getItem(STORAGE_KEY) || "es");
  current = stored;
  document.documentElement.setAttribute("lang", current);
  return current;
}