import { initGalleryView } from "../views/gallery.js";

document.addEventListener("DOMContentLoaded", () => {
  try {
    initGalleryView();
  } catch (error) {
    console.error("Error al inicializar la vista:", error);
  }
});

