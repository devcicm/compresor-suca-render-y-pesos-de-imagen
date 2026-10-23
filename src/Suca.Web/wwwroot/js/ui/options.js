/* Lectura y escritura unica de los controles de conversion del Compresor.
   Antes buildForm, readOptionsAsPreset, applyOptionsToSelected y applyPresetToOptions
   repetian el mismo mapeo campo por campo (antipatron DRY). */

import { el } from "../main.js";

const PROFILE_IDS = ["profile-a", "profile-b", "profile-c", "profile-d"];

/** Perfil A-D o null si esta incompleto/fuera de rango. */
export function readProfileInputs() {
  const values = PROFILE_IDS.map((id) => el(id)?.value ?? "");
  if (values.some((value) => String(value).trim() === "")) return null;

  const digits = values.map((value) => Number(value));
  if (digits.some((value) => !Number.isInteger(value) || value < 0 || value > 9)) return null;

  return { a: digits[0], b: digits[1], c: digits[2], d: digits[3] };
}

/** Estado actual de todos los controles, en un unico objeto serializable. */
export function readOptions() {
  return {
    engine: el("engine").value,
    format: el("format").value,
    quality: Number(el("quality").value) || 80,
    maxWidth: el("max-width").value ? Number(el("max-width").value) : null,
    maxHeight: el("max-height").value ? Number(el("max-height").value) : null,
    removeMetadata: el("remove-metadata").checked,
    collection: el("collection").value.trim() || null,
    profile: readProfileInputs(),
    pesoObjetivo: el("peso-objetivo").value.trim() ? Number(el("peso-objetivo").value) : null,
    allowLossy: el("allow-lossy").checked,
    optimize: el("optimize-target").checked
  };
}

/** Aplica valores a los controles (perfil, preset o valores optimos). */
export function applyOptions(options) {
  if (!options) return;

  if (options.engine && el("engine").querySelector(`option[value='${options.engine}']`)) {
    el("engine").value = options.engine;
  }

  if (options.format) {
    el("format").value = options.format;
    el("format").dispatchEvent(new Event("change"));
  }

  if (options.quality) {
    el("quality").value = String(options.quality);
    el("quality").dispatchEvent(new Event("input"));
  }

  el("max-width").value = options.maxWidth ? String(options.maxWidth) : "";
  el("max-height").value = options.maxHeight ? String(options.maxHeight) : "";
  el("remove-metadata").checked = options.removeMetadata !== false;
  el("collection").value = options.collection ?? "";

  if (options.profile) {
    el("profile-a").value = String(options.profile.a);
    el("profile-b").value = String(options.profile.b);
    el("profile-c").value = String(options.profile.c);
    el("profile-d").value = String(options.profile.d);
  }

  el("peso-objetivo").value = options.pesoObjetivo ? String(options.pesoObjetivo) : "";
  el("allow-lossy").checked = options.allowLossy !== false;
  el("optimize-target").checked = Boolean(options.optimize);

  el("engine").dispatchEvent(new Event("change"));
  el("allow-lossy").dispatchEvent(new Event("change"));
  el("peso-objetivo").dispatchEvent(new Event("input"));
}

/** Overrides por item de la cola a partir de un conjunto de opciones. */
export function overridesFromOptions(options) {
  if (!options) return null;

  return {
    engine: options.engine ?? undefined,
    format: options.format ?? undefined,
    quality: options.quality ?? undefined,
    maxWidth: options.maxWidth ?? undefined,
    maxHeight: options.maxHeight ?? undefined,
    collection: options.collection ?? undefined,
    profile: options.profile ?? undefined,
    pesoObjetivo: options.pesoObjetivo ?? undefined,
    allowLossy: options.allowLossy === false ? false : undefined,
    optimize: options.optimize === true ? true : undefined
  };
}

/**
 * Vuelca las opciones en un FormData, mezclando los overrides del item.
 * Un unico lugar decide que se envia y con que nombre.
 */
export function appendOptionsToForm(form, options, overrides = {}) {
  const merge = (key) => (overrides[key] !== undefined ? overrides[key] : options[key]);

  form.append("engine", merge("engine"));
  form.append("format", merge("format"));
  form.append("quality", merge("quality"));
  form.append("removeMetadata", options.removeMetadata ? "true" : "false");

  const profile = overrides.profile ?? options.profile;
  if (profile) {
    form.append("profileA", profile.a);
    form.append("profileB", profile.b);
    form.append("profileC", profile.c);
    form.append("profileD", profile.d);
  }

  if (merge("engine") === "suca") {
    const allowLossy = overrides.allowLossy ?? options.allowLossy;
    form.append("allowLossy", allowLossy ? "true" : "false");

    const peso = merge("pesoObjetivo");
    if (peso) {
      form.append("pesoMoscaKbTarget", peso);
      if (merge("optimize")) form.append("optimize", "true");
    }
  }

  const collection = merge("collection");
  if (collection) form.append("collection", collection);

  const maxWidth = merge("maxWidth");
  const maxHeight = merge("maxHeight");
  if (maxWidth) form.append("maxWidth", maxWidth);
  if (maxHeight) form.append("maxHeight", maxHeight);
}