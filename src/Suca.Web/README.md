# SUCA Web — Shell de Studio

Shell web sin framework que sirve los servicios de SUCA Studio. La navegacion es **multipage real**: cada servicio es su propia pagina HTML (paginas amigables `/studio/`, `/studio/galeria`, `/studio/chat`, `/studio/ajustes`, `/studio/actividad`) y los modulos son ES Modules servidos estaticamente, con un bootstrap por pagina (`js/pages/*.js`) que aísla los errores de inicio.

## Estructura

```text
wwwroot/
  index.html            Shell: aside de servicios + main + overlays
  css/suca-ui.css       Design tokens, base, layout y componentes (unico CSS)
  js/main.js            Helpers, nav colapsable, tooltips, toasts, ajustes
  js/pages/             Bootstrap por pagina (try/catch aislar errores)
  js/views/
    compress.js         Compresor SUCA: cola paralela editable (checkboxes, overrides
                        por archivo), explorador de carpetas del servidor, progreso,
                        orden orquestada y validacion previa
    gallery.js          Galeria: colecciones (CRUD en "Gestionar colecciones"),
                        chips de metricas, base64, detalle
    chat.js             Minichat (LLM + RAG via LM Studio)
    settings.js         Ajustes de sistema y usuario (scopes con origen visible)
    activity.js         Ordenes orquestadas y eventos de trazabilidad
```

## Convenciones CSS (sin antipatrones)

- Tokens unicos en `:root` (espaciado 4/8, radios, sombras, color, z-index).
- BEM-lite (`.svc-item`, `.card`, `.field`, `.tip-dot`, `.chip`).
- Prohibido `!important` y estilos inline desde JS: los estados visuales van por atributos (`data-state`, `data-nav-collapsed`, `aria-expanded`, `[hidden]`).
- Cuando una clase define `display` (ej. `.overlay`), su version oculta se declara con selector compuesto (`.overlay[hidden]`).
- Mobile-first; breakpoints 768/900/1024; soporte `prefers-reduced-motion` y `focus-visible`.

## Router y navegacion

La navegacion es **multipage real**: cada servicio es su propia pagina HTML y el navegador gestiona la navegacion (sin router JS ni secciones ocultas).

| Pagina | URL amigable | Archivo |
| --- | --- | --- |
| Compresor SUCA (inicio) | `/studio/` | `index.html` |
| Galeria | `/studio/galeria` | `galeria.html` |
| Chat (Minichat) | `/studio/chat` | `chat.html` |
| Ajustes | `/studio/ajustes` | `ajustes.html` |
| Actividad | `/studio/actividad` | `actividad.html` |

- El aside se construye desde una unica fuente (`js/layout.js`): marca la pagina activa (`aria-current`), aplica el colapso 240px a 68px y persiste el estado en `localStorage` (`suca.nav.collapsed`).
- En pantallas pequenas el aside es un drawer con overlay.
- Cada pagina carga `main.js` (layout + helpers + ajustes del servidor) y su propio bootstrap en `js/pages/{pagina}.js`; si una vista falla al iniciar, el error queda aislado en su pagina y no rompe la navegacion.
- `/minichat` redirige a `/studio/chat.html`; `/studio` es la entrada (redirige a `/studio/`).
- Apertura profunda: `/studio/galeria.html?detalle={id}` abre el detalle de una imagen (usado por el enlace "Detalle" de la cola del Compresor).

## Fundaciones transversales (Fase A)

Todo el shell se apoya en cuatro modulos compartidos:

```text
js/i18n.js       Diccionarios es/en, t(), applyI18n(), formato con Intl y persistencia (suca.lang)
js/a11y.js       Overlays con foco atrapado, Escape, navegacion con flechas y region viva
js/shortcuts.js  Atajos globales y dialogo de ayuda
js/layout.js     Aside, topbar, chip de salud y conmutador de idioma
js/main.js       Estado global, ajustes, toasts, sondeo de /health y utilidades
```

- **Idioma**: `t("clave")` devuelve la clave si falta y la registra en
  `window.__sucaI18nMissing` (verificacion en QA). Los elementos estáticos se traducen con
  `data-i18n`, `data-i18n-placeholder`, `data-i18n-title`, `data-i18n-aria-label`,
  `data-i18n-tip` y `data-i18n-tip-title`. El idioma se guarda en `suca.lang`.
  Regla critica: los suscriptores de `onLanguageChange` se registran **una sola vez** (fuera
  de `renderLayout`), porque registrare dentro del listener que vuelve a renderizar produce
  crecimiento exponencial y cuelga el navegador.
