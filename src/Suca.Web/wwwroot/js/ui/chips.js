/* Chips compartidos de la galeria y la cola.
   Una sola fuente para el reporte de integridad, el techo de peso y la legibilidad:
   antes estaba duplicado en compress.js y gallery.js con textos distintos (antipatron DRY). */

import { escapeHtml } from "../main.js";

function num(value, digits = 3) {
  const n = Number(value);
  return Number.isFinite(n) ? n.toFixed(digits) : "-";
}

/** Chip del techo de peso: verde si se cumplio, rojo si no. */
export function pesoObjetivoChip(record) {
  if (!record || record.pesoObjetivoKb === null || record.pesoObjetivoKb === undefined) return "";

  const target = Number(record.pesoObjetivoKb).toFixed(0);
  const ok = record.pesoObjetivoCumplido === true;
  const detail = record.optimized
    ? ` · optimizado: ${record.candidatesEvaluated} candidato(s), escala ${num(record.optimizedScale, 2)}, q${record.optimizedQuality}, score ${num(record.qualityScore)}`
    : "";
  const title = ok
    ? `Peso dentro del techo pedido (${record.pesoIteraciones ?? 0} iteraciones)${detail}`
    : `No se pudo alcanzar el techo con los ajustes disponibles (${record.pesoIteraciones ?? 0} iteraciones)`;

  return `<span class="chip" data-kind="${ok ? "ok" : "bad"}" title="${escapeHtml(title)}">objetivo ${target} KB ${ok ? "cumplido" : "no cumplido"}${record.optimized ? " · optimizado" : ""}</span>`;
}

/** Chip de legibilidad verificada (solo cuando hay reporte). */
export function legibilityChip(record) {
  if (!record || record.legibilityAcceptable === null || record.legibilityAcceptable === undefined) return "";

  return record.legibilityAcceptable
    ? '<span class="chip" data-kind="ok" title="SSIM y bordes por encima del umbral de legibilidad">legible</span>'
    : '<span class="chip" data-kind="bad" title="SSIM o bordes por debajo del umbral: la legibilidad puede verse afectada">legibilidad en riesgo</span>';
}

/** Chips compactos de integridad para la cola y las tarjetas. */
export function integrityChips(record) {
  if (!record || record.integritySsim === null || record.integritySsim === undefined) return "";

  if (record.integrityBitwiseEqual) {
    return '<span class="chip" data-kind="ok" title="SHA-256 identico: sin cambios en los pixeles">sin perdida (bit a bit)</span>';
  }

  const ok = record.integrityVerdict === "ACEPTABLE ESTRUCTURALMENTE";
  const title = `${record.integrityVerdict ?? ""} · bordes ${num(record.integrityEdgeRetention, 2)} · histograma ${num(record.integrityHistogram, 2)}`;

  return `<span class="chip" data-kind="${ok ? "ok" : "warn"}" title="${escapeHtml(title)}">SSIM ${num(record.integritySsim)} · PSNR ${num(record.integrityPsnr, 1)} dB</span>${legibilityChip(record)}`;
}

/** Reporte completo para el panel de detalle. */
export function integrityDetailChips(record) {
  const peso = pesoObjetivoChip(record);

  if (!record || record.integritySsim === null || record.integritySsim === undefined) {
    return `${peso}<span class="chip">sin reporte (motor directo)</span>`;
  }

  if (record.integrityBitwiseEqual) {
    return `${peso}<span class="chip" data-kind="ok">sin perdida: SHA-256 identico</span>`;
  }

  const ok = record.integrityVerdict === "ACEPTABLE ESTRUCTURALMENTE";

  return `
    ${peso}
    <span class="chip" data-kind="${ok ? "ok" : "warn"}">${escapeHtml(record.integrityVerdict ?? "")}</span>
    ${legibilityChip(record)}
    <span class="chip">SSIM ${num(record.integritySsim)}</span>
    <span class="chip">PSNR ${num(record.integrityPsnr, 1)} dB</span>
    <span class="chip">bordes ${num(record.integrityEdgeRetention, 2)}</span>
    <span class="chip">histograma ${num(record.integrityHistogram, 2)}</span>
    <span class="chip">${escapeHtml(record.integrityMode ?? "")}</span>`;
}