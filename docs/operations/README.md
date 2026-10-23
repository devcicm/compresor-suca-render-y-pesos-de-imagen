# Operacion de SUCA

## Launcher de inicio

```powershell
dotnet build "ConsoleApp suca compress.csproj"

# Sin argumentos abre una ventana para elegir el modo.
dotnet run --project "ConsoleApp suca compress.csproj"

# Modo consola: comprime una imagen y ejecuta el flujo local de apoyo.
dotnet run --project "ConsoleApp suca compress.csproj" -- --mode console --input "C:\imagenes\entrada.jpg" --output "C:\imagenes\SUCA_OUT"

# Lote en consola: convierte una carpeta en paralelo con progreso (P pausa, R reanuda, C cancela).
dotnet run --project "ConsoleApp suca compress.csproj" -- --input "C:\imagenes\lote" --parallel 3 --collection facturas

# Orden desde archivo JSON.
dotnet run --project "ConsoleApp suca compress.csproj" -- --order "C:\ordenes\lote.json"

# Modo web: expone REST, MCP y el shell SUCA Studio. Sin --urls arranca en http://127.0.0.1:5077.
dotnet run --project "ConsoleApp suca compress.csproj" -- --mode web --urls http://127.0.0.1:5077
```

`--output` es opcional en modo consola; si se omite, se crea `SUCA_OUT` junto a la imagen de entrada. `Ctrl+C` solicita la cancelacion ordenada y `--timeout-seconds <n>` reemplaza el limite configurado (180 segundos por defecto; vease la seccion de ajustes). Los aliases `console`, `--console`, `web`, `--web`, `serve` y `--rest` siguen siendo validos. Use `--help` para ver la ayuda del launcher. Los comandos `--settings` y `--settings-edit` muestran y editan las preferencias de sistema y usuario sin comprimir nada.

En cualquier modo, el arranque muestra los servicios y las URLs de backend y frontend (Studio, Minichat, MCP y salud).

## Variables de arranque y portabilidad

El repositorio funciona tal cual al clonarse en otra maquina Windows con .NET 8 SDK: no hay rutas absolutas versionadas y todo lo local vive en `runtime/` (gitignored). La raiz del repo se detecta buscando el `.csproj` hacia arriba desde el directorio actual o el binario publicado; si no se encuentra (paquete publicado), el estado queda junto al ejecutable.

Variables de entorno reconocidas (todas opcionales):

| Variable | Efecto | Por defecto |
| --- | --- | --- |
| `SUCA_URLS` | URLs del host web; precedencia: CLI `--urls` > `SUCA_URLS` > `ASPNETCORE_URLS` > default | `http://127.0.0.1:5077` |
| `SUCA_STORAGE_ROOT` | Carpeta de estado web (galeria, ordenes, chat de conversion) | `<repo>/runtime/web` |
| `SUCA_SETTINGS_ROOT` | Carpeta de ajustes de sistema y usuario | `<repo>/runtime/settings` |
| `SUCA_WEB_ROOT` | Carpeta de assets del shell (`wwwroot`); util si el layout publicado no incluye `src/Suca.Web` | autodeteccion |

Ejemplo de arranque portable con estado externo:

```powershell
$env:SUCA_URLS = "http://127.0.0.1:8080"
$env:SUCA_STORAGE_ROOT = "D:\suca-estado\web"
$env:SUCA_SETTINGS_ROOT = "D:\suca-estado\settings"
dotnet run --project "ConsoleApp suca compress.csproj" -- --mode web
```

El banner de arranque refleja la URL efectiva (incluida `SUCA_URLS`), por lo que en consola y web siempre se ven backend y frontend reales. Los ajustes persistentes (motor por defecto, paralelismo, LM Studio, RAG) viven en `runtime/settings` y se resuelven con precedencia CLI > usuario > sistema > default, de modo que cada instalacion puede configurarse sin tocar codigo.

El modo web sirve el shell SUCA Studio en `http://127.0.0.1:5077/studio`; `/minichat` redirige al chat dentro del shell. Compruebe que el proceso inicio con `GET /health` antes de enviar operaciones.

## SUCA Studio: shell de servicios

