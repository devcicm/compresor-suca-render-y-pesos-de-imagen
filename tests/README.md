# Estrategia de pruebas

Todavia no hay proyectos de pruebas automatizadas en el repositorio. Al crear las suites, deben reflejar estos limites:

- `Suca.Kernel.Tests`: decisiones de compresion, formatos e integridad.
- `Suca.Application.Tests`: orquestacion, cancelacion, locks y escritura atomica.
- `Suca.Integrations.LmStudio.Tests`: cliente, parser JSON y RAG con servidor simulado.
- `Suca.Api.IntegrationTests`: contratos REST y MCP de extremo a extremo.

No use un servidor LM Studio real para pruebas unitarias. Simule sus respuestas para cubrir variantes de JSON, cancelacion, reintentos y errores de red de forma determinista.

## Unidades listas para cubrir (prioridad sugerida)

Estas piezas son puras o deterministas y ya existen; son los candidatos inmediatos:

1. `ConversionJobStateMachine`: transiciones validas/invalidas, estados terminales, `Result` en transicion invalida.
2. `Result` / `Result<T>`: contratos Ok/Fail y codigos de error.
3. `SucaFormatConverter`: conversion WebP/JPEG/PNG, calidad fuera de rango, redimensionado, `TryParseFormat` (incluye `web` como alias).
4. `JsonSucaSettingsService`: union efectiva usuario > sistema > default, `sources`, validacion (rangos, engine, formato) y reset.
5. `ImageConversionService` (con kernel simulado): `Result` de conversion, codigos (`empty-file`, `unsupported-image`, `invalid-format`), metricas (bpp, ratio, duracion), colecciones (asignar, disolver, migracion a `general`) y rutas en disco.
6. `ConversionOrderOrchestrator`: alta de orden por items y directorio, limites (`empty-order`, `too-many-items`, `path-not-found`), pausa/reanudar/cancelar, derivacion de estado y recuperacion `server-restart`; `PreviewAsync` reproduce la misma validacion sin iniciar ejecucion.
7. `JsonConversionGalleryStore` / `JsonConversionOrderStore`: escritura atomica, reintentos de `File.Replace` y concurrencia (varios escritores simultaneos no pierden registros).
8. `CollectionsRegistryStore` + CRUD de `ImageConversionService`: crear coleccion vacia (y duplicado `collection-exists`), renombrar (mueve registros y registro), disolver (mueve a `general` y elimina del registro), coleccion `general` reservada, mezcla del registro en el listado y autorregistro al convertir/mover.
9. Cancelacion por correlacion en `ImageConversionService`: `CancelConversion` sobre una conversion en vuelo produce `Cancelled` (sin registro en galeria), y devuelve `record-not-found` si ya termino; el mapa se limpia al finalizar cada conversion.

Convenciones para las suites:

- Simule `ISucaCompressionKernel`, el sistema de archivos temporal y los relojes cuando haga falta; nunca dependa de `runtime/` real.
- Cubra siempre el par exito/fracaso con `errorCode` en cada endpoint de conversion.
- Las pruebas de integracion del host deben verificar el catalogo MCP (`/mcp/tools`) como fuente de verdad de los contratos.
