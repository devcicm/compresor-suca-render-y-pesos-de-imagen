# Instrucciones para asistentes y contribucion automatica (SUCA)

## Que es este repositorio

Compresor de imagenes para Windows (.NET 8) que se compila como **un unico ejecutable**
(`ConsoleApp suca compress.csproj`). El producto principal es el kernel de compresion
SUCA; OCR visual, embeddings, RAG, LM Studio, chat, REST y MCP son capacidades de apoyo
conectadas por la capa de aplicacion.

## Comandos

```powershell
dotnet build "ConsoleApp suca compress.csproj"

# Host web (REST + MCP + SUCA Studio)
.\bin\Debug\net8.0-windows\"ConsoleApp suca compress.exe" --mode web

# Compresion en consola
.\bin\Debug\net8.0-windows\"ConsoleApp suca compress.exe" --input "C:\imagenes\a.png"
```

Variables de entorno soportadas: `SUCA_URLS`, `SUCA_STORAGE_ROOT`, `SUCA_SETTINGS_ROOT`,
`SUCA_WEB_ROOT`. No introduzcas rutas absolutas de una maquina concreta en el codigo.

## Limites de responsabilidad

| Carpeta | Responsabilidad |
| --- | --- |
| `src/Suca.Kernel` | Dominio y contrato de compresion (formato, bpp, ratio, decision de motor) |
| `src/Suca.Shared` | Primitivas de imagen, vectores y conversion de formatos |
| `src/Suca.Application` | Result, maquina de estados, servicio de conversion, ordenes, ajustes, locks y observabilidad |
| `src/Suca.Api` | Host REST/MCP, endpoints y composicion de dependencias |
| `src/Suca.Web` | Shell multipage (HTML/CSS/ES Modules sin framework) |
| `legacy` | Version base historica; **no compila** y no debe modificarse |

El codigo en `legacy/` es la version original del compresor y se publica solo como
referencia historica.

## Patrones obligatorios

- **Resultado, no excepcion**: los errores esperados viajan como `Result`/`Result<T>` con
  `errorCode` (`ConversionErrorCodes`). Nunca lances excepciones para errores de negocio.
- **Maquina de estados**: todo trabajo de conversion pasa por `ConversionJobStateMachine`
  y deja `StateHistoryEntry` (estado, instante, ms transcurridos).
- **Correlacion**: cada accion (orden, item, conversion) lleva `correlationId`; las
  operaciones derivadas reutilizan la correlacion (`{ordenCorr}.{itemId8}`) para poder
  reconstruir la traza accion -> ejecucion.
- **Observabilidad**: registra con `IRuntimeObservability` usando la correlacion del
  trabajo; los secretos jamas se registran en logs ni en mensajes de error.
- **Escritura atomica**: nunca escribas archivos de forma parcial; usa `IAtomicFileStore`.
- **Estilo del shell web** (ver `src/Suca.Web/README.md`): sin framework, tokens CSS en
  `:root`, BEM-lite, prohibido `!important`, estados por atributo (`data-*`, `[hidden]`,
  `aria-*`), tooltips `tip-dot` en elementos interactivos y nada de estilos inline salvo
  anchos dinamicos de barra de progreso.
- **Idioma**: documentacion y mensajes de usuario en espanol; identificadores de codigo
  en espanol sin tildes ni `ñ` para evitar problemas de codificacion.

## Commits

Conventional Commits en espanol: `feat(scope): ...`, `fix(scope): ...`, `docs: ...`,
`chore(repo): ...`, `refactor: ...`. Un commit = una unidad logica revisable.

## Nada de datos de usuario

`runtime/` esta ignorado por Git y contiene conversaciones, indices, galeria y
resultados locales. Nunca lo agregues al repositorio ni lo uses en pruebas.