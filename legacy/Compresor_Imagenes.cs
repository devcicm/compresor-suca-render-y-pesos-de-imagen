// Version base historica de SUCA.
//
// Este archivo (y MatchersSuca.cs) conservan la idea original del compresor
// que dio pie al proyecto: pipeline SUCA, matchers por lote y escritura de
// resultados. Se mantiene sin modificar por su valor historico y esta
// excluido de la compilacion (ver "Compile Remove" en el .csproj).
//
// Las rutas de ejemplo de los comentarios se anonimizaron para poder publicar
// el repositorio; el codigo es el original.

//using System;
//using System.Diagnostics;
//using System.IO;
//using System.Linq;
//using SixLabors.ImageSharp;
//using SixLabors.ImageSharp.PixelFormats;
//using SixLabors.ImageSharp.Processing;
//using SixLabors.ImageSharp.Formats.Jpeg;
//using SixLabors.ImageSharp.Formats.Png;
//using SixLabors.ImageSharp.Formats.Webp;

//namespace SUCA_Compressor
//{
//    public static class SucaMath
//    {
//        // === Utilidades ===
//        static double Clamp01(double x) => x < 0 ? 0 : (x > 1 ? 1 : x);
//        static double SmoothStep01(double t) { t = Clamp01(t); return t * t * (3 - 2 * t); }
//        static double Lerp(double a, double b, double t) => a + (b - a) * t;

//        // Interpolación por tramos con transición suave (smoothstep) entre anclajes (x[i], y[i])
//        static double PiecewiseSmooth(double x, double[] xs, double[] ys)
//        {
//            int n = xs.Length;
//            if (x <= xs[0]) return ys[0];
//            if (x >= xs[n - 1]) return ys[n - 1];
//            for (int i = 0; i < n - 1; i++)
//            {
//                if (x >= xs[i] && x <= xs[i + 1])
//                {
//                    double t = (x - xs[i]) / (xs[i + 1] - xs[i]);
//                    t = SmoothStep01(t);
//                    return Lerp(ys[i], ys[i + 1], t);
//                }
//            }
//            return ys[n - 1];
//        }

//        // === Hash SUCA original (preserva tu “firma”) ===
//        public static int HashMinimalista(int a, int b, int c, int d)
//        {
//            int raw = (a << 3) + (b << 2) + (c << 1) + d;
//            return (raw & 0x7F) % 95 + 32; // igual que tu versión base
//        }

//        // === Escala: misma M + wobble; penalización continua que emula tus escalones ===
//        public static double FactorEscala(int a, int b, int c, int d, double kb)
//        {
//            int[,] M = { { 3, 2 }, { 1, 4 } };
//            int x = a * M[0, 0] + b * M[0, 1];
//            int y = c * M[1, 0] + d * M[1, 1];
//            int suma = x + y;

//            double baseEscala = 0.3 + (suma % 70) / 100.0;      // [0.3, 1.0)
//            double wobble = 0.85 + 0.15 * Math.Sin(suma);   // ±15%

//            // Anclajes idénticos a tus if/else: 0→1.0, 1000→0.8, 2000→0.7, 3000→0.6, 4000→0.5
//            double penal = PiecewiseSmooth(kb,
//                new[] { 0.0, 1000.0, 2000.0, 3000.0, 4000.0 },
//                new[] { 1.0, 0.8, 0.7, 0.6, 0.5 });

//            double escala = baseEscala * wobble * penal;
//            return Math.Clamp(escala, 0.3, 1.0);
//        }

//        // === Calidad: misma siembra senoidal; atenuación continua equivalente a -5/-10/-15 ===
//        public static int Calidad(int hash, double kb)
//        {
//            double vari = (Math.Sin(hash) + 1.0) / 2.0; // [0,1]
//            int qBaseInt = (int)(60.0 + vari * 39.0);   // igual que tu base (entero)

//            // 0KB→0, 2000→-5, 3000→-10, 4000→-15 (mismo efecto que tus umbrales, pero suave)
//            double delta = PiecewiseSmooth(kb,
//                new[] { 0.0, 2000.0, 3000.0, 4000.0 },
//                new[] { 0.0, -5.0, -10.0, -15.0 });

//            int q = qBaseInt + (int)Math.Round(delta);
//            return Math.Clamp(q, 45, 99);
//        }
//    }

//    public class SucaCompressor
//    {
//        public void Procesar(string rutaEntrada, int a, int b, int c, int d, string formatoSalida, string carpetaSalida)
//        {
//            if (!File.Exists(rutaEntrada))
//            {
//                Console.WriteLine("❌ Ruta no válida.");
//                return;
//            }
//            if (!Directory.Exists(carpetaSalida))
//                Directory.CreateDirectory(carpetaSalida);

//            var cronometroCarga = Stopwatch.StartNew();
//            using var imagen = Image.Load<Rgba32>(rutaEntrada);
//            cronometroCarga.Stop();

//            // NUEVO: Respeta orientación EXIF antes de cualquier operación
//            imagen.Mutate(p => p.AutoOrient());

