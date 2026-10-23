# Version base historica (legacy)

Esta carpeta conserva la **version base** del compresor SUCA: el punto de partida
desde el que escalo el proyecto actual. Se publica como referencia histórica.

| Archivo | Contenido |
| --- | --- |
| `Compresor_Imagenes.cs` | Implementacion original comentada: pipeline SUCA, parametros de calidad y escritura de resultados. |
| `MatchersSuca.cs` | Matchers originales del pipeline (deteccion y seleccion de imagenes). |

Notas:

- **No se compilan**: el `.csproj` los excluye con `<Compile Remove="legacy/**/*.cs" />`.
- **No son el codigo en uso**: la implementacion vigente vive en `src/Suca.Kernel`
  (motor), `src/Suca.Shared/Imaging` (conversion) y `src/Suca.Application` (servicio,
  ordenes y ajustes).
- El codigo se conserva tal cual. Solo se anonimizaron las rutas de ejemplo de los
  comentarios para poder publicar el repositorio en abierto.

Para el codigo actual, vease la [documentacion de arquitectura](../docs/architecture/README.md)
y la [documentacion de operacion](../docs/operations/README.md).