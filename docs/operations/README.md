# Operacion de SUCA

## Launcher de inicio

```powershell
dotnet build "ConsoleApp suca compress.csproj"

# Sin argumentos abre una ventana para elegir el modo.
dotnet run --project "ConsoleApp suca compress.csproj"

# Modo consola: comprime una imagen y ejecuta el flujo local de apoyo.
dotnet run --project "ConsoleApp suca compress.csproj" -- --mode console --input "C:\imagenes\entrada.jpg" --output "C:\imagenes\SUCA_OUT"

# Modo web: expone REST, MCP y el minichat.
dotnet run --project "ConsoleApp suca compress.csproj" -- --mode web --urls http://127.0.0.1:5077
```

`--output` es opcional en modo consola; si se omite, se crea `SUCA_OUT` junto a la imagen de entrada. `Ctrl+C` solicita la cancelacion ordenada y `--timeout-seconds <n>` reemplaza el limite predeterminado de tres minutos. Los aliases `console`, `--console`, `web`, `--web`, `serve` y `--rest` siguen siendo validos. Use `--help` para ver la ayuda del launcher.

El modo web sirve el minichat en `http://127.0.0.1:5077/minichat/`. Compruebe que el proceso inicio con `GET /health` antes de enviar operaciones.

## REST y MCP

Los grupos REST principales son:

- `POST /api/tools/compress` y `POST /api/tools/compress/jobs` para compresion sincronica o en segundo plano.
- `POST /api/tools/ocr`, `/weight-map`, `/embedding` y `/local-rag/query` para capacidades visuales locales.
- `GET /api/lmstudio/models` y `POST /api/tools/lmstudio/*` para modelos, RAG, vision e insight mediante LM Studio.
- `GET /api/minichat/*` y `POST /api/minichat/*` para conversaciones y entrega de mensajes.

El adaptador MCP publica sus contratos en `GET /mcp/tools` y `GET /mcp/resources`. Para invocar una herramienta use `POST /mcp/tools/{name}`; los nombres publicados son la fuente de verdad. Las respuestas de las herramientas LM Studio se transforman a objetos estructurados por el parser de `Suca.Integrations.LmStudio`, no se devuelven solo como texto crudo.

## RAG dinamico

`knowledge/seed/documents` es un corpus de ejemplo, no una carpeta obligatoria ni una estructura estatica. Una operacion puede indexar cualquier directorio al que tenga acceso el proceso usando:

- `POST /api/tools/lmstudio/rag/index-folder` para crear o reemplazar un indice.
- `POST /api/tools/lmstudio/rag/ask-folder` para consultar; puede reconstruir el indice antes de responder con `refreshIndexBeforeQuery`.

La solicitud recibe `sourceDirectory`, `semanticIndexPath`, `recursive`, `chunkSize`, `chunkOverlap` y, de forma opcional, `extensions`. Guarde `semanticIndexPath` bajo `runtime/indexes/`; esos archivos son datos generados y Git los ignora. El minichat usa por defecto `knowledge/seed/documents` y `runtime/indexes/minichat-rag-index.json`, pero cada solicitud puede elegir otro corpus e indice.

LM Studio debe estar ejecutando una API compatible con OpenAI, normalmente en `http://127.0.0.1:1234`. Indique en cada solicitud los modelos de chat y embeddings que esten cargados localmente; consulte primero `GET /api/lmstudio/models`.

## Depuracion y ciclo de vida

- `GET /api/observability/snapshot` resume operaciones, fallos, cancelaciones y jobs activos.
- `GET /api/observability/events` expone los eventos recientes y su correlacion.
- `GET /api/jobs` y `GET /api/jobs/{id}` muestran trabajos asincronos; `POST /api/jobs/{id}/cancel` solicita su cancelacion.

El host propaga `CancellationToken` desde HTTP, cancela jobs pendientes durante el apagado y libera recursos mediante `IDisposable` o `IAsyncDisposable`. El coordinador de compresion usa staging, locks por ruta y un commit atomico para no exponer archivos parciales.
