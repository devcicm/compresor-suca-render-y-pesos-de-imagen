import { initActivityView } from "../views/activity.js";

document.addEventListener("DOMContentLoaded", () => {
  try {
    initActivityView();
  } catch (error) {
    console.error("Error al inicializar la vista:", error);
  }
});

