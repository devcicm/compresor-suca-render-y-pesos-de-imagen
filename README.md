# SUCA

SUCA es un compresor de imagenes. El kernel de compresion es el producto principal. OCR visual, embeddings, RAG, LM Studio, chat, REST y MCP son capacidades de apoyo que se conectan mediante la capa de aplicacion.

## Estado del repositorio

La estructura ya expresa los limites de los contextos, pero el codigo aun se compila como un unico ejecutable .NET (`ConsoleApp suca compress.csproj`). No debe presentarse como una solucion multi-proyecto terminada: la extraccion fisica de las clases de apoyo que aun viven en `src/Suca.Kernel/SucaCompressionKernel.cs` sigue pendiente.

`legacy/` contiene la version base historica del compresor (la idea original del proyecto); el `.csproj` la excluye de la compilacion y no debe modificarse. `runtime/` contiene galeria de conversiones, ordenes, ajustes, indices, conversaciones y resultados locales; esta ignorado por Git para no versionar datos generados ni contenido de usuarios.

**Pruebas**: todavia no hay suites automatizadas; la verificacion actual es manual y guida por navegador (ver [Estrategia de pruebas](tests/README.md), donde estan las 9 unidades priorizadas para las futuras suites). La integracion continua valida compilacion e higiene del repositorio en cada `push`.

## Datos y privacidad

SUCA es una herramienta de escritorio local y todo su estado vive en su propia maquina:

- `runtime/` guarda conversaciones de chat, indices RAG, galeria de conversiones,
  ordenes y ajustes. **Esta ignorado por Git y no debe subirse nunca**: puede contener
  contenido personal del usuario.
- El repositorio no incluye imagenes de ejemplo con contenido de terceros, credenciales
  ni certificados. Las rutas de ejemplo de la documentacion y de `legacy/` son genericas.
- El host escucha en `127.0.0.1` y expone capacidades deliberadamente locales: rutas
  absolutas de disco, navegacion de carpetas y apertura del Explorador. Revise
  [SECURITY.md](SECURITY.md) antes de exponerlo en una red.
- Las credenciales de servicios externos (por ejemplo LM Studio) se configuran por
  entorno o en `runtime/settings/`, nunca en el codigo.

## Estructura

```text
src/
  Suca.Kernel/                 Dominio y contrato de compresion (+ punto de entrada temporal)
  Suca.Shared/                 Primitivas de imagen, vectores y conversor de formatos
  Suca.Application/            Result, maquina de estados, servicio de conversion,
                               ordenes orquestadas, ajustes, locks, jobs y observabilidad
  Suca.Support.Retrieval/      Extraccion de texto documental
  Suca.Integrations.LmStudio/  Cliente, parser y administracion LM Studio
  Suca.Chat/                   Chat persistente con outbox, ACK y reintentos
  Suca.Api/                    Launcher, host REST/MCP y composicion de dependencias
  Suca.Web/                    Shell multipage: aside de servicios, paginas por vista,
                               tooltips y galeria
knowledge/seed/                Corpus RAG de ejemplo versionable
runtime/                       Estado generado local (galeria, ordenes, ajustes,
                               indices y conversaciones); ignorado por Git
legacy/                        Codigo historico excluido de compilacion
docs/                          Arquitectura y operacion
tests/                         Estrategia y unidades candidatas para las suites
```

## Compilar y arrancar

Requiere el SDK de .NET 8 y Windows por el destino `net8.0-windows`.

```powershell
dotnet build "ConsoleApp suca compress.csproj"
```

Al iniciar la solucion desde Visual Studio con `F5`, o al ejecutar sin argumentos, el launcher muestra una ventana para elegir entre servicio web y consola. Para automatizarlo, indique uno de estos modos:
```powershell
# Compresion local en consola; --output es opcional.
dotnet run --project "ConsoleApp suca compress.csproj" -- --mode console --input "C:\imagenes\entrada.jpg" --output "C:\imagenes\SUCA_OUT"

# Lote orquestado en consola: carpeta en paralelo, con progreso y teclas P/R/C.
dotnet run --project "ConsoleApp suca compress.csproj" -- --input "C:\imagenes\lote" --parallel 3 --collection facturas

# Orden desde archivo JSON.
dotnet run --project "ConsoleApp suca compress.csproj" -- --order "C:\ordenes\lote.json"

# Host web con REST, MCP y SUCA Studio. --urls es opcional: sin el, arranca en http://127.0.0.1:5077.
# En ambos modos, el arranque muestra los servicios y las URLs de backend y frontend.
dotnet run --project "ConsoleApp suca compress.csproj" -- --mode web --urls http://127.0.0.1:5077
```

