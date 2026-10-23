# SUCA

SUCA es un compresor de imagenes. El kernel de compresion es el producto principal. OCR visual, embeddings, RAG, LM Studio, chat, REST y MCP son capacidades de apoyo que se conectan mediante la capa de aplicacion.

## Estado del repositorio

La estructura ya expresa los limites de los contextos, pero el codigo aun se compila como un unico ejecutable .NET (`ConsoleApp suca compress.csproj`). No debe presentarse como una solucion multi-proyecto terminada: la extraccion fisica de las clases de apoyo que aun viven en `src/Suca.Kernel/SucaCompressionKernel.cs` sigue pendiente.

`legacy/` contiene implementaciones historicas y el proyecto las excluye de la compilacion. `runtime/` contiene indices, conversaciones y resultados locales; esta ignorado por Git para no versionar datos generados ni contenido de usuarios.

## Estructura

```text
src/
  Suca.Kernel/                 Dominio y contrato de compresion
  Suca.Shared/                 Primitivas de imagen y vector reutilizables
  Suca.Application/            Casos de uso, locks, jobs y observabilidad
  Suca.Support.Retrieval/      Extraccion de texto documental
  Suca.Integrations.LmStudio/  Cliente, parser y administracion LM Studio
  Suca.Chat/                   Chat persistente con outbox, ACK y reintentos
  Suca.Api/                    Launcher, host REST/MCP y composicion de dependencias
  Suca.Web/                    Interfaz web del minichat
knowledge/seed/                Corpus RAG de ejemplo versionable
runtime/                       Estado generado local, ignorado por Git
legacy/                        Codigo historico excluido de compilacion
docs/                          Arquitectura y operacion
tests/                         Guia para las futuras suites de pruebas
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

# Host web con REST, MCP y minichat.
dotnet run --project "ConsoleApp suca compress.csproj" -- --mode web --urls http://127.0.0.1:5077
```

`console`, `--console`, `web`, `--web`, `serve` y `--rest` se conservan como aliases. Use `--help` para ver los comandos. En consola, `Ctrl+C` cancela la operacion y `--timeout-seconds <n>` cambia el limite predeterminado de tres minutos.

Con el modo web activo:

- Salud y diagnostico: `http://127.0.0.1:5077/health`
- Minichat: `http://127.0.0.1:5077/minichat/`
- Catalogo MCP: `http://127.0.0.1:5077/mcp/tools` y `http://127.0.0.1:5077/mcp/resources`

Para las funciones LLM, LM Studio debe publicar una API local compatible con OpenAI, normalmente en `http://127.0.0.1:1234`, y tener disponibles los modelos de chat y embeddings elegidos en la solicitud.

## Documentacion

- [Indice de documentacion](docs/README.md)
- [Arquitectura y limites](docs/architecture/README.md)
- [Operacion REST, MCP, RAG y depuracion](docs/operations/README.md)
- [Modulo de chat](src/Suca.Chat/README.md)
- [Interfaz web](src/Suca.Web/README.md)
- [Corpus RAG dinamico](knowledge/seed/README.md)
- [Estrategia de pruebas](tests/README.md)
