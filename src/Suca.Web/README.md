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

## Componentes interactivos del Compresor (extras)

- **Explorador de carpetas** (`#folder-overlay`): dialogo modal que consume `GET /api/web/folders`; lista unidades y subcarpetas con conteo de imagenes, sube con "Arriba" y devuelve la ruta seleccionada al campo `Carpeta del servidor`
- **Validacion de la orden** (`#order-validate`): `POST /api/web/orders/preview` antes de enviar; el resultado o el `errorCode` se muestra en estado y toasts.
- **Edicion de cola**: cada item editable (`pending/waiting/paused`) renderiza casilla + `<details class="q-edit">` con mini-formulario (motor, formato, calidad, coleccion, ancho/alto maximo) que guarda `item.overrides`; la construccion del `FormData` (`buildForm`) mezcla override con los controles globales. Barra: `Sel. todo`, `Aplicar opciones a seleccionadas`, `Quitar seleccionadas` y quitar por item.
- **Cancelacion con efecto real**: `cancelItem` aborta el XHR si la subida sigue en curso y, si el servidor ya esta procesando, llama `POST /api/web/convert/cancel` con la `correlationId` del item; la pausa global hace lo mismo enprocessing. El item queda `Cancelada` y el servidor no registra resultado.
- **Gestionar colecciones** (Galeria): `details` con alta, renombrado y disolucion via `POST|PUT|DELETE /api/web/collections`; deshabilitados para `general` y con confirmaciones por dialogo nativo.

## Tooltips

Los `span.tip-dot` muestran un popover con `data-tip-title` y `data-tip`; posicion con `data-tip-pos` (`right`, `top`). Hover y foco abren; en pantallas tactiles el toque alterna (`data-open`). Cierran con Escape o toque fuera.
