# Politica de seguridad

## Modelo de confianza

SUCA es una **herramienta de escritorio local**. El proceso escucha en
`http://127.0.0.1:5077` y esta disenado para ejecutarse en la maquina del usuario, igual
que un compresor o un explorador de archivos. No es un servicio multiusuario.

Por ese modelo, algunas capacidades son deliberadamente locales:

- `POST /api/web/images/{id}/reveal` y `POST /api/web/storage/reveal` abren el Explorador
  de archivos con el archivo seleccionado.
- `GET /api/web/images/{id}/path` y `POST /api/web/images/paths` devuelven rutas absolutas
  de disco de la maquina que ejecuta el proceso.
- `GET /api/web/folders` permite navegar el sistema de archivos del servidor.
- El modo chat habla con LM Studio en `http://127.0.0.1:1234`.

**No expongas el puerto a redes que no sean de confianza.** Si necesita acceso remoto,
coloque un proxy inverso con autenticacion y limite las rutas anteriores.

## No se abren las ventanas del Explorador

Es una herramienta de escritorio: necesita una sesion de Windows con Explorer disponible.

| Sintoma | Causa | Que hace SUCA |
| --- | --- | --- |
| "Abrir" no abre nada | El proceso se lanzo sin `UseShellExecute` y fuera de la sesion del shell | Arranca `explorer.exe` con ruta absoluta y shell execute |
| Abre la carpeta pero no selecciona el archivo | `/select,` no es soportado en algunos sistemas o con rutas largas | Cae en cascada a abrir la carpeta contenedora |
| No abre nada y la UI avisa | No hay escritorio interactivo: servicio, contenedor o sesion 0 | Devuelve la ruta en el error y la **copia al portapapeles** |

Si ocurre en tu equipo:

1. Comprueba que ejecutas SUCA en tu sesion normal de Windows, no como servicio ni desde
   un contenedor o WSL.
2. Si Explorador esta abierto con privilegios distintos a los de SUCA (por ejemplo uno
   elevado y SUCA sin elevar), abre una ventana de Explorador normal antes de usar "Abrir".
3. Siempre tienes alternativa: **Copiar ruta** (Galeria, Detalle o cola) y pegarla en el
   Explorador o en el Explorador de archivos. El endpoint `GET /api/web/images/{id}/path`
   devuelve la ruta sin abrir nada.

---

## Reportar una vulnerabilidad

No abra un issue publico para un fallo de seguridad. Escriba a
**devcicm@users.noreply.github.com** describiendo:

1. Que endpoint o flujo esta afectado.
2. Pasos para reproducirlo.
3. Impacto esperado (lectura de rutas ajenas, ejecucion en la maquina, etc.).
4. Version o commit afectado.

Se respondera en un plazo razonable y el arreglo se publicara antes de la divulgacion.

## Datos que nunca deben salir del equipo

- Conversaciones, indices RAG, galeria y resultados: viven en `runtime/` y estan
  ignorados por Git.
- Credenciales de LM Studio u otros servicios: se configuran por entorno
  (`SUCA_URLS`, variables del proceso) o en `runtime/settings/`, nunca en el codigo.
- Las rutas de ejemplo en la documentacion y en `legacy/` deben ser genericas.

## Buenas practicas al contribuir

- No agregue claves, tokens ni certificados; `.gitignore` ya excluye `.env`, `*.key`,
  `*.pfx`, `*.pem` y `secrets*.json`.
- Los mensajes de error devueltos al cliente no deben incluir rutas absolutas ni
  excepciones sin filtrar.
- Las rutas de disco se resuelven en tiempo de ejecucion con `SucaRuntimePaths`; nunca
  codifique rutas de una maquina concreta.