La interfaz `/studio` es un conjunto de paginas minimalistas con un aside lateral colapsable (riel de iconos; estado persistido) y cinco servicios navegables como paginas reales:

- **Compresor SUCA** (`/studio/`, vista inicial): seleccion con la ventana del navegador (dropzone `<label>` nativo) o arrastrar y soltar (multiples archivos y carpetas), cola con **conversion en paralelo** y **progreso real por archivo** (subida 0-55% por bytes, luego etapa y porcentaje del servidor con heartbeat), barra agregada, pausa/cancelacion/reanudacion por archivo y global, chips de metricas de entrada/salida y tarjeta avanzada para enviar **ordenes orquestadas** sobre carpetas del servidor. El motor por defecto es **Pipeline SUCA** (configurable a Directo).
- **Explorador de carpetas del servidor y validacion** (pantalla Compresor, tarjeta avanzada): boton **Explorar...** abre un dialogo que navega el sistema de archivos donde corre SUCA (unidades y subcarpetas con conteo de imagenes por carpeta; endpoint `GET /api/web/folders`), y el boton **Validar** envia `POST /api/web/orders/preview` que comprueba la ruta **antes de convertir** y devuelve conteo, peso total, paralelismo y ejemplos de nombres; si la ruta no existe o no contiene imagenes, se muestra el error (`path-not-found`, `empty-order`) sin iniciar nada.
- **Edicion de la cola** (pantalla Compresor): cada pendiente ofrece casillas y un panel **Editar opciones de esta imagen** (motor, formato, calidad, coleccion) con aviso visible `override: ...`; la barra de la cola agrega **Sel. todo**, **Aplicar opciones a seleccionadas** (copia los controles globales a todos los marcados), **Quitar seleccionadas** y **Quitar** por archivo. La pausa durante el vuelo deja el archivo en `Pausada` y al reanudarlo lo reintenta desde cero.
- **Galeria** (`/studio/galeria`): resultados agrupados por **coleccion** (categoria), descarga, copia de base64 y rutas, apertura en el Explorador, detalle con metricas completas y eliminacion. La tarjeta **Gestionar colecciones** da el CRUD completo: crear coleccion vacia, renombrar (actualiza todas sus imagenes) y disolver (pasa todo a `general` sin borrar archivos); `general` esta protegida y los duplicados devuelven `collection-exists` (HTTP 409).
- **Chat (Minichat)** (`/studio/chat`): chat LLM con RAG integrado al shell (requiere LM Studio).
- **Ajustes** (`/studio/ajustes`): preferencias de sistema y usuario con el origen de cada valor (default/sistema/usuario).
- **Actividad** (`/studio/actividad`): ordenes orquestadas (pausa/reanuda/cancela) y eventos de trazabilidad con filtro por correlacion.

Los elementos de interfaz incluyen tooltips informativos (puntos `?`) que explican cada control.

El shell se sirve con `Cache-Control: no-cache, no-store, must-revalidate`, de modo que el HTML y los modulos JS siempre corresponden a la misma version. Si el navegador conserva un `index.html` anterior (dropzone como `div`, `#browse-button`), el selector de archivos no abre; un refresco forzado (`Ctrl+F5`) lo resuelve y ya no deberia repetirse.

## Conversion orquestada (ordenes JSON)

Para servicios o agentes (por ejemplo una IA) que necesitan convertir multiples archivos del servidor:

```http
POST /api/web/orders
{
  "sourceDirectory": "C:\\imagenes\\lote",
  "recursive": true,
  "extensions": [".png", ".jpg"],
  "items": [ { "path": "C:\\imagenes\\a.png", "format": "webp", "quality": 85 } ],
  "defaults": { "engine": "suca", "format": "webp", "quality": 80, "removeMetadata": true,
                "collection": "facturas" },
  "output": { "mode": "gallery" },
  "parallelism": 3
}
```

