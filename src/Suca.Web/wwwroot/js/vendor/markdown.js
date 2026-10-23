/* Renderer Markdown minimo (vendorizado, sin dependencias externas).
   Origin: implementación propia para SUCA Studio, bajo licencia MIT.
   Ver THIRD-PARTY-NOTICES.md en la raíz del repositorio.

   Cubre lo que necesita el chat: encabezados, listas, citas, bloques de codigo,
   negritas, cursivas, enlaces, lineas horizontales y saltos de parrafo.
   El texto siempre pasa por escapeHtml antes de transformarse. */

const ESCAPES = { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" };

function escapeHtml(text) {
  return String(text ?? "").replace(/[&<>"']/g, (char) => ESCAPES[char]);
}

function escapeRegExp(text) {
  return String(text).replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function inline(text) {
  let value = escapeHtml(text);

  // Codigo en linea (antes que los enlaces para no pisar las URLs).
  const codeSpans = [];
  value = value.replace(/`([^`]+)`/g, (_, code) => {
    codeSpans.push(code);
    return `\u0000CODE${codeSpans.length - 1}\u0000`;
  });

  // Enlaces [texto](url) con esquema permitido.
  value = value.replace(/\[([^\]]+)\]\((https?:\/\/[^\s)]+|\/[^\s)]*)\)/g,
    (_, label, href) => `<a href="${href}" target="_blank" rel="noopener noreferrer">${label}</a>`);

  value = value.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
  value = value.replace(/(^|[^*])\*([^*\n]+)\*/g, "$1<em>$2</em>");
  value = value.replace(/(^|[^_])_([^_\n]+)_/g, "$1<em>$2</em>");

  return value.replace(/\u0000CODE(\d+)\u0000/g, (_, index) => `<code>${codeSpans[Number(index)]}</code>`);
}

/**
 * Convierte Markdown a HTML seguro.
 * @param {string} markdown
 * @returns {string} HTML
 */
export function renderMarkdown(markdown) {
  const source = String(markdown ?? "").replace(/\r\n?/g, "\n");
  if (source.trim().length === 0) return "";

  const lines = source.split("\n");
  const html = [];
  let index = 0;
  let listType = null;

  const closeList = () => {
    if (listType) {
      html.push(`</${listType}>`);
      listType = null;
    }
  };

  while (index < lines.length) {
    const line = lines[index];

    // Bloque de codigo con Vienna: se conserva literal.
    const fence = line.match(/^\s*```(\w*)\s*$/);
    if (fence) {
      closeList();
      const language = fence[1] || "";
      const buffer = [];
      index += 1;
      while (index < lines.length && !/^\s*```\s*$/.test(lines[index])) {
        buffer.push(lines[index]);
        index += 1;
      }
      index += 1;
      html.push(
        `<pre class="md-code"${language ? ` data-lang="${escapeHtml(language)}"` : ""}><code>${escapeHtml(buffer.join("\n"))}</code></pre>`
      );
      continue;
    }

    // Linea horizontal.
    if (/^\s*([-*_])\1{2,}\s*$/.test(line)) {
      closeList();
      html.push("<hr>");
      index += 1;
      continue;
    }

    // Encabezados.
    const heading = line.match(/^\s*(#{1,6})\s+(.*)$/);
    if (heading) {
      closeList();
      const level = heading[1].length;
      html.push(`<h${level + 2}>${inline(heading[2])}</h${level + 2}>`);
      index += 1;
      continue;
    }

    // Cita.
    const quote = line.match(/^\s*>\s?(.*)$/);
    if (quote) {
      closeList();
      const buffer = [];
      while (index < lines.length) {
        const match = lines[index].match(/^\s*>\s?(.*)$/);
        if (!match) break;
        buffer.push(match[1]);
        index += 1;
      }
      html.push(`<blockquote>${inline(buffer.join(" "))}</blockquote>`);
      continue;
    }

    // Listas con vinetas o numeros.
    const bullet = line.match(/^\s*[-*+]\s+(.*)$/);
    const ordered = line.match(/^\s*\d+[.)]\s+(.*)$/);
    if (bullet || ordered) {
      const wanted = bullet ? "ul" : "ol";
      if (listType !== wanted) {
        closeList();
        listType = wanted;
        html.push(`<${wanted}>`);
      }
      html.push(`<li>${inline((bullet || ordered)[1])}</li>`);
      index += 1;
      continue;
    }

    // Parrafo: agrupa lineas consecutivas.
    if (line.trim().length === 0) {
      closeList();
      index += 1;
      continue;
    }

    closeList();
    const buffer = [];
    while (index < lines.length && lines[index].trim().length > 0
           && !/^\s*([-*+]\s|\d+[.)]\s|#{1,6}\s|>\s?|```)/.test(lines[index])) {
      buffer.push(lines[index].trim());
      index += 1;
    }
    html.push(`<p>${inline(buffer.join(" "))}</p>`);
  }

  closeList();
  return html.join("\n");
}

/** Devuelve los bloques de codigo con su lenguaje, para el boton de copiar. */
export function extractCodeBlocks(markdown) {
  const blocks = [];
  const pattern = /```(\w*)\n([\s\S]*?)```/g;
  let match;
  while ((match = pattern.exec(String(markdown ?? ""))) !== null) {
    blocks.push({ language: match[1] || "", code: match[2].replace(/\n$/, "") });
  }
  return blocks;
}

export { escapeHtml as escapeMarkdownHtml, escapeRegExp };