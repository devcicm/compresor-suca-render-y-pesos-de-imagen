# Minimal Chat OpenAI Style

Modulo minimalista de chat conversacional con RAG y entrega confiable.

El estado persistente se guarda fuera del codigo, en `runtime/chat/`, y debe permanecer ignorado por Git. La interfaz es la vista **Chat** dentro del shell SUCA Studio (`http://127.0.0.1:5077/studio#/chat`); la ruta `/minichat` redirige alli cuando el host se inicia en modo web.

Objetivos:

- exponer un flujo tipo ChatGPT/OpenAI Chat Completions
- mantener conversaciones persistentes
- soportar RAG por carpeta dinamica
- registrar entrega estilo correo: outbox, inbox, acuse, reintento e idempotencia

Conceptos de mensajeria aplicados:

- `message id`: cada mensaje tiene identificador unico
- `conversation id`: equivalente al hilo
- `outbox`: mensajes pendientes de enviar o resolver
- `inbox`: mensajes ya recibidos y consolidados
- `ack`: acuse de entrega al completar persistencia y respuesta
- `retry`: reintentos con backoff ante fallos temporales
- `dead letter`: mensajes que agotaron reintentos
- `checksum`: huella del contenido para trazabilidad

Uso esperado:

- `POST /api/minichat/chat/completions`
- `POST /api/minichat/messages/send`
- `GET /api/minichat/conversations`
- `GET /api/minichat/conversations/{id}`

Las solicitudes pueden incluir configuracion LM Studio y RAG. El corpus y el indice no son fijos: una conversacion puede apuntar a cualquier directorio accesible y a un indice dentro de `runtime/`. Para la operacion completa consulte [Operacion de SUCA](../../docs/operations/README.md).