- `items` (rutas explicitas) **o** `sourceDirectory` (descubrimiento con filtros); maximo 500 imagenes por orden.- Respuesta `202` con `orderId`, `correlationId` y `statusUrl`.
- Seguimiento y control: `GET /api/web/orders`, `GET /api/web/orders/{id}`, `POST /api/web/orders/{id}/pause|resume|cancel` y `GET /api/web/orders/{id}/events` (trazabilidad por correlacion).
- Cada item sigue la maquina de estados `Pending → Running → Completed/Failed/Cancelled/Interrupted` con `stateHistory` persistido; al reiniciar el servidor, los items en vuelo quedan `Interrupted` con codigo `server-restart`.
- `output.mode = "directory"` copia ademas los convertidos a la carpeta indicada.
- **Correlacion de punta a punta**: la orden lleva `correlationId` (del cliente en el JSON o generada `SRV...`); cada item ejecuta su compresion con la correlacion derivada `{correlationId}.{itemId8}`, que queda estampada en (a) el registro de galeria, (b) los eventos de observabilidad (`order-item ... gallery-record={id}` une orden con resultado) y (c) `GET /api/web/orders/{id}/events`, que agrega los eventos cuyo `correlationId` coincida con el de la orden o empiece por `{correlationId}.`. Los eventos en memoria (`RuntimeObservability`) viven mientras corre el servidor; la trazabilidad fuera de linea queda en `stateHistory` del JSON de orden. El lote por CLI fija su propia correlacion legible `console-{fecha}` y la imprime al iniciar.
- **Pausa por archivo y conversion en vuelo**: la conversion se registra en el servidor por su `correlationId`; `POST /api/web/convert/cancel` (`{"correlationId":"..."}`) emite la cancelacion real del trabajo en curso (`202 Accepted`, o `404 record-not-found` si ya termino), la conversion responde `cancelled` y **no** se registra en la galeria. El boton de cancelar y la pausa global de la cola lo usan automaticamente cuando la subida ya llego al servidor.
- **Progreso real y heartbeat**: mientras la conversion corre, `GET /api/web/convert/progress?correlationId=...` devuelve `percent` (0-100), `phase` (`preparando`, `analizando`, `decidiendo`, `cargando`, `redimensionando`, `codificando`, `optimizando`, `verificando`, `registrando`, `listo`, `error`, `cancelada`), `detail`, `elapsedMs`, `sinceUpdateMs` (antiguedad de la etapa) y `heartbeatMs` (edad del ultimo latido del request, refrescado cada segundo). Una etapa larga se ve como `sinceUpdateMs` creciente con `heartbeatMs` bajo; un cuelgue real se ve como `heartbeatMs` creciente. El endpoint responde `404` si la correlacion ya expiro (los estados terminados se conservan 2 minutos). La UI sondea cada 500 ms y muestra `57% · codificando · 3.2 s · vivo`.
- **Limites que evitan cuelgues**: `web.maxUploadMegabytes` (50 MB por defecto) responde `413` antes de leer el archivo, y una imagen de mas de **120 MP** se rechaza con `too-large` (HTTP 413) usando solo la cabecera (`Image.Identify`), sin decodificarla. El cliente corta la espera a `web.convertTimeoutSeconds + 20 s`; el servidor cancela a `web.convertTimeoutSeconds` (180 s por defecto). Una subida sin bytes durante 30 s o un servidor que deja de informar progreso durante 25 s se abortan con un mensaje explicito.
- Estado y errores usan el patron Result: `errorCode` tipado (`path-not-found`, `empty-order`, `unsupported-image`, `too-many-items`, `too-large`, `cancelled`, ...). Los codigos `record-not-found` y `collection-not-found` responden HTTP 404; `collection-exists`, 409.
- `POST /api/web/orders/preview` acepta el mismo cuerpo y **no inicia nada**: valida ruta/imagenes/parallelism respondiendo `source`, `itemCount`, `totalBytes`, `parallelism` y los primeros `firstItems`; si falla devuelve el `errorCode` (`path-not-found` cuando la carpeta no existe, `empty-order` cuando no hay imagenes). La UI lo usa en el boton **Validar**.

Persistencia: `runtime/web/orders/{orderId}.json` (ignorado por Git).

## Colecciones de galeria

Una coleccion es una categoria (ej.: `facturas`, `lote-shell`). Se asigna al subir (campo `collection`), al enviar una orden (`defaults.collection` o, si se omite, `order-{id}`) y desde consola (`--collection`).

