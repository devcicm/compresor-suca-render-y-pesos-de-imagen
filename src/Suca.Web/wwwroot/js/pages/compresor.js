import { initCompressView } from "../views/compress.js";

document.addEventListener("DOMContentLoaded", () => {
  try {
    initCompressView();
  } catch (error) {
    console.error("Error al inicializar la vista:", error);
  }
});

