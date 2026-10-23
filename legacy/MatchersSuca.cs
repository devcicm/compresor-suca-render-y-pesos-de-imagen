using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Quantization;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms; // requiere <UseWindowsForms>true</UseWindowsForms> y net8.0-windows
using Image = SixLabors.ImageSharp.Image;
using Size = SixLabors.ImageSharp.Size;

// ============================================================
// SUCA Console: investigación, planeación y calibración in-memory
// - FileDialog multi-selección (Windows)
// - Estimación heurística sizeOut ≈ sizeIn * s^2 * α * (Q/75)^β + meta
// - Medición real (in-memory) Top-K por formato y recálculo de α
// - Reporte técnico y frontera de Pareto (reducción vs dimensión)
// - Mantiene la "esencia" SUCA (HashMinimalista, FactorEscala, Calidad)
// ============================================================

namespace SUCA_Console
{
    public static class SucaMath
    {
        // Hash minimalista (0..9 entradas esperadas)
        public static int HashMinimalista(int a, int b, int c, int d)
        {
            int raw = (a << 3) + (b << 2) + (c << 1) + d; // mezcla simple
            return (raw & 0x7F) % 95 + 32;                 // 32..126
        }

        // Escala base con matriz M y ajuste sinusoidal. Mantiene esencia.
        public static double FactorEscala(int a, int b, int c, int d, double tamKB)
        {
            int[,] M = { { 3, 2 }, { 1, 4 } };
            int x = (a * M[0, 0]) + (b * M[0, 1]);
            int y = (c * M[1, 0]) + (d * M[1, 1]);
            int suma = x + y;

            double baseEscala = 0.30 + (suma % 70) / 100.0; // 0.30..0.99
            double ajuste = 0.85 + 0.15 * Math.Sin(suma);
            double escala = baseEscala * ajuste;

            // Gates suaves por tamaño (evita escalas demasiado grandes en 4K/8K)
            if (tamKB > 8000) escala *= 0.70;
            else if (tamKB > 4000) escala *= 0.78;
            else if (tamKB > 3000) escala *= 0.84;
            else if (tamKB > 2000) escala *= 0.90;

            return Math.Clamp(escala, 0.30, 1.00);
        }

        // Calidad en 45..99 con ajuste por tamaño; sin categorías discretas
        public static int Calidad(int hash, double tamKB)
        {
            double variacion = (Math.Sin(hash) + 1.0) * 0.5; // 0..1
            int q = (int)Math.Round(60 + variacion * 39);    // 60..99 aprox

            if (tamKB > 8000) q = Math.Max(45, q - 20);
            else if (tamKB > 4000) q = Math.Max(45, q - 15);
            else if (tamKB > 3000) q = Math.Max(48, q - 10);
            else if (tamKB > 2000) q = Math.Max(52, q - 6);

            return Math.Clamp(q, 45, 99);
        }
    }

    public enum SucaMode { SUCA_Original, SUCA_Suave }

    public enum OutFmt { jpg, png, webp }

    // Guardrails y pesos (ajustan calidad mínima, tiempo y piso de resolución)
    public static class Config
    {
        public const int MinLongSidePx = 1400; // si la fuente es mayor a esto, no bajamos de aquí
        public const int QFloorLarge = 60;     // piso de Q para salidas grandes
        public const int QFloorMed = 55;     // piso de Q para salidas medianas
        public const int QFloorSmall = 50;     // piso de Q para salidas pequeñas

        public static readonly Dictionary<OutFmt, double> TimeWeights = new()
        {
            { OutFmt.jpg,  1.0 }, // rápido
            { OutFmt.webp, 1.6 }, // moderado
            { OutFmt.png,  2.2 }  // más lento
        };

        public const double TimePenaltyWeight = 0.05; // contribución a la penalización del score
    }

    public sealed class Calib
    {
        public double Alpha; // factor base
        public double Beta;  // exponente de (Q/75)
        public double MetaKB; // overhead y headers
        public Calib(double a, double b, double meta) { Alpha = a; Beta = b; MetaKB = meta; }
        public Calib Clone() => new Calib(Alpha, Beta, MetaKB);
    }

    public static class Calibs
    {
        // Semillas razonables (se recalibran por imagen vía medición real)
        public static readonly Dictionary<OutFmt, Calib> Seed = new()
        {
            { OutFmt.jpg,  new Calib(a:0.45,  b:1.25, meta:40) },
            { OutFmt.webp, new Calib(a:0.18,  b:1.08, meta:24) },
            { OutFmt.png,  new Calib(a:1.15, b:0.00, meta:32) } // lossless: Q no afecta (β≈0). α ajustada para que siempre reduzca al escalar.
        };
    }