- `GET /api/web/collections`: catalogo con conteos y metricas agregadas (incluye colecciones vacias creadas explicitamente).
- `POST /api/web/collections` (body `{"name":"lote-x"}`): crea una coleccion vacia. `201` si se creo; `409` `collection-exists` si ya existe; `400` `invalid-collection` si el nombre es invalido o es `general` (reservada).
- `PUT /api/web/collections/{name}` (body `{"newName":"nuevo"}`): renombra y actualiza todas sus imagenes y el registro; mismos errores (`collection-exists`, `collection-not-found`, `invalid-collection` para `general` o nombre vacio).
- `GET /api/web/collections/{name}/images`: JSON enriquecido con grupos `input` (formato, dimensiones, bytes, MPx, EXIF), `output` (formato, calidad, bpp, ratio, alpha, duracion), `metrics`, `links`, y **base64/dataUrl** de la imagen convertida. Parametros: `includeBase64` (default `true`), `originalBase64` (default `false`), `page`, `pageSize`.
- **JSON legible por defecto**: los enums se serializan como texto (`"state": "Completed"`,
  no `6`) tanto en la API HTTP como en la salida de MCP, para que un servicio o una IA
  entienda el estado sin tabla de equivalencias. Los archivos ya escritos siguen leyendose.
- `GET /api/web/images` acepta `page` y `pageSize` (1-200; si se omiten devuelve todo para no romper clientes existentes) e incluye `total`, `page`, `pageSize` e `items`.
- `PUT /api/web/images/{id}/collection` (body `{"collection":"nombre"}`): reasignar (registra la coleccion si era nueva).
- `DELETE /api/web/collections/{name}?confirm=true`: disuelve la coleccion; las imagenes pasan a `general` (no borra archivos).

Toda coleccion referenciada (al crear, mover imagenes o convertir) se registra en `runtime/web/collections.json` (ignorado por Git), asi que `ListCollections` muestra tambien colecciones sin imagenes y este catalogo sobrevive a reinicios.
- **Rutas y Explorador** (herramienta local de escritorio): `GET /api/web/images/{id}/path?variant=`, `POST /api/web/images/paths` (`{ids, variant}`), `GET /api/web/collections/{name}/paths?variant=`, `POST /api/web/images/{id}/reveal?variant=` (abre el Explorador con el archivo seleccionado) y `POST /api/web/storage/reveal` (abre `runtime/web/images`). Via MCP: `image-path`, `image-reveal` y `collection-paths` (incluye `joined` con todas las rutas separadas por salto de linea).
  La apertura usa la **API nativa de Windows** `SHOpenFolderAndSelectItems` (la misma que "Mostrar en carpeta"), que devuelve un HRESULT real; si falla, cae en cascada a `explorer.exe /select` y luego a abrir la carpeta contenedora. Si no hay escritorio interactivo (servicio, contenedor, sesion 0) o SUCA corre **elevado**, lo informa en vez de fingir exito y devuelve `path` en el error, de modo que la UI copia la ruta al portapapeles.
- **Diagnostico del Explorador**: `GET /api/web/diagnostics/reveal?path=` informa `sessionId`, `interactive`, `elevated`, `explorerExists`, `explorerProcesses`, `pathExists`, el resultado real de la API (`revealed`) y una nota con la causa probable. Es el primer sitio donde mirar si "Abrir carpeta" no muestra ventana.

## Metricas por imagen

- **Rendimiento y optimizacion**: el motor SUCA procesa en paralelo desde la cola. Con
  `optimize=true` cada imagen se codifica varias veces (busqueda escala x calidad), asi que
  el lote tarda mas; la UI lo indica en el tooltip. Para volumen alto, usar el modo simple
  (solo `pesoMoscaKbTarget`) y reservar la optimizacion para imagenes criticas.

Cada conversion registra y expone: formato y dimensiones de entrada y salida, bytes, megapixeles, presencia de EXIF, bits por pixel, ratio de compresion, porcentaje de ahorro, duracion (ms), motor, **calidad aplicada** (en SUCA es la que realmente uso el kernel: puede ser menor que la pedida si el techo de peso la bajo), hash SUCA (modo suca), estado, `correlationId` e historial de estados. La calidad pedida por el usuario se respeta en el kernel SUCA; los suelos por tipo de contenido (documento/UI/huella) solo aplican cuando no se pidio una calidad explicita.