Suca detecta automaticamente la raiz del repositorio (buscando el `.csproj` hacia arriba) y coloca todo el estado local en `runtime/`, de modo que **el repo funciona igual al clonarse en otra maquina**: no hay rutas absolutas versionadas ni datos quedando en el codigo. Para instalaciones portable puede redirigir todo con variables de entorno: `SUCA_URLS`, `SUCA_STORAGE_ROOT`, `SUCA_SETTINGS_ROOT` y `SUCA_WEB_ROOT` (tabla completa en [Operacion > Variables de arranque y portabilidad](docs/operations/README.md#variables-de-arranque-y-portabilidad)).

`console`, `--console`, `web`, `--web`, `serve` y `--rest` se conservan como aliases. Use `--help` para ver los comandos. En consola, `Ctrl+C` cancela la operacion y `--timeout-seconds <n>` cambia el limite predeterminado de tres minutos.

Con el modo web activo:

- Salud y diagnostico: `http://127.0.0.1:5077/health`
- SUCA Studio (shell con Compresor, Galeria, Chat, Ajustes y Actividad): `http://127.0.0.1:5077/studio`
- Minichat: `http://127.0.0.1:5077/minichat` (redirige al chat del shell)
- Catalogo MCP: `http://127.0.0.1:5077/mcp/tools` y `http://127.0.0.1:5077/mcp/resources`

Para las funciones LLM, LM Studio debe publicar una API local compatible con OpenAI, normalmente en `http://127.0.0.1:1234`, y tener disponibles los modelos de chat y embeddings elegidos en la solicitud.

### Servicio de conversion de imagenes

SUCA Studio permite subir imagenes (ventana de seleccion o arrastrar y soltar, una o muchas en paralelo) y convertirlas entre `webp` (formato por defecto), `jpeg` y `png`. El motor por defecto es el **pipeline SUCA** (configurable a re-encodado `directo`), con barras de progreso por archivo, pausa/cancelacion/reanudacion y metricas de entrada y salida (formato, dimensiones, EXIF, bpp, ratio, ahorro, duracion). Los resultados quedan en una galeria con **colecciones** y se recuperan como archivo o base64:

- `POST /api/web/convert` (multipart: `file`, `engine`, `format`, `quality`, `maxWidth`, `maxHeight`, `removeMetadata`, `collection`, `correlationId`) y `POST /api/web/convert/cancel` (`{"correlationId":"..."}`) para cancelar de verdad una conversion en vuelo: el trabajo se interrumpe y no se registra en la galeria.
- `GET /api/web/images[?collection=nombre]`, `GET /api/web/images/{id}`.
- `GET /api/web/images/{id}/file?variant=converted|original` devuelve los bytes.
- `GET /api/web/images/{id}/base64?variant=converted|original` devuelve `base64` y `dataUrl`.
- `DELETE /api/web/images/{id}` elimina el registro y sus archivos.
- **Rutas en disco**: `GET /api/web/images/{id}/path?variant=`, `POST /api/web/images/paths` (body `{ids, variant}`), `GET /api/web/collections/{name}/paths?variant=` y `POST /api/web/storage/reveal` para abrir la carpeta en el Explorador; `POST /api/web/images/{id}/reveal?variant=` abre el Explorador con el archivo seleccionado. La interfaz lo expone como **Abrir carpeta** y **Copiar ruta(s)** en Galeria, Detalle y la cola del Compresor.
- Colecciones (CRUD completo): `GET /api/web/collections`, `POST /api/web/collections` (crear vacia), `PUT /api/web/collections/{name}` (renombrar actualizando sus imagenes), `GET /api/web/collections/{name}/images` (JSON enriquecido con base64), `PUT /api/web/images/{id}/collection`, `DELETE /api/web/collections/{name}?confirm=true` (disolver; las imagenes pasan a `general`).
- Ordenes orquestadas (servicios e IA): `POST /api/web/orders` con un JSON de rutas y opciones, mas estado, pausa/reanudar/cancelar y eventos por correlacion; `POST /api/web/orders/preview` valida sin iniciar; `GET /api/web/folders` permite navegar carpetas del servidor. Detalle completo en [Operacion](docs/operations/README.md).

Los archivos viven bajo `runtime/web/` (ignorado por Git); el manifiesto de la galeria es `runtime/web/gallery.json` y las ordenes en `runtime/web/orders/`.

### Ajustes de sistema y usuario

Los dos modos cargan preferencias con precedencia CLI > usuario > sistema > valores por defecto. En modo web se administran en la vista Ajustes de `/studio` o con:

- `GET /api/settings/effective`, `GET|PUT /api/settings/{system|user}`, `POST /api/settings/{system|user}/reset`.

En consola, `--settings` muestra la configuracion efectiva con su origen y `--settings-edit` abre un editor interactivo que guarda en el scope usuario. Los archivos viven bajo `runtime/settings/`.

## Documentacion

- [Indice de documentacion](docs/README.md)
- [Arquitectura y limites](docs/architecture/README.md)
- [Operacion REST, MCP, RAG y depuracion](docs/operations/README.md)
- [Modulo de chat](src/Suca.Chat/README.md)
- [Interfaz web](src/Suca.Web/README.md)
- [Corpus RAG dinamico](knowledge/seed/README.md)
- [Estrategia de pruebas](tests/README.md)
- [Version base historica](legacy/README.md)
- [Como contribuir](CONTRIBUTING.md) · [Seguridad y privacidad](SECURITY.md) · [Novedades](CHANGELOG.md)
