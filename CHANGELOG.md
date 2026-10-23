# Changelog

Todas las novedades de SUCA. El formato sigue [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/)
y el versionado es [SemVer](https://semver.org/lang/es/).

## [No publicado]

Todavia no hay release publicada: este es el estado actual del codigo de `main`.

### Anadido

**Motor SUCA: optimizacion de la conversion bajo un techo de peso**
- **Modo optimizado** (`optimize=true` en REST/MCP y casilla "Buscar la mejor calidad que
  quepa" en la UI): explora varias **escalas** y, para cada una, la **maxima calidad que
  cabe**; elige la combinacion con mejor puntuacion, priorizando retencion de bordes en
  documento/UI/huella/grafico y SSIM en fotos. Reporta `optimized`, `optimizedScale`,
  `optimizedQuality`, `candidatesEvaluated` y `qualityScore`.
- **Legibilidad verificada** (`legibilityAcceptable`): SSIM >= 0.90 y, en contenido
  textual, bordes >= 0.70. Si el techo se cumple pero la legibilidad no, la UI lo marca en
  rojo.
- Medicion: con el mismo techo de 80 KB, la foto de prueba pasa de 1016x1524 (simple) a
  **1440x2160** (optimizada): +41% de pixeles a igual peso. En un documento de 60 KB pasa de
  1152x1584 a **1360x1870 con q92**.

**Motor SUCA documentado y configurable**
- Nuevo `docs/math/suca-formulas.md`: las formulas completas del kernel (senales visuales,
  matriz de factorizacion A-D, hash SUCA, calidad base, peso mosca, bpp/ratio, integridad
  perceptual y mapa de pesos 8x8) con ejemplo numerico paso a paso.
- **Perfil SUCA A-D** configurable en la conversion: `POST /api/web/convert` acepta
  `profileA|profileB|profileC|profileD` (0-9, los cuatro juntos) y el tool MCP `convert`
  acepta `"profile": { "a": 0, "b": 1, "c": 1, "d": 3 }`. La UI del Compresor anade los
  cuatro digitos con tooltip. Sin perfil se usa el 0-1-1-3 del kernel.

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

- **El techo de peso no se cumplia**: con objetivo 80 KB un archivo de 3.29 MB salia en
  495 KB. El bucle hacia 8 pasos fijos de `x0.94` (maximo 0.61 de reduccion) y se rendia en
  silencio. Ahora baja calidad y reduce escala con paso geometrico `sqrt(objetivo/peso)`
  (1-3 iteraciones), sube calidad si sobra margen y publica `pesoObjetivoCumplido` para que
  la UI marque el incumplimiento. El techo es **opt-in**: sin `pesoMoscaKbTarget` manda el
  perfil A-D.
- **Fuga de informacion en errores 500**: el manejador global devolvia `detail` con el stack
  trace completo. Ahora el stack va al log del servidor y la respuesta es un mensaje limpio.
- **Cuerpo demasiado grande devolvia 500**: `BadHttpRequestException` del limite de subida
  ahora responde **413** con mensaje claro.
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

### Cambiado

**UI del Compresor: configuracion sin antipatrones**
- Opciones agrupadas en `fieldset`/`legend` (Peso y calidad · Salida) y perfiles movidos a
  `<details>`: menos ruido, divulgacion progresiva.
- Nuevo panel **"Valores optimos"**: muestra lo que eligio el optimizador (dimensiones,
  calidad, escala, candidatos, score, peso, legibilidad) y permite **aplicarlo a pendientes**
  o **guardarlo como perfil**.
- Nuevos modulos compartidos para evitar duplicacion (DRY): `js/ui/options.js` (mapeo unico
  de los controles), `js/ui/chips.js` (integridad/techo/legibilidad) y `js/ui/dialog.js`
  (confirmaciones y prompts propios).
- Se eliminaron los **9 `window.confirm`/`window.prompt`** del shell y los **4 estilos inline**
  restantes; las confirmaciones destructivas usan un dialogo con foco atrapado, `Escape` y
  etiqueta explicita.

- **SixLabors.ImageSharp fijado a 2.1.13** (Apache-2.0, gratuita y compatible con la
  licencia MIT): la linea 3.x exige licencia comercial de Six Labors en compilacion y sus
  versiones flotantes (`3.*`) rompian la CI y la reproducibilidad. Se elimino
  `SixLabors.ImageSharp.Drawing` (no se usaba). Los avisos de seguridad pendientes de la
  serie 2.x estan documentados y justificados en `SECURITY.md`.

### Seguridad y privacidad

- **Apertura del Explorador corregida**: `explorer.exe` se lanza con ruta absoluta y
  `UseShellExecute = true`; sin esa opcion el proceso se creaba fuera de la sesion del shell
  y no aparecia ninguna ventana. Ahora hay cascada (seleccionar archivo, luego carpeta) y,
  si no hay escritorio interactivo, se informa y se devuelve la ruta para copiarla.
- Eliminacion de datos de usuario de `runtime/` y reglas para que nunca se versionen.
- Rutas personales anonimizadas en los comentarios de `legacy/`.
- Escaneo de secretos e integracion continua (build en Windows) en GitHub Actions.
- `SECURITY.md` con el modelo de confianza de escritorio local y la guia de diagnostico
  del Explorador.

### Documentacion

- Documentacion de operacion con el contrato de ordenes, colecciones, rutas, metricas y
  variables de arranque.
- Documentacion de arquitectura, del shell web, del chat y estrategia de pruebas.
- `README.md`, `CONTRIBUTING.md` y seccion de datos y privacidad.
- **Licencia MIT** (`LICENSE`) y avisos de terceros (`THIRD-PARTY-NOTICES.md`).