- **Salud del servidor**: chip en el topbar con sondeo a `/health` cada 10 s, reintento al
  recuperar el foco de la ventana y al hacer clic en el chip; avisa por toast al perder o
  recuperar la conexion.
- **Atajos**: `Ctrl+O` abrir archivos, `Ctrl+Enter` convertir pendientes, `Ctrl+Shift+C`
  cancelar conversiones en curso y `Shift+?` ayuda. Se ignoran dentro de campos de
  escritura; las acciones de pagina viajan como evento `suca:action`.
- **Accesibilidad**: los overlays declaran `role="dialog"` y `aria-modal`, atrapan `Tab`,
  cierran con `Escape` y devuelven el foco al elemento de origen; las listas y grillas
  navegables usan `data-nav-item` y flechas; el progreso se anuncia con una region viva
  (`.visually-hidden`, `role="status"`).

## Ajustes: buscador, cambios sin guardar e import/export

- **Buscador** por nombre visible o clave (`quality`, `web.`, `lmstudio`...): oculta lo que no
  coincide e informa cuantos hay.
- **Barra de cambios sin guardar**: fija arriba con el numero de campos tocados, **Guardar
  (usuario)** y **Descartar cambios**. El estado sucio se marca por atributo
  (`label[data-dirty]`), nunca con estilos en linea.
- **Validacion en linea**: numeros contra `min`/`max` declarados en el HTML y URLs con
  esquema; marca `aria-invalid` y muestra el motivo. **El guardado se bloquea** si algun
  campo modificado es invalido, indicando cual.
- **Exportar / importar JSON**: descarga los valores efectivos (con su origen) y permite
  cargarlos de vuelta en el scope `usuario` con confirmacion previa.
- Correccion: `fetchJson` se importaba sin estar en la lista de imports, por lo que guardar
  o restablecer ajustes fallaba al recargar del servidor.

## Actividad: timeline y detalle de orden

- **Timeline por correlacion**: agrupa los eventos por correlacion base (ignorando el sufijo
  `.itemId8` de los items) y dibuja cada trabajo como una barra con su duracion y marcadores
  por evento; el color indica info, warning o error. Alternancia **Tabla | Timeline**
  persistida en `suca.activity.view`.
- **Detalle de orden**: el identificador de cada orden abre un dialogo con sus items, su
  `stateHistory` (`Pending · 0 ms -> Running · 0 ms -> Completed · 448 ms`), duracion,
  `recordId` de galeria y descarga del JSON de la orden. El estado global se deriva de los
  items porque el endpoint de detalle no lo incluye.

## Chat: banner de servicio, streaming y Markdown

- **Banner de LM Studio**: consulta `GET /api/lmstudio/health` al abrir la pagina y muestra
  el estado con el enlace para abrirlo y un boton **Reintentar**; si responde, el banner se
  oculta solo a los 2,5 s. `GET /api/lmstudio/models` responde ahora **503** con detalle
  cuando el servicio externo esta caido (antes era un 500 opaco).
- **Streaming (SSE)**: `POST /api/minichat/chat/stream` reenvia los fragmentos de LM Studio
  (`Started`, `Context`, `Delta`, `Completed`, `Error` y `[DONE]`). La UI los pinta al vuelo
  con un punto pulsante y ofrece **Detener**, que aborta la peticion; el mensaje queda
  persistido igualmente en la conversacion. El canal de fragmentos es **local por peticion**
  (dos streams simultaneos no se mezclan).
- **Markdown vendorizado** (`js/vendor/markdown.js`, MIT, sin npm): encabezados, listas,
  citas, bloques de codigo con boton **Copiar**, negritas, cursivas y enlaces. Todo el texto
  se escapa antes de transformarse (verificado: `<script>` y `<img onerror>` salen como
  texto). Licencia en `THIRD-PARTY-NOTICES.md`.
- **Citas RAG**: los eventos `Context` se muestran como chips bajo el mensaje. Viven fuera
  del cuerpo Markdown porque el repintado por fragmentos regenera su `innerHTML`.
- **Gestion de conversaciones**: buscador por titulo o contenido, **Renombrar**
  (`PATCH /api/minichat/conversations/{id}`) y **Eliminar** (`DELETE .../{id}`, que tambien
  limpia inbox/outbox). Botones visibles solo con conversacion seleccionada.

## Galeria: busqueda, seleccion y vistas

- **Buscador y orden**: filtro por nombre (con rebote de 200 ms) y orden por recientes,
  ahorro, tamaño o nombre. Ambos operan sobre la pagina cargada sin volver a pedirla.
