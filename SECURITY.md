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
Antes de tocar nada, pide el diagnostico:

```powershell
curl "http://127.0.0.1:5077/api/web/diagnostics/reveal?path=C:\ruta\archivo.webp"
```

Devuelve `sessionId`, `interactive`, `elevated`, `explorerExists`, `explorerProcesses`,
`pathExists`, `revealed` (resultado real de `SHOpenFolderAndSelectItems`) y una nota.

| Sintoma | Causa | Que hace SUCA |
| --- | --- | --- |
| `revealed: false` | Windows rechazo la peticion (tipicamente SUCA **elevado**, sin escritorio interactivo, o ruta inexistente) | Lo informa y devuelve la ruta |
| `elevated: true` | La app corre como administrador | Cierra SUCA y ejecutalo sin elevacion: es la causa mas comun |
| `interactive: false` / `sessionId: 0` | Servicio o contenedor sin escritorio | Usa "Copiar ruta" |
| `revealed: true` y no ves ventana | La ventana se abrio detras de otras | Revisa la barra de tareas |
| `pathExists: false` | El archivo se borro (por ejemplo al limpiar `runtime/`) | Vuelve a convertir la imagen |

La apertura usa `SHOpenFolderAndSelectItems` (API nativa, la misma de "Mostrar en carpeta") y
cae en cascada a `explorer.exe /select` y a abrir la carpeta. **Copiar ruta** siempre esta
disponible: el endpoint `GET /api/web/images/{id}/path` devuelve la ruta sin abrir nada.

---

## Dependencias con avisos de seguridad

El proyecto fija **SixLabors.ImageSharp 2.1.13**, la ultima version de la serie
**Apache-2.0** (compatible con la licencia MIT del proyecto). Esa serie arrastra avisos
publicados (DoS por imagenes malformadas: GHSA-j3p4-wp97-rph4, GHSA-j9gm-c75j-xc9q,
GHSA-jjfr-hcj7-qf5w, GHSA-gwg2-r3hj-4w44, GHSA-wmxv-xphr-5c9g). La serie 3.x corrige esos
avisos, pero **exige licencia comercial de Six Labors en tiempo de compilacion**, lo que
contradiria la licencia MIT y haria fallar la integracion continua de un clon abierto.

Razon del criterio:

- SUCA es una herramienta **local de escritorio**: procesa imagenes de quien la usa, no
  entrada de terceros sin filtrar.
- Los avisos son de denegacion de servicio por archivos corruptos, no de ejecucion de codigo.
- El servicio ya limita el tamano de subida (`web.maxUploadMegabytes`) y reporta
  `unsupported-image` cuando la decodificacion falla.

Cada ejecucion de CI imprime el informe (`dotnet list package --vulnerable`) para que el
estado sea visible. Si en el futuro el proyecto necesita la serie 3.x, hay que comprar la
licencia de Six Labors y aportar `SixLaborsLicenseKey` como secreto de la accion, lo que
implicaria revisar la licencia del repositorio.

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