    public sealed record Candidate(
        OutFmt Fmt,
        int A, int B, int C, int D,
        double Scale,
        int Q,
        int OutW,
        int OutH,
        double EstKB,
        double RedPct,
        double DimPct,
        double Score
    );

    public static class SUCA
    {
        // Estimación heurística
        public static double EstimateKB(double inKB, double scale, int q, Calib calib)
        {
            double area = scale * scale;
            double qRatio = Math.Pow(q / 75.0, calib.Beta);
            double kb = inKB * area * calib.Alpha * qRatio + calib.MetaKB;
            return Math.Max(kb, 1.0);
        }

        public static IEnumerable<Candidate> Plan(
            Size src, double inKB, SucaMode mode, ImageProfile profile, bool forcePng = false,
            int maxPerFmt = 3000) // 10k combos -> filtramos pronto
        {
            var rng = Enumerable.Range(0, 10);
            var baseCalib = Calibs.Seed.ToDictionary(k => k.Key, v => v.Value.Clone());

            int pixelsIn = src.Width * src.Height;
            int srcLongSide = Math.Max(src.Width, src.Height);
            var list = new List<Candidate>(maxPerFmt * 3);

            foreach (var fmt in new[] { OutFmt.webp, OutFmt.jpg, OutFmt.png })
            {
                Calib calib = baseCalib[fmt];

                // Saltar PNG si el perfil sugiere que no conviene (foto sin alpha y grande)
                bool allowPng =
                (profile?.HasAlpha ?? false) ||                     // transparencia real
                (profile?.IsGraphics ?? false) ||                   // gráficos/flat
                ((profile?.InputIsPng ?? false) && inKB < 1024) ||  // PNG de entrada pequeño
                inKB < 512;                                         // archivos realmente pequeños
                if (fmt == OutFmt.png && !allowPng && !forcePng)
                    continue;

                // exploración: todos los a,b,c,d en 0..9 (10k) con pruning por escala
                foreach (int a in rng)
                    foreach (int b in rng)
                        foreach (int c in rng)
                            foreach (int d in rng)
                            {
                                int hash = SucaMath.HashMinimalista(a, b, c, d);
                                double s = SucaMath.FactorEscala(a, b, c, d, inKB);

                                // "SUCA_Suave": penalización continua leve si s>0.7 para 4K+
                                if (mode == SucaMode.SUCA_Suave && inKB > 3000 && s > 0.70)
                                    s *= 0.92; // suaviza sin romper esencia

                                if (s < 0.30) continue; // fuera de rango útil

                                int outW = (int)Math.Round(src.Width * s);
                                int outH = (int)Math.Round(src.Height * s);
                                if (outW < 16 || outH < 16) continue; // degenerate

                                int longSide = Math.Max(outW, outH);
                                // Piso por lado largo si la fuente es grande
                                if (srcLongSide > Config.MinLongSidePx && longSide < Config.MinLongSidePx)
                                    continue;

                                int q = SucaMath.Calidad(hash, inKB);

                                // Piso de calidad por resolución de salida (no aplica a PNG lossless)
                                if (fmt != OutFmt.png)
                                {
                                    int qFloor = longSide >= 2200 ? Config.QFloorLarge : (longSide >= 1600 ? Config.QFloorMed : Config.QFloorSmall);
                                    if (q < qFloor) q = qFloor;
                                }

                                // Para PNG tratamos Q como esfuerzo/compresión, pero β≈0 -> efecto nulo en estimate
                                double estKB = EstimateKB(inKB, s, q, calib);
                                if (fmt == OutFmt.png && estKB >= inKB) continue; // garantiza que PNG no aumente
                                double red = 100.0 - (estKB / inKB * 100.0);
                                double dim = (outW * (double)outH) / pixelsIn * 100.0; // % área

                                // score balanceado: prioriza reducción y retiene algo de dimensión
                                double timeProxy = Config.TimeWeights[fmt] * dim; // aproximación del coste de tiempo
                                double score = (red * 0.70) + (dim * 0.25) - (Config.TimePenaltyWeight * timeProxy);

                                list.Add(new Candidate(fmt, a, b, c, d, s, q, outW, outH, estKB, red, dim, score));
                            }
            }

            // Orden global por reducción (top-N) + por score (balanceado)
            // Mantén una cuota por formato para no perder JPG/PNG si WEBP domina.
            return list
                .GroupBy(c => c.Fmt)
                .SelectMany(g => g.OrderByDescending(x => x.RedPct)
                                  .ThenBy(x => x.EstKB)
                                  .Take(maxPerFmt))
                .ToList();
        }

