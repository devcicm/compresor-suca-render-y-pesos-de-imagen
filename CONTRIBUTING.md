# Como contribuir

Gracias por querer mejorar SUCA. Esta guia resume el flujo de trabajo.

## Requisitos

- Windows y **SDK de .NET 8** (el proyecto apunta a `net8.0-windows`).
- Opcional: **LM Studio** para chat, RAG y vision.

## Puesta en marcha

```powershell
git clone <url-del-repo> SUCA
cd SUCA
dotnet build "ConsoleApp suca compress.csproj"

# shell web en http://127.0.0.1:5077/studio
.\bin\Debug\net8.0-windows\"ConsoleApp suca compress.exe" --mode web
```

La primera ejecucion crea `runtime/` (galeria, ordenes, ajustes, chat e indices). Esa
carpeta es **local y esta ignorada por Git**: no la suba nunca.

No versione imagenes de prueba: la raiz ignora `*.jpg`, `*.png`, `*.webp`, `*.bmp` y
similares (los activos del shell viven en `src/Suca.Web/wwwroot`). `runtime/` tambien esta
ignorado: ahi quedan galeria, ordenes, ajustes e indices locales.

Windows bloquea el `.exe` mientras la app corre. El target `StopRunningSuca` del
`.csproj` cierra la instancia antes de compilar, asi que `dotnet build` y `F5` funcionan
aunque el servicio siga abierto. Para desactivarlo: `dotnet build -p:StopRunningApp=false`
(o `.\build.ps1 -KeepRunning`, que falla sin cerrar nada).

## Antes de abrir un PR

```powershell
dotnet build "ConsoleApp suca compress.csproj"     # debe quedar en 0 errores
```

Si toca el shell web, revise tambien:

- Convenciones CSS y accesibilidad en `src/Suca.Web/README.md` (sin `!important`, sin
  estilos inline, estados por atributo, tooltips en controles).
- Que los modulos sigan siendo ES Modules validos.

Si toca la API o el dominio:

- Los errores esperados se devuelven con `Result` y `errorCode` tipado; no lance
  excepciones para fallos de negocio.
- Toda accion nueva debe quedar trazable con `correlationId` y registrada en
  observabilidad.
- Documente el contrato nuevo en `docs/operations/README.md`.

## Estilo de commits

[Conventional Commits](https://www.conventionalcommits.org/) en espanol, un commit por
unidad logica:

```text
feat(application): ordenes con vista previa de validacion
fix(web): la pausa durante la subida conservaba el estado pausado
docs(operations): explicar la correlacion de punta a punta
chore(repo): reglas de publicacion y escaneo de secretos
```

## Reportes de errores

Incluya version de .NET, modo de ejecucion (web o consola), pasos, salida esperada y
salida real. Si adjunta imagenes, use la galeria local y comparta el `correlationId`
del registro: permite reconstruir la traza completa.

## Alcance

- Todo cambio debe mantener el modo web y el modo consola funcionando.
- No se aceptan rutas absolutas de una maquina concreta: use `SucaRuntimePaths` o las
  variables de entorno `SUCA_*`.
- Las suites automatizadas estan planificadas en `tests/README.md`; mientras tanto, cada
  PR debe incluir los pasos manuales de verificacion.