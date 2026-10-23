# Corpus RAG de SUCA

Este directorio es contenido de ejemplo versionable para probar RAG sobre SUCA. No representa un indice fijo ni limita el sistema a estos archivos.

## Contenido

- `documents/`: documentos fuente en Markdown sobre compresion, OCR, RAG, LM Studio y operacion.
- `qa/`: preguntas y respuestas en JSONL para evaluar recuperacion semantica.

## Indexacion dinamica

El servicio puede indexar cualquier carpeta accesible al proceso, con extensiones y profundidad configurables. Use `knowledge/seed/documents` solo como punto de partida. Los indices resultantes deben escribirse fuera del corpus, por ejemplo en `runtime/indexes/`, porque son artefactos regenerables y pueden contener datos del usuario.

Flujo sugerido:

1. Envie `sourceDirectory` y `semanticIndexPath` a `POST /api/tools/lmstudio/rag/index-folder`.
2. Consulte con `POST /api/tools/lmstudio/rag/ask-folder`, indicando si desea regenerar el indice antes de buscar.
3. Ejecute las preguntas de `qa/suca_temario_qa.jsonl` y compruebe que los contextos recuperados preserven la idea central, aunque cambie la redaccion.

La configuracion de LM Studio, modelos, tamanos de chunk, solapamiento y extensiones forma parte de la solicitud. Consulte la [guia de operacion](../../docs/operations/README.md) para los endpoints y prerequisitos.