        public static List<Candidate> ParetoFront(IEnumerable<Candidate> items)
        {
            // Frontera pareto en 2D: (dim% alto, red% alto)
            var pts = items.OrderByDescending(c => c.RedPct).ThenByDescending(c => c.DimPct).ToList();
            var front = new List<Candidate>();
            double bestDim = double.NegativeInfinity;
            foreach (var c in pts)
            {
                if (c.DimPct > bestDim)
                {
                    front.Add(c);
                    bestDim = c.DimPct;
                }
            }
            return front.Take(12).ToList();
        }

        // Nuevo: selección por presupuesto (peso mosca)
        public static Candidate PickByTargetKB(IEnumerable<Candidate> planned, double targetKB)
        {
            if (planned == null) throw new ArgumentNullException(nameof(planned));
            var candidates = planned.ToList();
            if (candidates.Count == 0) throw new InvalidOperationException("No hay candidatos.");

            var ok = candidates.Where(c => c.EstKB <= targetKB).ToList();
            if (ok.Count > 0)
            {
                return ok.OrderByDescending(c => c.DimPct)
                         .ThenByDescending(c => c.Score)
                         .First();
            }

            return candidates.OrderBy(c => Math.Abs(c.EstKB - targetKB)).First();
        }

        // Medición real in-memory sobre Top-K por formato; recalibra α (y mantiene β)
        public static (Dictionary<OutFmt, Calib> calib, List<(Candidate cand, double realKB, double tResizeMs, double tEncMs)> rows)
            MeasureAndRecalibrate(Image<Rgba32> src, IEnumerable<Candidate> planned, double inKB, ImageProfile profile, int topK = 3)
        {
            var byFmt = planned.GroupBy(c => c.Fmt)
                               .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.RedPct).Take(topK).ToList());

            var newCalib = Calibs.Seed.ToDictionary(k => k.Key, v => v.Value.Clone());
            var rows = new List<(Candidate, double, double, double)>();

            foreach (var kv in byFmt)
            {
                var fmt = kv.Key;
                var sample = kv.Value;
                var ratios = new List<double>();

                foreach (var cand in sample)
                {
                    var swR = Stopwatch.StartNew();
                    using var resized = src.Clone(ctx => ctx.Resize(new ResizeOptions
                    {
                        Size = new Size(cand.OutW, cand.OutH),
                        Mode = ResizeMode.Max,
                        Sampler = KnownResamplers.MitchellNetravali
                    }));
                    swR.Stop();

                    using var ms = new MemoryStream();
                    var swE = Stopwatch.StartNew();
                    switch (fmt)
                    {
                        case OutFmt.jpg:
                            resized.Save(ms, new JpegEncoder { Quality = cand.Q }); // Quality 1..100
                            break;
                        case OutFmt.webp:
                            resized.Save(ms, new WebpEncoder
                            {
                                Quality = cand.Q, // 0..100
                                FileFormat = WebpFileFormatType.Lossy, // lossy
                                NearLossless = false
                            });
                            break;
                        case OutFmt.png:
                            // PNG es lossless; usamos BestCompression
                            var pngEnc = (profile != null && profile.IsGraphics)
                            ? new PngEncoder
                            {
                                CompressionLevel = PngCompressionLevel.BestCompression,
                                Quantizer = KnownQuantizers.Wu,
                                ColorType = PngColorType.Palette
                            }
                            : new PngEncoder
                            {
                                CompressionLevel = PngCompressionLevel.BestCompression
                            };
                            resized.Save(ms, pngEnc);
                            break;
                    }
                    swE.Stop();

                    double realKB = Math.Max(ms.Length / 1024.0, 1.0);
                    rows.Add((cand, realKB, swR.Elapsed.TotalMilliseconds, swE.Elapsed.TotalMilliseconds));

                    // α := mediana( (KB_real - meta) / (inKB * s^2 * (Q/75)^β) )
                    var calib = newCalib[fmt];
                    double denom = inKB * cand.Scale * cand.Scale * Math.Pow(cand.Q / 75.0, calib.Beta);
                    if (denom > 0.0)
                        ratios.Add(Math.Max((realKB - calib.MetaKB) / denom, 1e-6));
                }

                if (ratios.Count > 0)
                {
                    var calib = newCalib[fmt];
                    calib.Alpha = Percentile(ratios, 0.5); // robustez -> mediana
                }
            }

