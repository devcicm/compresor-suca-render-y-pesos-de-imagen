# Arquitectura de SUCA

## Estado actual

El repositorio tiene un solo proyecto ejecutable: `ConsoleApp suca compress.csproj`. Las carpetas bajo `src/` marcan propiedad y direccion de dependencias, pero no son aun ensamblados independientes. Esta es una migracion por etapas para conservar la compatibilidad del ejecutable mientras se extrae el codigo.

En particular, `src/Suca.Kernel/SucaCompressionKernel.cs` conserva clases de apoyo heredadas para OCR visual, embeddings, red neuronal local y RAG visual. Esas clases deben extraerse gradualmente a los contextos correspondientes; su ubicacion fisica actual no convierte esas funciones en reglas del dominio de compresion.

## Mapa de contextos

| Ubicacion | Responsabilidad | Regla de dependencia |
| --- | --- | --- |
| `src/Suca.Kernel` | Analisis, decision, codificacion, refinamiento e integridad de compresion. | No conoce HTTP, chat, LM Studio ni RAG documental. |
| `src/Suca.Kernel/Contracts` | Contrato `ISucaCompressionKernel`. | Lo consume Application. |
| `src/Suca.Shared` | Operaciones genericas de senal de imagen y vectores. | No contiene reglas de negocio de SUCA. |
| `src/Suca.Application` | Casos de uso, transaccion de archivos, locks, jobs, cancelacion y observabilidad. | Depende de contratos del kernel y de integraciones, nunca de REST. |
| `src/Suca.Support.Retrieval` | Lectura y normalizacion de texto documental. | No llama al compresor. |
| `src/Suca.Integrations.LmStudio` | Cliente REST, parser JSON, embeddings y administracion de modelos LM Studio. | No conoce el host HTTP de SUCA. |
| `src/Suca.Chat` | Conversaciones, outbox/inbox, ACK, idempotencia, reintentos y dead-letter. | Consume la integracion y RAG mediante contratos. |
| `src/Suca.Api` | Launcher WinForms, adaptador REST/MCP, DI, ciclo de vida y exposicion de endpoints. | Depende de Application y adaptadores, nunca al reves. |
| `src/Suca.Web` | Cliente estatico del minichat servido por el host. | No contiene logica de dominio. |

## Direccion de dependencias

```text
REST / MCP / Web / Chat
            |
      Application
       /        \\
Kernel          Integraciones y retrieval
  |                    |
  +------ Shared -------+
```

El kernel recibe una ruta de imagen, una ruta de salida, `SucaProfile` y `SucaOptions`; entrega `CompressionResult`. No debe crear embeddings, datasets, indices RAG, mensajes de chat ni respuestas HTTP.

La aplicacion coordina las operaciones: bloquea por recurso, propaga `CancellationToken`, usa staging y commit atomico para archivos, publica artefactos opcionales despues de la compresion y registra observabilidad. Los endpoints solo adaptan solicitudes HTTP a esos contratos.

## Datos y codigo heredado

- `knowledge/seed/` contiene documentos de ejemplo y puede versionarse.
- `runtime/` contiene indices RAG, conversaciones y salidas locales; Git lo ignora.
- `legacy/` contiene codigo historico. El archivo de proyecto usa `Compile Remove="legacy/**/*.cs"`, por lo que no participa en la compilacion.

## Siguientes extracciones

1. Mover OCR, embeddings, mapa de pesos y RAG visual desde `SucaCompressionKernel.cs` a contextos de soporte con contratos propios.
2. Crear proyectos `.csproj` por contexto solo despues de eliminar referencias circulares y cubrir los contratos con pruebas.
3. Mantener `Suca.Api` como composition root: registrar implementaciones alli, no en el kernel.
