# Las matematicas de SUCA

Este documento describe las formulas reales que ejecuta el kernel SUCA
(`src/Suca.Kernel/SucaCompressionKernel.cs`): como analiza la imagen, como decide la
escala de renderizado y la calidad de pixeles, como aproxima el peso objetivo y como
mide el resultado. Cada seccion cita la clase y el metodo exactos del codigo.

Indice:

1. [Senales visuales (analisis de la imagen)](#1-senales-visuales)
2. [El perfil A-B-C-D y el hash SUCA](#2-el-perfil-a-b-c-d-y-el-hash-suca)
3. [Factorizacion y escala de renderizado](#3-factorizacion-y-escala-de-renderizado)
4. [Calidad de pixeles (CalidadBase)](#4-calidad-de-pixeles)
5. [Decisiones correctivas antes de renderizar](#5-decisiones-correctivas)
6. [Renderizado (resize y codificacion)](#6-renderizado)
7. [Peso mosca: aproximacion iterativa del peso](#7-peso-mosca)
8. [Metricas de salida: bpp, ratio y ahorro](#8-metricas-de-salida)
9. [Integridad perceptual: MSE, PSNR y SSIM](#9-integridad-perceptual)
10. [Mapa de pesos 8x8](#10-mapa-de-pesos-8x8)
11. [Ejemplo numerico completo](#11-ejemplo-numerico)

---

## 1. Senales visuales

`ImageAnalyzer.Analyze` convierte la imagen a una matriz de grises con luminancia
BT.601 y extrae siete senales:

```
Y = 0.299·R + 0.587·G + 0.114·B
```

| Senal | Formula (metodo) | Significado |
| --- | --- | --- |
| Media | `μ = Σ Y / (H·W)` | brillo global |
| Desviacion | `σ = √( Σ (Y−μ)² / n )` | contraste |
| Entropia | `H = − Σ p·log₂(p)`, con `p = frecuencia(i) / n` sobre 256 niveles | riqueza de informacion |
| Densidad de bordes | Sobel 3x3 (`gx`, `gy`); cuenta `√(gx²+gy²) > 80` dividido por pixeles interiores | detalle y estructura |
| Varianza de proyecciones | varianza de las medias por fila (H) y por columna (V) | detecta lineas de texto |
| ForegroundRatio | umbral adaptativo `t = μ − 0.2·σ`; cuenta `Y < t / total` | proporcion de "tinta" |
| RidgeLikeScore | `|(izq−c)·(der−c)| + |(arr−c)·(aba−c)|` promediado, normalizado por `255²` | crestas (huellas) |

`ImageAnalyzer.Classify` clasifica el contenido con umbrales explicitos:

```
fingerprint : ridge > 0.07 ∧ bordes > 0.10 ∧ 0.08 < tinta < 0.60
document    : proyH > 150 ∧ proyV > 120 ∧ 0.02 < tinta < 0.45
ui          : entropia < 5.2 ∧ bordes > 0.18 ∧ σ > 35
photo       : entropia ≥ 5.8 ∧ bordes < 0.25
graphic     : entropia < 4.6 ∧ bordes < 0.12
```

## 2. El perfil A-B-C-D y el hash SUCA

Cada conversion SUCA recibe un `SucaProfile` con cuatro digitos `A, B, C, D ∈ [0..9]`.
El perfil por defecto es **0, 1, 1, 3** (usado cuando la API no especifica ninguno).

```csharp
// SucaMath.HashMinimalista
raw  = (A << 3) + (B << 2) + (C << 1) + D
hash = (raw & 0x7F) % 95 + 32        // resultado en 32..126 (ASCII)
```

Existe un hash extendido (`HashSucaExtendido`: `h = 17; h = h·31 + x …` con
desplazamientos XOR) para trazabilidad, pero la decision solo usa el minimalista.

## 3. Factorizacion y escala de renderizado

`SucaMath.FactorEscalaBase` es el metodo de factorizacion: una matriz fija 2x2
transforma el perfil en un escalar, y sobre el se apilan tres correcciones.

```
M = ┌ 3  2 ┐        x    = 3·A + 2·B
    └ 1  4 ┘        y    = 1·C + 4·D
                    suma = x + y

baseEscala = 0.3 + (suma mod 70) / 100          // factorizacion modular: 0.30 .. 0.99
wobble     = 0.85 + 0.15·sin(suma)              // oscilacion determinista: 0.70 .. 1.00
```

`penal(KB)` castiga las imagenes de entrada grandes con una curva por tramos suave
(`SucaMath.PiecewiseSmooth` + `SmoothStep01(t) = t²·(3−2t)`):

```
KB      :   0     1000    2000    3000    4000    8000   16000
penal   :  1.00   0.88    0.78    0.68    0.58    0.50    0.42
```

La escala final se acota:

```
escala = clamp( baseEscala · wobble · penal(KB), 0.25, 1.0 )
```

## 4. Calidad de pixeles

`SucaMath.CalidadBase` deriva la calidad de compresion del hash y del peso:

```
vari  = (sin(hash) + 1) / 2            // 0 .. 1, determinista por perfil
qBase = 60 + vari · 39                 // 60 .. 99

KB     :   0    2000    3000    4000    8000   16000
delta  :   0     −5     −10     −15     −20     −25

calidad = clamp(qBase + delta, 35, 99)
```

## 5. Decisiones correctivas

`SucaDecisionEngine.Decide` aplica, en orden:

1. **Proteccion de imagenes pequenas** (`PreserveSmallImages`):
   `px ≤ 300k → escala ≥ 0.72`, `≤ 800k → ≥ 0.58`, `≤ 1.5M → ≥ 0.46`.
2. **Contenido**: Documento `escala ≥ 0.72, q ≥ 78`; UI `≥ 0.78, q ≥ 82`;
   Fingerprint `≥ 0.84, q ≥ 88`; Graphic `≥ 0.85, q ≥ 84`; Photo `q ≤ 90`.
3. **Dimensiones**: `nuevo = redondear(original · escala)`, con suelo `192x192`
   (`options.MinWidth/MinHeight`) y sin agrandar nunca.
4. **Formato**: Documento → WebP near-lossless q70; UI → WebP near-lossless q75 (o PNG);
   Fingerprint → WebP near-lossless q80 si `PesoMoscaKbTarget ≤ 120`, si no PNG, `q ≥ 92`;
   Photo → WebP; sin perdida → PNG q100.

## 6. Renderizado

`SucaCompressor.Process`:

1. Limpia metadatos EXIF/ICC/IPTC/XMP si `RemoveMetadata`.
2. **Resize** solo si cambia, con remuestreo **Mitchell-Netravali** y `ResizeMode.Max`
   (mantiene proporcion, nunca agranda).
3. Codifica segun decision: **WebP** lossy `BestQuality` con near-lossless opcional,
   **PNG** `BestCompression`, **JPEG** por calidad.

`SucaFormatConverter.Convert` (motor "directo") hace lo mismo sin perfil: resize
Mitchell-Netravali y encode directo.

## 7. Peso mosca

`SucaCompressor.SaveWithOptionalPesoMosca` aproxima el peso objetivo de forma
descendente (por defecto objetivo 100 KB, maximo 8 iteraciones):

```
mientras KB > objetivo y quedan iteraciones:
    si el formato admite calidad:  q −= 5  (piso 35)  y near-lossless −= 5 (piso 45)
    si ya no se puede bajar calidad: resize x 0.94 (piso 128x128) y repetir
    re-encode y volver a medir
```

Nunca sube calidad: es una aproximacion por decremento que se detiene en cuanto
`KB ≤ objetivo`.

## 8. Metricas de salida

`ImageConversionService.FillOutputMetrics` calcula las que ve la galeria y la API:

```
bpp    = bytes_salida · 8 / (ancho · alto)          // "calidad de pixeles" efectiva
ratio  = bytes_originales / bytes_convertidos
ahorro = 100 − (convertidos / originales) · 100
reduccion_px = 100 − (pixeles_salida / pixeles_entrada) · 100
```

## 9. Integridad perceptual

`IntegrityValidator.Compare` iguala los tamaños, convierte ambos a grises y calcula:

```
MSE  = Σ (a−b)² / n
MAE  = Σ |a−b| / n
PSNR = 10 · log₁₀(255² / MSE)                     // 99 si identicas
SSIM-lite = (2μa·μb + c1)(2cov + c2) / ((μa²+μb²+c1)(σa²+σb²+c2))
            con c1 = (0.01·255)² y c2 = (0.03·255)²
histSim  = Σ min(hist_a, hist_b) / Σ hist_a       // interseccion de 256 bins
edgeRet  = min(bordes_b / bordes_a, 1.5)
```

Veredicto: `SIN CAMBIO BIT A BIT` si los SHA-256 coinciden; `ACEPTABLE ESTRUCTURALMENTE`
si `SSIM ≥ 0.88 ∧ edgeRet ≥ 0.80 ∧ histSim ≥ 0.84`; si no, `RIESGO DE PERDIDA RELEVANTE`.

## 10. Mapa de pesos 8x8

`SucaNeuralMapEngine.BuildWeightMap` divide la imagen en 8x8 celdas y calcula por celda
un **peso de preservacion** (el codigo aclara: "No es una red neuronal real"):

```
detail  = media de |Y[y,x] − Y[y,x−1]| + |Y[y,x] − Y[y−1,x]| / n / 255
texture = σ_celda / 255
bias    = (seed mod 17 / 16) · 0.15
peso    = clamp( 0.45·detail + 0.35·texture + 0.20·(1 − |μ/255 − 0.5|) + bias , 0, 1 )
```

Hoy el mapa es **informativo** (se expone via MCP/embeddings): no alimenta `Decide`.

## 11. Ejemplo numerico

Perfil por defecto `A=0, B=1, C=1, D=3`, imagen de entrada `4000x3000`, `500 KB`:

```
x    = 3·0 + 2·1 = 2
y    = 1·1 + 4·3 = 13
suma = 15

baseEscala = 0.3 + (15 mod 70)/100 = 0.45
wobble     = 0.85 + 0.15·sin(15) = 0.85 + 0.15·0.6503 = 0.9475
penal(500) = 1.00 + (0.88 − 1.00)·smoothstep(0.5) = 1.00 − 0.12·0.5 = 0.94

escala = clamp(0.45 · 0.9475 · 0.94, 0.25, 1.0) = 0.4008
```

Renderizado: `4000 · 0.4008 → 1603` y `3000 · 0.4008 → 1202`.

Calidad:

```
raw  = (0<<3) + (1<<2) + (1<<1) + 3 = 9
hash = (9 & 0x7F) mod 95 + 32 = 41

vari   = (sin(41) + 1)/2 = (−0.1586 + 1)/2 = 0.4207
qBase  = 60 + 0.4207·39 = 76
delta(500 KB) = 0  →  calidad = 76
```

## 12. ¿"80 KB sin perdida"? Que es verdad y que no

La frase correcta no es *"80 KB sin perdida"*, sino **"80 KB con perdida medida y acotada"**.
El kernel nunca afirma que no haya perdida: la mide y la publica.

### Cuando SI hay perdida (y es inevitable)

| Fuente de perdida | Por que | Donde esta en el codigo |
| --- | --- | --- |
| **Resize** | Cada pixel de salida mezcla varios de entrada (Mitchell-Netravali). Aunque el codigo sea sin perdida, ya no son los pixeles originales. | `SucaCompressor.Process` → `Resize` |
| **Cuantizacion del codec** | WebP/JPEG lossy descartan informacion de alta frecuencia. | `SaveByDecision` |
| **Near-lossless WebP** | `NearLossless=true` reduce la precision espacial: es *casi* sin perdida, no sin perdida. | Documento/UI/Huella |
| **Peso mosca** | Baja calidad de 5 en 5 y, si hace falta, reduce dimensiones ×0.94 hasta clavar el objetivo. | `SaveWithOptionalPesoMosca` |

Consecuencia: **alcanzar 80 KB desde un archivo mayor siempre implica perdida**. Lo unico
sin perdida real es `AllowLossy = false` (PNG) **y** sin resize **y** sin quitar metadatos:
ahi el SHA-256 coincide y el reporte dice `sin perdida (bit a bit)`.

### Cuando 80 KB se ve "sin perdida" (percepcion)

- **El contenido decide**: un documento, una captura de UI o una huella toleran WebP
  near-lossless con calidad 70-88 y el ojo no distingue; una foto con textura fina a 80 KB
  si muestra perdida.
- **La calidad no se elige al azar**: el perfil A-D y el hash fijan la calidad, el tipo de
  contenido impone suelos (Documento q≥78, UI q≥82, Huella q≥88) y las fotos aceptan mas
  compresion (q≤90).
- **El ahorro no sale solo de tirar pixeles**: sale de quitar metadatos (EXIF/ICC),
  reducir dimensiones y usar un codec moderno. En el ejemplo del degradado, 77 MB → 4 KB
  (-99.99%) porque el contenido es trivialmente compresible.

### La prueba objetiva que ahora se guarda

Desde esta version el registro incluye el reporte de integridad:

```
integritySsim            similitud estructural global (1.0 = identicas)
integrityPsnr            PSNR en dB (mas alto = menos error)
integrityEdgeRetention   bordes conservados (1.0 = todos)
integrityHistogram       similitud de histograma
integrityMode            "perceptual" o "lossless-strong"
integrityVerdict         ACEPTABLE ESTRUCTURALMENTE / RIESGO DE PERDIDA RELEVANTE
integrityBitwiseEqual    true solo si los SHA-256 coinciden
```

Criterio de aceptacion del kernel: `SSIM ≥ 0.88 ∧ bordes ≥ 0.80 ∧ histograma ≥ 0.84`.
Eso es lo que la UI muestra como chip `SSIM 0.96 · PSNR 41.2 dB` o `sin perdida (bit a bit)`.
Es una afirmacion **medible**, no una promesa de marketing.

### Medicion real del caso "80 KB"

Foto sintetica de prueba (900x700, 1846 KB), dos estrategias con el mismo objetivo de 80 KB:

| Estrategia | Salida | Dimensiones | SSIM | PSNR | Bordes | Veredicto |
| --- | --- | --- | --- | --- | --- | --- |
| WebP con perdida (perfil 0-1-1-3) | 7.2 KB | 522x406 | 0.9987 | 36.9 dB | **0.157** | RIESGO DE PERDIDA RELEVANTE |
| PNG sin perdida (codec) + objetivo | 70.4 KB | **407x317** | 0.9996 | — | — | RIESGO DE PERDIDA RELEVANTE |

Lectura honesta de la tabla:

1. **El objetivo de peso es un techo, no una meta**: el caso WebP salio en 7 KB porque la
   escala factorizada y la calidad derivada del hash ya estaban muy por debajo de 80 KB.
2. **PNG "sin perdida" tampoco salva los pixeles**: para caber en 80 KB el kernel tuvo que
   reducir 900x700 a 407x317. El codec es sin perdida, pero los pixeles ya no son los
   originales (`integrityBitwiseEqual = false`).
3. **La unica salida identica bit a bit** exige: codec sin perdida, cero resize y cero
   metadatos. Con un original de 1.8 MB de detalle, 80 KB no puede contener la misma
   informacion: la relacion de compresion es de 23:1.

Por eso SUCA no promete "80 KB sin perdida": elige la ruta que menos dano hace segun el
contenido (near-lossless para documento/UI/huella, perdida controlada para foto) y **mide**
el resultado para que la decision sea auditable.

### Mejora posible: maximizar calidad dentro del techo

Hoy el peso mosca **solo desciende** (nunca sube calidad para aprovechar el margen). Si se
quisiera "la mejor calidad posible que quepa en 80 KB", habria que anadir una busqueda
ascendente (+5 de calidad mientras el peso siga bajo el techo). No esta implementado porque
rompe la determinacion por hash/perfil que documenta la seccion 3; queda como decision de
diseno pendiente.