//            string extensionOriginal = Path.GetExtension(rutaEntrada).ToUpperInvariant();
//            long tamOriginalBytes = new FileInfo(rutaEntrada).Length;
//            double tamOriginalKB = tamOriginalBytes / 1024.0;
//            int anchoOriginal = imagen.Width;
//            int altoOriginal = imagen.Height;
//            long pixelesOriginales = (long)anchoOriginal * altoOriginal;

//            int hash = SucaMath.HashMinimalista(a, b, c, d);
//            double escala = SucaMath.FactorEscala(a, b, c, d, tamOriginalKB);
//            int calidad = SucaMath.Calidad(hash, tamOriginalKB);

//            int nuevoAncho = (int)Math.Round(anchoOriginal * escala);
//            int nuevoAlto = (int)Math.Round(altoOriginal * escala);
//            long pixelesFinales = (long)nuevoAncho * nuevoAlto;

//            var cronometroEscala = Stopwatch.StartNew();
//            imagen.Mutate(x => x.Resize(new ResizeOptions
//            {
//                Mode = ResizeMode.Max,
//                Sampler = KnownResamplers.MitchellNetravali, // conservamos tu estética
//                Size = new Size(nuevoAncho, nuevoAlto)
//            }));
//            cronometroEscala.Stop();

//            string extensionSalida = formatoSalida.ToUpper() switch
//            {
//                "WEBP" => ".webp",
//                "PNG" => ".png",
//                _ => ".jpg"
//            };
//            string salida = Path.Combine(carpetaSalida, "comprimido_suca" + extensionSalida);

//            var cronometroGuardado = Stopwatch.StartNew();
//            switch (formatoSalida.ToUpper())
//            {
//                case "WEBP":
//                    imagen.Save(salida, new WebpEncoder
//                    {
//                        Quality = calidad,
//                        FileFormat = WebpFileFormatType.Lossy
//                    });
//                    break;
//                case "PNG":
//                    imagen.Save(salida, new PngEncoder { CompressionLevel = PngCompressionLevel.BestCompression });
//                    break;
//                default:
//                    imagen.Save(salida, new JpegEncoder { Quality = calidad });
//                    break;
//            }
//            cronometroGuardado.Stop();

//            long tamFinalBytes = new FileInfo(salida).Length;
//            double tamFinalKB = tamFinalBytes / 1024.0;
//            double ahorro = 100 - ((tamFinalKB / tamOriginalKB) * 100);
//            double reduccionPixeles = 100 - ((pixelesFinales / (double)pixelesOriginales) * 100);

//            // Reporte técnico (intacto)
//            Console.WriteLine("=== Compresor SUCA ===");
//            Console.WriteLine("=== Compresor SUCA con Reporte Completo ===");
//            Console.WriteLine("== REPORTE TÉCNICO SUCA ==");
//            Console.WriteLine($"📄 Formato original: {extensionOriginal}");
//            Console.WriteLine($"🔢 Bits por píxel: {imagen.PixelType.BitsPerPixel}");
//            Console.WriteLine($"📏 Resolución original: {anchoOriginal} x {altoOriginal} px");
//            Console.WriteLine($"🧮 Píxeles originales: {pixelesOriginales:N0}");
//            Console.WriteLine($"💾 Tamaño original: {tamOriginalKB:F2} KB");
//            Console.WriteLine("--- Parámetros SUCA ---");
//            Console.WriteLine($"A={a}, B={b}, C={c}, D={d}");
//            Console.WriteLine($"Hash={hash}, Escala ajustada={escala:F2}, Calidad={calidad}");
//            Console.WriteLine("--- Resultado ---");
//            Console.WriteLine($"📏 Nueva resolución: {nuevoAncho} x {nuevoAlto} px");
//            Console.WriteLine($"🧮 Píxeles finales: {pixelesFinales:N0}");
//            Console.WriteLine($"📉 Reducción de píxeles: {reduccionPixeles:F2}%");
//            Console.WriteLine($"💾 Tamaño final: {tamFinalKB:F2} KB");
//            Console.WriteLine($"📉 Ahorro en tamaño: {ahorro:F2}%");
//            Console.WriteLine($"📦 Formato salida: {formatoSalida.ToUpper()}");
//            Console.WriteLine($"⏱ Tiempo carga: {cronometroCarga.ElapsedMilliseconds} ms");
//            Console.WriteLine($"⏱ Tiempo escalado: {cronometroEscala.ElapsedMilliseconds} ms");
//            Console.WriteLine($"⏱ Tiempo guardado: {cronometroGuardado.ElapsedMilliseconds} ms");
//            Console.WriteLine("== FIN REPORTE ==");
//        }
//    }

//    class Program
//    {
//        static void Main()
//        {
//            Console.WriteLine("=== Compresor SUCA ===");
//            string ruta = @"C:\Users\TU_USUARIO\Desktop\pls.jpg";
//            string carpetaSalida = @"C:\Users\TU_USUARIO\Desktop";

//            var compresor = new SucaCompressor();
//            // Prueba con parámetros
//            compresor.Procesar(ruta, 0, 0, 0, 5, "png", carpetaSalida);

//            Console.WriteLine("Pulsa una tecla para salir...");
//            Console.ReadKey();
//        }
//    }
//}