- **Seleccion multiple**: casilla en cada tarjeta, `Seleccionar pagina` y una barra fija
  con ZIP, copiar rutas, mover a coleccion y eliminar. Las acciones de lote van al servidor:
  `POST /api/web/images/export` (ZIP) y `POST /api/web/images/collection` (mover N imagenes).
- **Comparador original/convertida**: modal con las dos imagenes alineadas en el mismo
  encuadre y un deslizador que recorta la original para revelar la convertida, con las
  metricas del par debajo.
- **Lightbox**: pantalla completa con navegacion `←`/`→`, pie con posicion (`3/12`) y
  cierre con `Escape`.
- **Vistas y densidad**: cuadricula o lista, y tarjetas compactas; ambas se guardan en
  `localStorage` (`suca.gallery.view`, `suca.gallery.compact`).
- **Paginacion**: `Cargar mas` usa `page`/`pageSize` de `GET /api/web/images` (tambien
  respeta `web.galleryPageSize`) y deduplica por id. Las imagenes usan `loading="lazy"`.
- La galeria **espera a los ajustes** (`settingsReady` de `main.js`) antes de la primera
  carga: asi el tamano de pagina real se aplica desde el principio y no se traen todos los
  registros para volver a filtrar en cliente.

## Compresor: presets, carpetas y gestion de la cola

- **Presets** (`conversion.presets` en los ajustes): perfil con motor, formato, calidad,
  ancho/alto, metadatos y coleccion. En la pantalla Compresor hay selector con **Aplicar**
  (a toda la cola) y **Aplicar a marcadas** (solo las seleccionadas), **Guardar actual**
  (pide nombre la primera vez, actualiza si ya existe) y **Borrar**. Al aplicar un preset a
  toda la cola los pendientes reciben un `override` con esos valores. La validacion del
  servidor limita a 24 presets, nombre de 1-40 caracteres, calidad 1-100, formato soportado,
  dimensiones 1-20000 y nombres unicos (sin distinguir mayusculas).
  Ajustes muestra el listado informativo de los presets guardados.
- **Arrastrar carpetas**: el dropzone acepta carpetas completas mediante
  `webkitGetAsEntry()` con recorrido recursivo, filtra solo imagenes y muestra el avance de
  lectura. Si no hay imagenes avisa en lugar de fallar en silencio.
- **Pegar desde el portapapeles**: evento `paste` con imagenes (`Ctrl+V`), sin afectar a
  los campos de texto.
- **Gestion de la cola**: `Reintentar fallidas` vuelve a poner en lista los errores y
  arranca la conversion, `Limpiar terminadas` retira convertidas/canceladas/con error (no
  borra resultados de la galeria) y `Cancelar todo` detiene lo que este en curso (atajo
  `Ctrl+Shift+C`). Los botones aparecen solo cuando tienen sentido.
- Las opciones visibles quedan **fijadas** (`optionsPinned`) cuando el usuario las edita o
  aplica un preset, para que una recarga de ajustes no las pise.

## Componentes interactivos del Compresor (extras)

- **Explorador de carpetas** (`#folder-overlay`): dialogo modal que consume `GET /api/web/folders`; lista unidades y subcarpetas con conteo de imagenes, sube con "Arriba" y devuelve la ruta seleccionada al campo `Carpeta del servidor`
- **Validacion de la orden** (`#order-validate`): `POST /api/web/orders/preview` antes de enviar; el resultado o el `errorCode` se muestra en estado y toasts.
- **Edicion de cola**: cada item editable (`pending/waiting/paused`) renderiza casilla + `<details class="q-edit">` con mini-formulario (motor, formato, calidad, coleccion, ancho/alto maximo) que guarda `item.overrides`; la construccion del `FormData` (`buildForm`) mezcla override con los controles globales. Barra: `Sel. todo`, `Aplicar opciones a seleccionadas`, `Quitar seleccionadas` y quitar por item.
- **Cancelacion con efecto real**: `cancelItem` aborta el XHR si la subida sigue en curso y, si el servidor ya esta procesando, llama `POST /api/web/convert/cancel` con la `correlationId` del item; la pausa global hace lo mismo enprocessing. El item queda `Cancelada` y el servidor no registra resultado.
- **Gestionar colecciones** (Galeria): `details` con alta, renombrado y disolucion via `POST|PUT|DELETE /api/web/collections`; deshabilitados para `general` y con confirmaciones por dialogo nativo.

## Tooltips

Los `span.tip-dot` muestran un popover con `data-tip-title` y `data-tip`; posicion con `data-tip-pos` (`right`, `top`). Hover y foco abren; en pantallas tactiles el toque alterna (`data-open`). Cierran con Escape o toque fuera.
