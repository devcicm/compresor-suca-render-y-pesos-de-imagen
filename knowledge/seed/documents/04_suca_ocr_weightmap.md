# SUCA OCR And Weight Map

El OCR visual de SUCA no extrae texto semantico como un OCR tradicional. En cambio, estima legibilidad con rasgos como densidad de tinta, numero de lineas, columnas y transiciones de caracteres.

Esto permite decidir si una imagen parece un documento y si conviene ser mas conservador con la compresion.

El mapa de pesos SUCA tampoco es una red neuronal completa. Es una matriz de preservacion guiada por detalle, textura y distribucion local de intensidad.

Las zonas con peso alto indican regiones que merecen mayor cuidado visual. Por ejemplo, bordes finos, patrones importantes o estructuras delicadas.

El OCR visual y el weight map se complementan: uno estima legibilidad global y el otro ayuda a localizar sensibilidad espacial.
