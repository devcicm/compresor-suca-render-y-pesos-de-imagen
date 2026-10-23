# Avisos de terceros (Third-Party Notices)

Este proyecto se distribuye bajo licencia [MIT](LICENSE) y usa los siguientes componentes
de terceros en tiempo de ejecucion.

## `src/Suca.Web/wwwroot/js/vendor/markdown.js`

- **Licencia**: MIT
- **Origen**: implementacion propia escrita para SUCA Studio (no es un paquete de npm).
- **Proposito**: renderizado minimo de Markdown para las respuestas del chat
  (encabezados, listas, citas, codigo, negritas, cursivas y enlaces).
- **Por que esta vendorizado**: el shell web se sirve como estatico, sin `npm install`
  ni cadena de construccion; asi el Markdown funciona en cualquier clon del repo y en
  instalaciones publicadas.

Si en el futuro se sustituye por una biblioteca externa (por ejemplo `marked`), debe
anadirse aqui su licencia y origen, y mantenerse el aviso en `CHANGELOG.md`.

## Paquetes NuGet

| Paquete | Uso | Licencia |
| --- | --- | --- |
| `SixLabors.ImageSharp` / `.Drawing` | conversion y procesado de imagenes | Apache-2.0 |
| `PdfPig` | extraccion de texto de PDF para el indice RAG | Apache-2.0 |

Sus licencias se distribuyen con los propios paquetes en la carpeta `bin/` y en la
carpeta de paquetes de NuGet del equipo.

## Kernel de compresion

`src/Suca.Kernel` incluye el algoritmo SUCA original, desarrollado dentro de este
repositorio sin dependencias externas.