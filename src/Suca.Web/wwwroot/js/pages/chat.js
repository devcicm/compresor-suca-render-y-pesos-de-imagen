import { initChatView } from "../views/chat.js";

document.addEventListener("DOMContentLoaded", () => {
  try {
    initChatView();
  } catch (error) {
    console.error("Error al inicializar la vista:", error);
  }
});
