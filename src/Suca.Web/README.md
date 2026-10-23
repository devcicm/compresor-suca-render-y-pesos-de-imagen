# SUCA Minichat Web

UI web minima para probar el backend `Suca.Chat` desde el mismo host REST. Sus archivos estaticos estan en `src/Suca.Web/wwwroot/`; no contiene logica de dominio ni almacena conversaciones en el repositorio.

Ruta:

- `http://127.0.0.1:5077/minichat/`

Requisitos:

- host SUCA levantado con `--rest`
- LM Studio local escuchando en `http://127.0.0.1:1234`

La UI consume:

- `GET /api/lmstudio/models`
- `GET /api/minichat/defaults`
- `GET /api/minichat/conversations`
- `GET /api/minichat/conversations/{id}`
- `POST /api/minichat/messages/send`

El estado persistente de conversaciones e indices pertenece a `runtime/` y esta ignorado por Git. Consulte [Operacion de SUCA](../../docs/operations/README.md) para iniciar el host, configurar LM Studio y depurar solicitudes.
