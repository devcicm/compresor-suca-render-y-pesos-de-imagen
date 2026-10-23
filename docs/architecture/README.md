# Arquitectura de SUCA

## Estado actual

El repositorio tiene un solo proyecto ejecutable: `ConsoleApp suca compress.csproj`. Las carpetas bajo `src/` marcan propiedad y direccion de dependencias, pero no son aun ensamblados independientes. Esta es una migracion por etapas para conservar la compatibilidad del ejecutable mientras se extrae el codigo.

En particular, `src/Suca.Kernel/SucaCompressionKernel.cs` conserva clases de apoyo heredadas para OCR visual, embeddings, red neuronal local y RAG visual, ademas del punto de entrada `Program`. Esas clases deben extraerse gradualmente a los contextos correspondientes; su ubicacion fisica actual no convierte esas funciones en reglas del dominio de compresion.

## Mapa de contextos

| Ubicacion | Responsabilidad | Regla de dependencia |
| --- | --- | --- |
| `src/Suca.Kernel` | Analisis, decision, codificacion, refinamiento e integridad de compresion. | No conoce HTTP, chat, LM Studio ni RAG documental. |
| `src/Suca.Kernel/Contracts` | Contrato `ISucaCompressionKernel`. | Lo consume Application. |
| `src/Suca.Shared` | Operaciones genericas: senal de imagen, vectores y `SucaFormatConverter` (re-encodado WebP/JPEG/PNG). | No contiene reglas de negocio de SUCA. |
| `src/Suca.Application` | Casos de uso y servicios de aplicacion (ver detalle abajo). | Depende de contratos del kernel y de integraciones, nunca de REST. |
| `src/Suca.Support.Retrieval` | Lectura y normalizacion de texto documental. | No llama al compresor. |
| `src/Suca.Integrations.LmStudio` | Cliente REST, parser JSON, embeddings y administracion de modelos LM Studio. | No conoce el host HTTP de SUCA. |
| `src/Suca.Chat` | Conversaciones, outbox/inbox, ACK, idempotencia, reintentos y dead-letter. | Consume la integracion y RAG mediante contratos. |
| `src/Suca.Api` | Adaptador REST/MCP, launcher WinForms, DI, ciclo de vida y exposicion de endpoints. | Depende de Application y adaptadores, nunca al reves. |
| `src/Suca.Web` | Shell SPA estatico (aside de servicios, vistas, tooltips) servido por el host. | No contiene logica de dominio; habla con la API. |

## Estructura interna de `Suca.Application`

| Carpeta | Contenido |
| --- | --- |
| `Common/` | Patron `Result` / `Result<T>` y codigos de error tipados (`ConversionErrorCodes`). |
| `Conversion/` | `ImageConversionService` (conversion directa y SUCA con Result), `JsonConversionGalleryStore` (manifiesto), **maquina de estados** de trabajos (`ConversionJobStateMachine` + `stateHistory`), **orquestador de ordenes** JSON (`ConversionOrderOrchestrator` con pausa/cancelacion/reanudacion y persistencia por orden) y `ConsoleConversionRunner` (lote en consola con progreso). |
| `Orchestration/` | Coordinador de compresion (locks, staging, commit atomico), locks por recurso, store atomico, jobs administrados y observabilidad. |
| `Artifacts/` | Publicacion opcional de artefactos post-compresion (dataset, RAG visual). |
| `Settings/` | Preferencias de sistema y usuario (`JsonSucaSettingsService`, union efectiva con origen por hoja) y resolucion de rutas de runtime. |

## Direccion de dependencias

```text
REST / MCP / Shell Web / Chat / Consola
                |
          Application (Result, maquina de estados, ordenes, ajustes)
           /        \
    Kernel          Integraciones y retrieval
      |                    |
      +------ Shared ------+
```

El kernel recibe una ruta de imagen, una ruta de salida, `SucaProfile` y `SucaOptions`; entrega `CompressionResult`. No debe crear embeddings, datasets, indices RAG, mensajes de chat ni respuestas HTTP.

La aplicacion coordina las operaciones: bloquea por recurso, propaga `CancellationToken`, usa staging y commit atomico para archivos, registra observabilidad con `correlationId` por trabajo y devuelve resultados con el patron Result (los errores esperados nunca viajan como excepciones). Los endpoints solo adaptan solicitudes HTTP/MCP a esos contratos.

## Servicio de conversion de imagenes

- **Motor directo** (`SucaFormatConverter`, en Shared): re-encodado con calidad y redimensionado.
- **Motor SUCA**: delega en `ISucaCompressionKernel` (perfil A-D, peso mosca, integridad).
- **Cola web**: paralelismo en el cliente con progreso de subida; el servidor es seguro para concurrencia (archivos por GUID, locks por ruta, manifiesto atomico con reintentos).
- **Ordenes**: `ConversionOrderOrchestrator` ejecuta lotes JSON con la misma maquina de estados; cada item conserva `stateHistory` auditable y los items en vuelo al reiniciar quedan `Interrupted (server-restart)`.
- **Galeria y colecciones**: manifiesto `runtime/web/gallery.json`; las colecciones son categorias consultables con JSON enriquecido (metricas, base64).
- **Persistencia local**: `runtime/web/{images,orders,gallery.json}`; Git lo ignora.

## Datos y codigo heredado

- `knowledge/seed/` contiene documentos de ejemplo y puede versionarse.
- `runtime/` contiene indices RAG, conversaciones, galeria, ordenes y ajustes locales; Git lo ignora.
- `legacy/` contiene codigo historico. El archivo de proyecto usa `Compile Remove="legacy/**/*.cs"`, por lo que no participa en la compilacion.

## Frontend (Shell)

`src/Suca.Web/wwwroot` contiene un SPA sin framework: aside de servicios colapsable, router por hash, tootips informativos, toasts y vistas (Compresor, Galeria, Chat, Ajustes, Actividad). Convenciones CSS sin antipatrones (tokens unicos, BEM-lite, sin `!important` ni estilos inline) documentadas en `src/Suca.Web/README.md`.

## Siguientes extracciones

1. Mover OCR, embeddings, mapa de pesos y RAG visual desde `SucaCompressionKernel.cs` a contextos de soporte con contratos propios.
2. Mover `Program` (punto de entrada) fuera del kernel hacia `Suca.Api`, actualizando `StartupObject`.
3. Crear proyectos `.csproj` por contexto solo despues de eliminar referencias circulares y cubrir los contratos con pruebas (candidatos inmediatos: `Result`, `ConversionJobStateMachine`, `SucaFormatConverter`, `JsonSucaSettingsService`).
4. Mantener `Suca.Api` como composition root: registrar implementaciones alli, no en el kernel.
