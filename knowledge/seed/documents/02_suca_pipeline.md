# SUCA Pipeline

El pipeline de SUCA sigue una secuencia clara.

Primero analiza la imagen original. Despues calcula una decision de compresion usando el perfil SUCA y el tipo de contenido. Luego carga la imagen, corrige orientacion, elimina metadatos si aplica, redimensiona y guarda el resultado.

Despues del guardado, el flujo puede calcular integridad perceptual, embeddings visuales, registros de dataset y entradas para un indice RAG.

Si el modo peso mosca esta activo, SUCA intenta refinar calidad o escala para acercarse a un objetivo de tamano.

La salida final incluye datos de tiempo, tamano, reduccion de pixeles, ahorro y, si se habilita, metricas perceptuales.