**Integridad perceptual (motor SUCA)**: el kernel genera un reporte que ahora viaja con el registro y se muestra en la UI: `integritySsim`, `integrityPsnr` (dB), `integrityEdgeRetention`, `integrityHistogram`, `integrityMode` (`perceptual` o `lossless-strong`), `integrityVerdict` y `integrityBitwiseEqual`. Criterio de aceptacion: `SSIM >= 0.88`, bordes `>= 0.80` y histograma `>= 0.84`. `integrityBitwiseEqual = true` solo cuando los SHA-256 coinciden (sin perdida real). El motor directo no emite reporte. Detalle matematico en [docs/math/suca-formulas.md](../math/suca-formulas.md).

**Techo de peso (peso mosca)**: `pesoMoscaKbTarget` es **opt-in**; sin el, la escala la decide el perfil A-D. Cuando se pide, el kernel lo hace cumplir (baja calidad y reduce escala con paso geometrico; si sobra margen, sube calidad) y el registro informa `pesoObjetivoKb`, `pesoObjetivoCumplido` y `pesoIteraciones`. La UI muestra un chip verde "objetivo N KB cumplido" o rojo "no cumplido".

**Optimizacion bajo el techo (`optimize=true`)**: explora varias escalas y, para cada una, la maxima calidad que cabe; elige la combinacion con mejor puntuacion (prioriza retencion de bordes en documento/UI/huella/grafico y SSIM en fotos). El registro anade `optimized`, `optimizedScale`, `optimizedQuality`, `candidatesEvaluated`, `qualityScore` y `legibilityAcceptable` (SSIM >= 0.90 y, en contenido textual, bordes >= 0.70). Es mas lento (varias codificaciones por imagen) y solo aplica al motor SUCA con techo de peso.

## Ajustes de sistema y usuario

Precedencia: CLI > usuario > sistema > valores por defecto. Los archivos viven en `runtime/settings/` (`system.json` y `users/<usuario>.json`); Git los ignora. Los valores que requieren reinicio se marcan en la respuesta con `requiresRestart`. Grupos: `conversion` (motor `suca|directo`, formato, calidad, carpeta de salida), `web` (subida maxima, pagina de galeria, timeout, paralelismo de conversion), `console` (timeout, paralelismo de lote, carpeta SUCA_OUT), `lmstudio` y `rag`.

- `GET /api/settings/effective` devuelve la union con `sources` (origen de cada hoja).
- `GET /api/settings/{scope}` devuelve el JSON crudo del scope (`system` o `user`).
- `PUT /api/settings/{scope}` combina un JSON parcial sobre el scope.
- `POST /api/settings/{scope}/reset` elimina el archivo del scope.
- `--settings` (consola) imprime la configuracion efectiva; `--settings-edit` abre un editor interactivo del scope usuario con claves `seccion.propiedad` (ej.: `conversion.quality=85`).

## REST y MCP

Los grupos REST principales son:

