# LM Studio Integration For SUCA

LM Studio actua como proveedor local de modelos de chat, vision y embeddings.

SUCA puede listar modelos, cargarlos, descargarlos y usar modelos distintos segun la tarea. Un modelo de chat puede responder preguntas, uno de vision puede describir una imagen y uno de embeddings puede construir vectores semanticos.

No todos los modelos responden igual. Algunos entregan JSON limpio, otros usan distinto formato, y otros pueden cortar la salida si el limite de tokens es bajo.

Por eso SUCA incluye una capa de parseo de JSON con soporte para respuestas completas, respuestas parciales y respuestas defectuosas.

La combinacion de LM Studio con SUCA permite usar RAG local sin depender de servicios remotos.