            return (newCalib, rows);
        }

        private static double Percentile(List<double> values, double p)
        {
            values.Sort();
            if (values.Count == 0) return 0;
            double r = (values.Count - 1) * p;
            int lo = (int)Math.Floor(r);
            int hi = (int)Math.Ceiling(r);
            if (lo == hi) return values[lo];
            return values[lo] + (values[hi] - values[lo]) * (r - lo);
        }
    }

    public static class Printer
    {
        public static void PrintHeader(string title)
        {
            Console.WriteLine($@"=== {title} ===
");
        }

        public static void PrintPlanner(Size src, double inKB)
        {
            Console.WriteLine("=== SUCA Planner - Reporte Técnico (estimación en memoria) ===");
            Console.WriteLine($"Entrada: {src.Width}x{src.Height}  | {inKB:F2} KB  | Pixeles: {((long)src.Width * src.Height):N0}");
            Console.WriteLine(@"Heurística: sizeOut ~ sizeIn * scale^2 * α * (Q/75)^β + MetaKB
");
        }

        public static void PrintTable(string head, IEnumerable<Candidate> rows, int cols = 6)
        {
            Console.WriteLine(head);
            Console.WriteLine("Formato  A B C D | Escala  Q  | OutWxH        | KB_Estim  Reducción");
            Console.WriteLine(new string('-', 69));
            foreach (var c in rows)
            {
                Console.WriteLine($"  {c.Fmt,-4}  {c.A} {c.B} {c.C} {c.D} |  {c.Scale,0:0.000} {c.Q,2} |  {c.OutW,4}x{c.OutH,-8}  |  {c.EstKB,7:0.0}  {c.RedPct,10:0.00}%");
            }
            Console.WriteLine(new string('-', 69));
        }

        public static void PrintBest(string label, Candidate bestRed, Candidate bestScore)
        {
            Console.WriteLine($@">> Mejor por reducción: {bestRed.Fmt}  A={bestRed.A} B={bestRed.B} C={bestRed.C} D={bestRed.D}  Escala={bestRed.Scale:0.000}  Q={bestRed.Q}  Out={bestRed.OutW}x{bestRed.OutH}  KB~{bestRed.EstKB:0.0}  Reducción={bestRed.RedPct:0.00}%");
            Console.WriteLine($@">> Mejor balanceado:    {bestScore.Fmt}  A={bestScore.A} B={bestScore.B} C={bestScore.C} D={bestScore.D}  Escala={bestScore.Scale:0.000}  Q={bestScore.Q}  Out={bestScore.OutW}x{bestScore.OutH}  KB~{bestScore.EstKB:0.0}  Reducción={bestScore.RedPct:0.00}%  Score={bestScore.Score:0.00}
");
        }

        public static void PrintPareto(List<Candidate> pareto)
        {
            Console.WriteLine("— Frontera de Pareto (reducción vs dimensión), 12 puntos —");
            Console.WriteLine("Formato  A B C D | Escala  Q  | Dim%   Reducción%  KB_Estim  OutWxH");
            Console.WriteLine(new string('-', 69));
            foreach (var c in pareto)
                Console.WriteLine($"  {c.Fmt,-4}  {c.A} {c.B} {c.C} {c.D} |  {c.Scale,0:0.000} {c.Q,2} |  {c.DimPct,4:0.0}        {c.RedPct,6:0.00}     {c.EstKB,7:0.1}  {c.OutW}x{c.OutH}");
        }
    }

    internal static class Dialogs
    {
        public static string[] PickFilesWithDialog()
        {
            try
            {
                using var ofd = new OpenFileDialog
                {
                    Title = "Selecciona una o varias imágenes",
                    Filter = "Imágenes|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.tif;*.tiff|Todos|*.*",
                    Multiselect = true,
                    CheckFileExists = true
                };
                return ofd.ShowDialog() == DialogResult.OK ? ofd.FileNames : Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }

    public static class Util
    {
        public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        public static string SizeFmt(double kb) => kb >= 1024 ? $"{kb / 1024.0:0.00} MB" : $"{kb:0.00} KB";
    }

    // Perfil simple de imagen para decidir estrategias por formato
    public sealed record ImageProfile(bool HasAlpha, bool IsGraphics, bool InputIsPng);

    public static class Analyzer
    {
        public static ImageProfile InferProfile(Image<Rgba32> img, string? ext)
        {
            bool inputIsPng = string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase);
            bool hasAlpha = HasAnyAlpha(img);

            // Muestreo reducido
            int sw = Math.Min(128, img.Width);
            int sh = Math.Min(128, img.Height);
            using var small = img.Clone(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(sw, sh),
                Mode = ResizeMode.Max,
                Sampler = KnownResamplers.Bicubic
            }));

            // (a) diversidad de color (5 bits por canal)
            var uniq = new HashSet<int>();
            // (b) textura simple: media de |Δluma| h/v
            double madSum = 0; int madN = 0;

            small.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        var p = row[x];
                        int r = p.R >> 3, g = p.G >> 3, b = p.B >> 3;
                        uniq.Add((r << 10) | (g << 5) | b);

                        double y0 = 0.2126 * p.R + 0.7152 * p.G + 0.0722 * p.B;
                        if (x + 1 < row.Length)
                        {
                            var pr = row[x + 1];
                            double y1 = 0.2126 * pr.R + 0.7152 * pr.G + 0.0722 * pr.B;
                            madSum += Math.Abs(y1 - y0); madN++;
                        }
                        if (y + 1 < accessor.Height)
                        {
                            var next = accessor.GetRowSpan(y + 1)[x];
                            double y2 = 0.2126 * next.R + 0.7152 * next.G + 0.0722 * next.B;
                            madSum += Math.Abs(y2 - y0); madN++;
                        }
                    }
                }
            });

            double uniqueRatio = uniq.Count / (double)(sw * sh);   // 0..1
            double mad = madN > 0 ? madSum / madN : 0;              // 0..255 aprox

            // Para ser "gráfico" exigimos POCOS colores y POCA textura
            bool isGraphics = (uniqueRatio < 0.28) && (mad < 7.5);

            return new ImageProfile(hasAlpha, isGraphics, inputIsPng);
        }

        private static bool HasAnyAlpha(Image<Rgba32> img)
        {
            bool any = false;
            img.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height && !any; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        if (row[x].A < 255) { any = true; break; }
                    }
                }
            });
            return any;
        }
    }

    // ================================================
    // Conversor directo base (compatible con SUCA_Console)
    // Port de tu clase SucaCompressor en SUCA_Compressor
    // ================================================
    public static class SucaCompressor
    {
        // Procesa un archivo con A,B,C,D y guarda como JPG/PNG/WEBP en carpetaSalida
        // Garantías:
        //  - Respeta AutoOrient
        //  - Aplica la misma escala/calidad de SucaMath
        //  - Para PNG intenta cuantizar (si gráficos) y NO permite aumentar tamaño; cae a WEBP si es necesario
        public static string Procesar(string rutaEntrada, int a, int b, int c, int d, string formatoSalida, string carpetaSalida, bool ensureReduction = true)
        {
            if (!File.Exists(rutaEntrada))
                throw new FileNotFoundException("Ruta no válida", rutaEntrada);

            if (!Directory.Exists(carpetaSalida))
                Directory.CreateDirectory(carpetaSalida);

            using var imagen = Image.Load<Rgba32>(rutaEntrada);
            imagen.Mutate(p => p.AutoOrient());

            string extIn = Path.GetExtension(rutaEntrada);
            var profile = Analyzer.InferProfile(imagen, extIn);

            long inBytes = new FileInfo(rutaEntrada).Length;
            double inKB = Math.Max(inBytes / 1024.0, 1.0);

            int srcW = imagen.Width;
            int srcH = imagen.Height;
            int srcLong = Math.Max(srcW, srcH);

            int hash = SucaMath.HashMinimalista(a, b, c, d);
            double scale = SucaMath.FactorEscala(a, b, c, d, inKB);

            // Piso por lado largo si la fuente es grande
            int targetLong = (int)Math.Round(srcLong * scale);
            if (srcLong > Config.MinLongSidePx && targetLong < Config.MinLongSidePx)
            {
                scale = (double)Config.MinLongSidePx / srcLong;
            }

            int outW = (int)Math.Round(srcW * scale);
            int outH = (int)Math.Round(srcH * scale);
            int outLong = Math.Max(outW, outH);

            int q = SucaMath.Calidad(hash, inKB);
            string fmtUpper = (formatoSalida ?? "jpg").Trim().ToUpperInvariant();

            if (fmtUpper != "PNG")
            {
                int qFloor = outLong >= 2200 ? Config.QFloorLarge : (outLong >= 1600 ? Config.QFloorMed : Config.QFloorSmall);
                if (q < qFloor) q = qFloor;
            }

            // Redimensiona
            imagen.Mutate(x => x.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Sampler = KnownResamplers.MitchellNetravali,
                Size = new Size(outW, outH)
            }));

            // Ruta de salida
            string baseName = Path.GetFileNameWithoutExtension(rutaEntrada);
            string outExt = fmtUpper switch { "WEBP" => ".webp", "PNG" => ".png", _ => ".jpg" };
            string outPath = Path.Combine(carpetaSalida, $"{baseName}_suca{outExt}");

            // Guardado según formato
            using var ms = new MemoryStream();
            if (fmtUpper == "WEBP")
            {
                imagen.Save(ms, new WebpEncoder { Quality = q, FileFormat = WebpFileFormatType.Lossy, NearLossless = false });
            }
            else if (fmtUpper == "PNG")
            {
                // Si es gráfico o con pocos colores -> paleta (reduce mucho). Si no, solo compresión.
                var pngEnc = (profile.IsGraphics || profile.HasAlpha)
                    ? new PngEncoder { CompressionLevel = PngCompressionLevel.BestCompression, Quantizer = KnownQuantizers.Wu, ColorType = PngColorType.Palette }
                    : new PngEncoder { CompressionLevel = PngCompressionLevel.BestCompression };
                imagen.Save(ms, pngEnc);
            }
            else // JPG
            {
                imagen.Save(ms, new JpegEncoder { Quality = q });
            }

            // Verificación de reducción para PNG
            if (fmtUpper == "PNG" && ensureReduction)
            {
                double outKBProbe = ms.Length / 1024.0;
                if (outKBProbe >= inKB)
                {
                    // Si no reduce, caemos a WEBP (manteniendo q)
                    ms.SetLength(0);
                    imagen.Save(ms, new WebpEncoder { Quality = q, FileFormat = WebpFileFormatType.Lossy, NearLossless = false });
                    outExt = ".webp";
                    outPath = Path.Combine(carpetaSalida, $"{baseName}_suca{outExt}");
                    fmtUpper = "WEBP";
                }
            }

            // Graba al disco
            File.WriteAllBytes(outPath, ms.ToArray());

            // Log corto (quien llame puede imprimir más si quiere)
            double outKB = new FileInfo(outPath).Length / 1024.0;
            double redPct = 100.0 - (outKB / inKB * 100.0);
            Console.WriteLine($"[SUCA] {Path.GetFileName(rutaEntrada)} -> {Path.GetFileName(outPath)} | {inKB:0.0}KB → {outKB:0.0}KB (-{redPct:0.00}%) | Scale={scale:0.000} Q={q}");

            return outPath;
        }

        // === Nuevo: guardado directo desde un candidato planificado (peso mosca / knee) ===
        public static string GuardarCandidato(Image<Rgba32> src, string rutaEntrada, Candidate cand, string outputDir, ImageProfile profile, double inKB)
        {
            if (string.IsNullOrWhiteSpace(outputDir)) outputDir = Path.GetDirectoryName(rutaEntrada) ?? Environment.CurrentDirectory;

            string baseName = Path.GetFileNameWithoutExtension(rutaEntrada);
            string ext = cand.Fmt switch { OutFmt.jpg => ".jpg", OutFmt.webp => ".webp", OutFmt.png => ".png", _ => ".jpg" };
            string outPath = Path.Combine(outputDir, $"{baseName}_suca{ext}");

            using var resized = src.Clone(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(cand.OutW, cand.OutH),
                Mode = ResizeMode.Max,
                Sampler = KnownResamplers.MitchellNetravali
            }));

            using var ms = new MemoryStream();
            if (cand.Fmt == OutFmt.jpg)
            {
                resized.Save(ms, new JpegEncoder { Quality = cand.Q });
            }
            else if (cand.Fmt == OutFmt.webp)
            {
                resized.Save(ms, new WebpEncoder { Quality = cand.Q, FileFormat = WebpFileFormatType.Lossy, NearLossless = false });
            }
            else // PNG
            {
                var pngEnc = (profile.IsGraphics || profile.HasAlpha)
                    ? new PngEncoder { CompressionLevel = PngCompressionLevel.BestCompression, Quantizer = KnownQuantizers.Wu, ColorType = PngColorType.Palette }
                    : new PngEncoder { CompressionLevel = PngCompressionLevel.BestCompression };
                resized.Save(ms, pngEnc);

                // Seguridad: si no reduce frente al original, cae a WEBP con la misma Q
                if (ms.Length / 1024.0 >= inKB)
                {
                    ms.SetLength(0);
                    resized.Save(ms, new WebpEncoder { Quality = cand.Q, FileFormat = WebpFileFormatType.Lossy, NearLossless = false });
                    ext = ".webp";
                    outPath = Path.Combine(outputDir, $"{baseName}_suca{ext}");
                }
            }

            File.WriteAllBytes(outPath, ms.ToArray());
            return outPath;
        }
    }

    public class App
    {
        public void Run(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Printer.PrintHeader("SUCA Investigación & Calibración (en memoria)");

            string[] files = Array.Empty<string>();
            bool forcePng = args.Any(a => string.Equals(a, "--force-png", StringComparison.OrdinalIgnoreCase));

            double? targetKB = null;
            bool applyTarget = args.Any(a =>
                string.Equals(a, "--save-target", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "--aplicar-target", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "--aplicar", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "--save", StringComparison.OrdinalIgnoreCase));

            string? outDir = null;
            foreach (var a in args)
            {
                if (a.StartsWith("--target-kb=", StringComparison.OrdinalIgnoreCase) ||
                    a.StartsWith("--peso-mosca=", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = a.Split('=', 2);
                    if (parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var tkb))
                        targetKB = Math.Max(1, tkb);
                }
                else if (a.StartsWith("--outdir=", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = a.Split('=', 2);
                    if (parts.Length == 2)
                    {
                        var v = parts[1].Trim().Trim('"');
                        if (!string.IsNullOrWhiteSpace(v)) outDir = v;
                    }
                }
            }

            if (args.Length > 0)
            {
                files = args.Where(File.Exists).ToArray();
            }
            else if (Util.IsWindows)
            {
                files = Dialogs.PickFilesWithDialog();
            }

            if (files.Length == 0)
            {
                Console.WriteLine("No se seleccionaron archivos. Arrastra y suelta rutas o pega una por línea (vacío para terminar):");
                var dyn = new List<string>();
                while (true)
                {
                    string? line = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(line)) break;
                    if (File.Exists(line)) dyn.Add(line);
                    else Console.WriteLine("Ruta inválida, intenta de nuevo.");
                }
                files = dyn.ToArray();
                if (files.Length == 0) return;
            }

            foreach (var path in files)
            {
                try
                {
                    using var img = Image.Load<Rgba32>(path);
                    var srcSize = new Size(img.Width, img.Height);
                    long inBytes = new FileInfo(path).Length;
                    double inKB = Math.Max(inBytes / 1024.0, 1.0);

                    Console.WriteLine($@"

=== Archivo: {Path.GetFileName(path)} === ");
                    Console.WriteLine($"Fuente: {srcSize.Width}x{srcSize.Height}  | {Util.SizeFmt(inKB)}  | bpp: {img.PixelType.BitsPerPixel}");

                    // Analiza la imagen para decidir si conviene PNG (alpha/gráficos)
                    var profile = Analyzer.InferProfile(img, Path.GetExtension(path));
                    Console.WriteLine($"Perfil: alpha={(profile.HasAlpha ? "sí" : "no")}, gráficos={(profile.IsGraphics ? "sí" : "no")}, fuentePNG={(profile.InputIsPng ? "sí" : "no")}");
// Info de gating PNG
bool allowPngByProfile = (profile?.HasAlpha ?? false) ||
                         (profile?.IsGraphics ?? false) ||
                         ((profile?.InputIsPng ?? false) && inKB < 1024) ||
                         inKB < 512;
                    Console.WriteLine($"PNG habilitado: {(allowPngByProfile || forcePng ? "sí" : "no")} {(forcePng ? "(forzado)" : allowPngByProfile ? string.Empty : "(foto sin alpha/gráficos y fuente grande)")}");

                    foreach (var mode in new[] { SucaMode.SUCA_Original, SucaMode.SUCA_Suave })
                    {
                        Console.WriteLine($"=== Modo: {mode} ===");

                        Printer.PrintPlanner(srcSize, inKB);
                        var planned = SUCA.Plan(srcSize, inKB, mode, profile, forcePng).ToList();

                        // Top N global por reducción
                        var topGlobal = planned.OrderByDescending(c => c.RedPct).Take(12).ToList();
                        Printer.PrintTable("Top 12 por % reducción (GLOBAL):", topGlobal);
                        var bestRed = topGlobal.First();
                        var bestScore = planned.OrderByDescending(c => c.Score).First();
                        Printer.PrintBest("Global", bestRed, bestScore);

                        // Por formato
                        foreach (var fmt in new[] { OutFmt.jpg, OutFmt.png, OutFmt.webp })
                        {
                            var topFmt = planned.Where(c => c.Fmt == fmt)
                            .OrderByDescending(c => c.RedPct)
                            .Take(6)
                            .ToList();
                            Printer.PrintTable($"### Formato: {fmt.ToString().ToUpperInvariant()}", topFmt);
                            if (topFmt.Count > 0)
                            {
                                var bf = topFmt.First();
                                var bs = planned.Where(c => c.Fmt == fmt).OrderByDescending(c => c.Score).First();
                                Console.WriteLine($@"> Mejor {fmt} por reducción: A={bf.A} B={bf.B} C={bf.C} D={bf.D}  Escala={bf.Scale:0.000}  Q={bf.Q}  Out={bf.OutW}x{bf.OutH}  KB~{bf.EstKB:0.0}  Reducción={bf.RedPct:0.00}%");
                                Console.WriteLine($@"> Mejor {fmt} balanceado:    A={bs.A} B={bs.B} C={bs.C} D={bs.D}  Escala={bs.Scale:0.000}  Q={bs.Q}  Out={bs.OutW}x{bs.OutH}  KB~{bs.EstKB:0.0}  Reducción={bs.RedPct:0.00}%  Score={bs.Score:0.00}
");
                            }
                            else
                            {
                                Console.WriteLine($"> Sin candidatos para {fmt} (posible dominancia de otro formato en este dataset).");
                            }
                        }

                        var pareto = SUCA.ParetoFront(planned);
                        Printer.PrintPareto(pareto);
                        var knee = pareto.OrderBy(c => Math.Abs(c.Scale - 0.45)).FirstOrDefault()
                                   ?? planned.OrderByDescending(c => c.Score).First();
                        Console.WriteLine($">> Recomendado (knee ~0.45): {knee.Fmt}  Escala={knee.Scale:0.000}  Q={knee.Q}  Out={knee.OutW}x{knee.OutH}  KB~{knee.EstKB:0.0}  Reducción={knee.RedPct:0.00}%  Score={knee.Score:0.00}");
                        if (targetKB.HasValue)
                        {
                            var byBudget = SUCA.PickByTargetKB(planned, targetKB.Value);
                            Console.WriteLine($">> Recomendado (target {targetKB.Value:0} KB): {byBudget.Fmt}  Escala={byBudget.Scale:0.000}  Q={byBudget.Q}  Out={byBudget.OutW}x{byBudget.OutH}  KB~{byBudget.EstKB:0.0}  Reducción={byBudget.RedPct:0.00}%");

                            if (applyTarget)
                            {
                                var outDirEff = outDir ?? Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
                                try { Directory.CreateDirectory(outDirEff); } catch { }
                                var saved = SucaCompressor.GuardarCandidato(img, path, byBudget, outDirEff, profile, inKB);
                                Console.WriteLine($">> Guardado por 'peso mosca' → {Path.GetFileName(saved)}");
                            }
                        }

                        // Medición real + recálculo de α
                        var (newCalib, rows) = SUCA.MeasureAndRecalibrate(img, planned, inKB, profile, topK: 3);

                        // Imprime por formato (real vs pred)
                        foreach (var fmt in new[] { OutFmt.jpg, OutFmt.webp, OutFmt.png })
                        {
                            var set = rows.Where(r => r.cand.Fmt == fmt).ToList();
                            if (set.Count == 0) continue;

                            Console.WriteLine($"\n- Medición real (in-memory) en Top-K por formato [{mode}] -\n");
                            Console.WriteLine($"Formato {fmt.ToString().ToUpperInvariant()} (K={set.Count})");
                            Console.WriteLine("A B C D | Escala  Q | OutWxH     | KB_Pred  KB_Real  Red%_Pred  Red%_Real  tResize  tEncode");
                            foreach (var row in set)
                            {
                                var c = row.cand;
                                double redPred = c.RedPct;
                                double redReal = 100 - (row.realKB / inKB * 100);
                                Console.WriteLine($"{c.A} {c.B} {c.C} {c.D} |  {c.Scale,0:0.000} {c.Q,2} |  {c.OutW,4}x{c.OutH,-8}  |  {c.EstKB,7:0.1}  {row.realKB,7:0.1}     {redPred,7:0.00}%    {redReal,7:0.00}%   {row.tResizeMs,6:0}ms  {row.tEncMs,6:0}ms");
                            }

                            var cal = newCalib[fmt];
                            Console.WriteLine($@">> Nueva calib {fmt.ToString().ToUpper()}: α={cal.Alpha:0.###}, β={cal.Beta:0.###}, MetaKB={cal.MetaKB:0.0}
");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Error] {Path.GetFileName(path)}: {ex.Message}");
                }
            }
        }
    }

    public static class Program
    {
        [STAThread] // Necesario para OpenFileDialog
        public static void Main(string[] args)
        {
            // Cultura fija para decimales en consola
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            new App().Run(args);
            Console.WriteLine(@"
Listo.Presiona una tecla para salir...");
            try { Console.ReadKey(true); } catch { }
        }
    }
}