- `POST /api/tools/compress` y `POST /api/tools/compress/jobs` para compresion sincronica o en segundo plano.
- `POST /api/tools/ocr`, `/weight-map`, `/embedding` y `/local-rag/query` para capacidades visuales locales.
- `GET /api/lmstudio/models` y `POST /api/tools/lmstudio/*` para modelos, RAG, vision e insight mediante LM Studio.
- `GET /api/minichat/*` y `POST /api/minichat/*` para conversaciones y entrega de mensajes.
- `GET /api/lmstudio/health` informa si el servicio externo responde (`available`/`unavailable` con `baseUrl` y detalle) para que la UI muestre su banner; `GET /api/lmstudio/models` devuelve **503** con detalle cuando LM Studio esta caido.
- `POST /api/minichat/chat/stream` emite la respuesta en **SSE** (`text/event-stream`): un `data:` por linea con eventos `Started`, `Context` (citas RAG), `Delta`, `Completed` y `Error`, terminando en `data: [DONE]`. Cancelar la peticion HTTP detiene la generacion en LM Studio y el mensaje se persiste con estado `Failed`.
- Gestion de conversaciones: `GET /api/minichat/conversations?search=` (busca en titulo y mensajes), `PATCH /api/minichat/conversations/{id}` (body `{"title":"..."}`) y `DELETE /api/minichat/conversations/{id}` (elimina tambien las colas inbox/outbox).
- `POST /api/web/convert` (multipart con `engine`, `format`, `quality`, `maxWidth`, `maxHeight`, `removeMetadata`, `collection`, `correlationId`, el **perfil SUCA** `profileA|profileB|profileC|profileD` de 0 a 9 — los cuatro juntos, si no se usa el perfil por defecto 0-1-1-3 —, `pesoMoscaKbTarget` (1-20000, techo de peso) y `allowLossy` (`false` fuerza PNG sin perdida de codec)), `GET /api/web/images[?collection=&page=&pageSize=]`, `GET /api/web/images/{id}/file`, `GET /api/web/images/{id}/base64` y `DELETE /api/web/images/{id}` para el servicio de conversion, galeria y recuperacion base64. `GET /api/web/convert/progress?correlationId=...` expone porcentaje, etapa y heartbeat de una conversion en vuelo. `POST /api/web/convert/cancel` (`{"correlationId":"..."}`) cancela de verdad una conversion en vuelo: el trabajo se interrumpe y no se registra en la galeria.
- Las formulas del motor SUCA (matriz de factorizacion, hash, calidad, peso mosca, bpp/ratio e integridad) estan documentadas en [docs/math/suca-formulas.md](../math/suca-formulas.md). El perfil A-D tambien viaja en el payload JSON del tool MCP `convert` (`"profile": { "a": 0, "b": 1, "c": 1, "d": 3 }`), junto con `pesoMoscaKbTarget` y `allowLossy`.
- Acciones en lote de la galeria: `POST /api/web/images/export` (body `{"ids":[...],"variant":"converted"}`) devuelve un ZIP (`application/zip`, nombres deduplicados; con `variant=original` los archivos van en la subcarpeta `original/`) y `POST /api/web/images/collection` (body `{"ids":[...],"collection":"nombre"}`) mueve varias imagenes registrando la coleccion destino. Maximo 500 ids por peticion.
- `POST /api/web/orders` y sus endpoints de estado/control (ver seccion de ordenes); `GET /api/web/folders?path=` para el explorador de carpetas del servidor y `POST /api/web/orders/preview` para validar antes de convertir.
- `GET /api/web/collections`, `POST /api/web/collections`, `PUT /api/web/collections/{name}`, `GET /api/web/collections/{name}/images`, `PUT /api/web/images/{id}/collection` y `DELETE /api/web/collections/{name}` para el CRUD de colecciones.
- `GET /api/settings/effective`, `GET|PUT /api/settings/{scope}` y `POST /api/settings/{scope}/reset` para los ajustes de sistema y usuario.
- **Presets de conversion**: `conversion.presets[]` con `name`, `engine`, `format`, `quality`, `maxWidth`, `maxHeight`, `removeMetadata` y `collection`. Se editan con `PUT /api/settings/user` (UI en Compresor: guardar, aplicar a la cola, aplicar a las marcadas o borrar) y se validan en el servidor: maximo 24 presets, nombre unico de 1-40 caracteres, calidad 1-100, formato `webp|jpeg|png`, motor `suca|direct` y dimensiones 1-20000.

El adaptador MCP publica sus contratos en `GET /mcp/tools` y `GET /mcp/resources`. Herramientas: `convert` (con `contentBase64`), `gallery-list`, `image-base64`, `convert-order`, `order-status`, `order-pause`, `order-resume`, `order-cancel`, `gallery-collections`, `gallery-by-collection` (acepta `collection`, `includeBase64`, `originalBase64`, `page`, `pageSize`), `settings-get` y `settings-update`. Recursos: `gallery.images`, `gallery.collections`, `orders` y `settings.effective`. Una IA puede enviar una orden, encuestar su estado y recuperar resultados en base64 sin interfaz web.

Las respuestas de las herramientas LM Studio se transforman a objetos estructurados por el parser de `Suca.Integrations.LmStudio`, no se devuelven solo como texto crudo.

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
