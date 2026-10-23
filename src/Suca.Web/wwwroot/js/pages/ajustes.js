import { initSettingsView } from "../views/settings.js";

document.addEventListener("DOMContentLoaded", () => {
  try {
    initSettingsView();
  } catch (error) {
    console.error("Error al inicializar la vista:", error);
  }
});

