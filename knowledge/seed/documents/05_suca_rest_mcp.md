# SUCA REST And MCP Style Access

SUCA expone una capa REST para operar compresion, OCR, embeddings, consultas RAG locales y flujos con LM Studio.

Tambien publica recursos y herramientas con un estilo tipo MCP sobre HTTP. Esto permite descubrir capacidades, ejecutar operaciones y consultar observabilidad desde clientes externos.

La capa de aplicacion maneja cancelacion, jobs, locks por recurso, escritura atomica y eventos recientes de operacion.

La observabilidad es importante porque una app con IA y procesamiento de imagen necesita depurar tiempo, estado de jobs, errores de parseo y fallas de integracion con modelos.

Si una respuesta de LLM llega incompleta o con JSON roto, el parser debe intentar rescatar el contenido util y marcar si la respuesta fue parcial.
