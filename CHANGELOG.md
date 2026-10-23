# Changelog

Todas las novedades de SUCA. El formato sigue [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/)
y el versionado es [SemVer](https://semver.org/lang/es/).

## [No publicado]

Todavia no hay release publicada: este es el estado actual del codigo de `main`.

### Anadido

**Servicio de conversion**
- Patron `Result`/`Result<T>` con codigos de error tipados (`ConversionErrorCodes`) para
  que los errores esperados no viajen como excepciones.
- Maquina de estados de trabajos (`Pending`, `Uploading`, `Queued`, `Running`, `Paused`,
  `Cancelling`, `Completed`, `Failed`, `Cancelled`, `Interrupted`) con historial por
  transicion (`StateHistoryEntry`).
- Conversion en paralelo desde la web y desde consola (`--input <carpeta> --parallel N`)
  con metricas por imagen: formato, dimensiones, EXIF, MPx, bpp, ratio, ahorro, duracion.
- Ordenes orquestadas JSON (`POST /api/web/orders`) para servicios o IAs: descubrimiento
  por carpeta, pausa, reanudacion, cancelacion, eventos por correlacion y recuperacion
  `Interrupted` al reiniciar el servidor.
- Vista previa de validacion (`POST /api/web/orders/preview`): comprueba ruta e imagenes
  sin iniciar nada.
- Explorador de carpetas del servidor en la UI (`GET /api/web/folders`) con conteo de
  imagenes por carpeta.
- Galeria con colecciones: asignar al convertir, mover imagenes, filtrar y recuperar
  JSON enriquecido con **base64** y `dataUrl` (`GET /api/web/collections/{name}/images`).
- CRUD completo de colecciones: crear (incluso vacias), renombrar (actualiza sus
  imagenes) y disolver (mueve a `general` sin borrar archivos), con registro persistente
  en `runtime/web/collections.json`.
- Rutas en disco y apertura en el Explorador (`/path`, `/paths`, `/reveal`,
  `/storage/reveal`).
- **Cancelacion real de conversiones en vuelo** (`POST /api/web/convert/cancel`) por
  `correlationId`: el trabajo se interrumpe y no se registra resultado.
- Shell SUCA Studio multipage (`/studio`, `/studio/galeria`, `/studio/chat`,
  `/studio/ajustes`, `/studio/actividad`) con aside colapsable, tooltips y toasts.
- Edicion de la cola por archivo y en lote: overrides de motor, formato, calidad,
  coleccion y dimensiones, con seleccion multiple y aplicacion en lote.
- Modo consola con lote, progreso y controles `P` pausa, `R` reanuda, `C` cancela.
- Catalogo MCP ampliado a 29 herramientas, incluidas `convert-order`, `order-status`,
  `order-pause|resume|cancel`, `gallery-by-collection`, `image-path` e `image-reveal`.
- Banner de arranque con las URLs de backend y frontend en ambos modos.
- Portabilidad: `SUCA_URLS`, `SUCA_STORAGE_ROOT`, `SUCA_SETTINGS_ROOT` y `SUCA_WEB_ROOT`;
  deteccion automatica de la raiz del repositorio y fallback al binario publicado.

### Corregido

- Reintentos en el commit atomico de archivos cuando `File.Replace` falla de forma
  transitoria.
- Imagenes no soportadas devuelven `422 unsupported-image` en vez de un error interno.
- El overlay de dialogos ya no bloquea los clics cuando se oculta (selector compuesto
  `.overlay[hidden]` en vez de `!important`).
- Pausar durante la subida conserva el estado **Pausada** (antes marcaba la imagen como
  cancelada por la carrera del aborto).
- La pagina de Galeria inicializaba de forma rota por un `await` fuera de funcion
  asincrona al usar la apertura profunda por query string.
- `record-not-found` y `collection-not-found` responden `404` en vez de `400`; los
  duplicados de coleccion responden `409`.
- Las ordenes de consola muestran su `correlationId` para poder rastrearlas.

### Seguridad y privacidad

- Eliminacion de datos de usuario de `runtime/` y reglas para que nunca se versionen.
- Rutas personales anonimizadas en los comentarios de `legacy/`.
- Escaneo de secretos e integracion continua (build en Windows) en GitHub Actions.
- `SECURITY.md` con el modelo de confianza de escritorio local.

### Documentacion

- Documentacion de operacion con el contrato de ordenes, colecciones, rutas, metricas y
  variables de arranque.
- Documentacion de arquitectura, del shell web, del chat y estrategia de pruebas.
- `README.md`, `CONTRIBUTING.md` y seccion de datos y privacidad.