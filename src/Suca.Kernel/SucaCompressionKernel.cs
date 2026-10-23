// ============================================================
// SUCA Unified AI/Compression Lab - Single File .cs
// Requiere:
//   - SixLabors.ImageSharp
//   - .NET 8+ recomendado
//
// NuGet:
//   dotnet add package SixLabors.ImageSharp
//
// Propósito:
//   - Compresión SUCA refactorizada
//   - Integridad perceptual / estructural
//   - Dataset builder
//   - Embeddings visuales
//   - OCR visual básico
//   - Tiny neural net experimental
//   - RAG local simple
// ============================================================

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SUCA_Compressor.Kernel;
using SUCA_Compressor.Shared.Imaging;
using SUCA_Compressor.Shared.Math;
using Image = SixLabors.ImageSharp.Image;
using Size = SixLabors.ImageSharp.Size;

namespace SUCA_Compressor
{
    // =========================================================
    // 1) MATH CORE
    // =========================================================
    public static class SucaMath
    {
        public static double Clamp01(double x) => x < 0 ? 0 : (x > 1 ? 1 : x);
        public static double SmoothStep01(double t) { t = Clamp01(t); return t * t * (3 - 2 * t); }
        public static double Lerp(double a, double b, double t) => a + (b - a) * t;

        public static double Sigmoid(double x) => 1.0 / (1.0 + Math.Exp(-x));

        public static double PiecewiseSmooth(double x, double[] xs, double[] ys)
        {
            int n = xs.Length;
            if (n == 0) throw new ArgumentException("xs vacío");
            if (xs.Length != ys.Length) throw new ArgumentException("xs/ys longitud distinta");

            if (x <= xs[0]) return ys[0];
            if (x >= xs[n - 1]) return ys[n - 1];

            for (int i = 0; i < n - 1; i++)
            {
                if (x >= xs[i] && x <= xs[i + 1])
                {
                    double t = (x - xs[i]) / (xs[i + 1] - xs[i]);
                    t = SmoothStep01(t);
                    return Lerp(ys[i], ys[i + 1], t);
                }
            }
            return ys[n - 1];
        }

        // === Hash SUCA original ===
        public static int HashMinimalista(int a, int b, int c, int d)
        {
            int raw = (a << 3) + (b << 2) + (c << 1) + d;
            return (raw & 0x7F) % 95 + 32;
        }

        // Variante extendida para no depender sólo del hash original
        public static int HashSucaExtendido(int a, int b, int c, int d)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + a;
                h = h * 31 + b;
                h = h * 31 + c;
                h = h * 31 + d;
                h ^= (a << 7) ^ (b << 5) ^ (c << 3) ^ d;
                h = Math.Abs(h);
                return h;
            }
        }

        // Tu fórmula base, con mejoras paramétricas por contenido
        public static double FactorEscalaBase(int a, int b, int c, int d, double kb)
        {
            int[,] M = { { 3, 2 }, { 1, 4 } };
            int x = a * M[0, 0] + b * M[0, 1];
            int y = c * M[1, 0] + d * M[1, 1];
            int suma = x + y;

            double baseEscala = 0.3 + (suma % 70) / 100.0;
            double wobble = 0.85 + 0.15 * Math.Sin(suma);

            double penal = PiecewiseSmooth(kb,
                new[] { 0.0, 1000.0, 2000.0, 3000.0, 4000.0, 8000.0, 16000.0 },
                new[] { 1.0, 0.88, 0.78, 0.68, 0.58, 0.50, 0.42 });

            double escala = baseEscala * wobble * penal;
            return Math.Clamp(escala, 0.25, 1.0);
        }

        public static int CalidadBase(int hash, double kb)
        {
            double vari = (Math.Sin(hash) + 1.0) / 2.0;
            int qBaseInt = (int)(60.0 + vari * 39.0);

            double delta = PiecewiseSmooth(kb,
                new[] { 0.0, 2000.0, 3000.0, 4000.0, 8000.0, 16000.0 },
                new[] { 0.0, -5.0, -10.0, -15.0, -20.0, -25.0 });

            int q = qBaseInt + (int)Math.Round(delta);
            return Math.Clamp(q, 35, 99);
        }

        public static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        {
            if (a.Length != b.Length || a.Length == 0) return 0;
            double dot = 0, na = 0, nb = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                na += a[i] * a[i];
                nb += b[i] * b[i];
            }
            if (na <= 0 || nb <= 0) return 0;
            return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
        }
    }

    // =========================================================
    // 2) MODELOS / DTO
    // =========================================================
    public enum SucaOutputFormat { Jpeg, Png, Webp }
    public enum ImageContentKind { Photo, Document, Ui, Fingerprint, Graphic, Unknown }

    public sealed class SucaProfile
    {
        public int A { get; init; }
        public int B { get; init; }
        public int C { get; init; }
        public int D { get; init; }

        public void Validate()
        {
            ValidateRange(nameof(A), A);
            ValidateRange(nameof(B), B);
            ValidateRange(nameof(C), C);
            ValidateRange(nameof(D), D);
        }

        private static void ValidateRange(string name, int v)
        {
            // Rango contenido para trazabilidad.
            if (v < 0 || v > 9)
                throw new ArgumentOutOfRangeException(name, $"{name} debe estar entre 0 y 9.");
        }

        public int Hash() => SucaMath.HashMinimalista(A, B, C, D);
        public int ExtendedHash() => SucaMath.HashSucaExtendido(A, B, C, D);

        public override string ToString() => $"A={A},B={B},C={C},D={D}";
    }

    public sealed class SucaOptions
    {
        public SucaOutputFormat PreferredFormat { get; set; } = SucaOutputFormat.Webp;
        public bool RemoveMetadata { get; set; } = true;
        public bool AllowLossy { get; set; } = true;
        public bool PreserveSmallImages { get; set; } = true;
        public bool UseNearLosslessForUi { get; set; } = true;
        public bool EnablePesoMosca { get; set; } = true;
        public double PesoMoscaKbTarget { get; set; } = 100.0;
        public int MaxRefineIterations { get; set; } = 8;
        public int MinWidth { get; set; } = 192;
        public int MinHeight { get; set; } = 192;
        public bool GenerateIntegrityReport { get; set; } = true;
    }

    public sealed class ImageAnalysis
    {
        public string InputPath { get; set; } = "";
        public string ExtensionOriginal { get; set; } = "";
        public long InputBytes { get; set; }
        public double InputKb { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public long PixelCount { get; set; }
        public double AspectRatio { get; set; }

        public double MeanLuma { get; set; }
        public double StdLuma { get; set; }
        public double Entropy { get; set; }
        public double EdgeDensity { get; set; }
        public double HorizontalProjectionVariance { get; set; }
        public double VerticalProjectionVariance { get; set; }
        public double ForegroundRatio { get; set; }
        public double RidgeLikeScore { get; set; } // útil para huella
        public ImageContentKind Kind { get; set; } = ImageContentKind.Unknown;

        public override string ToString()
        {
            return $"[{Kind}] {Width}x{Height}, KB={InputKb:F2}, Entropy={Entropy:F3}, Edge={EdgeDensity:F4}, Ridge={RidgeLikeScore:F4}";
        }
    }

    public sealed class SucaDecision
    {
        public int Hash { get; set; }
        public int ExtendedHash { get; set; }
        public double Scale { get; set; }
        public int Quality { get; set; }
        public SucaOutputFormat OutputFormat { get; set; }
        public bool UseNearLosslessWebp { get; set; }
        public int NearLosslessQuality { get; set; }
        public bool RemoveMetadata { get; set; }
        public ImageContentKind ContentKind { get; set; }

        public int NewWidth { get; set; }
        public int NewHeight { get; set; }

        public override string ToString()
        {
            return $"Hash={Hash}, Scale={Scale:F4}, Quality={Quality}, Format={OutputFormat}, Content={ContentKind}, New={NewWidth}x{NewHeight}";
        }
    }

    public sealed class CompressionResult
    {
        public string InputPath { get; set; } = "";
        public string OutputPath { get; set; } = "";
        public SucaDecision Decision { get; set; } = new();
        public ImageAnalysis Analysis { get; set; } = new();

        public long OutputBytes { get; set; }
        public double OutputKb { get; set; }
        public double SavingPercent { get; set; }
        public long OutputPixels { get; set; }
        public double PixelReductionPercent { get; set; }

        public long LoadMs { get; set; }
        public long AnalyzeMs { get; set; }
        public long ResizeMs { get; set; }
        public long SaveMs { get; set; }
        public long TotalMs { get; set; }

        public IntegrityReport? Integrity { get; set; }
    }

    public sealed class IntegrityReport
    {
        public string Mode { get; set; } = ""; // lossless-strong / perceptual
        public bool BitwiseEqual { get; set; }
        public string OriginalSha256 { get; set; } = "";
        public string CompressedSha256 { get; set; } = "";
        public double Mse { get; set; }
        public double Mae { get; set; }
        public double Psnr { get; set; }
        public double SsimLite { get; set; }
        public double HistogramSimilarity { get; set; }
        public double EdgeRetention { get; set; }
        public bool IsStructurallyAcceptable { get; set; }
        public string Verdict { get; set; } = "";

        public override string ToString()
        {
            return $"Mode={Mode}, Bitwise={BitwiseEqual}, PSNR={Psnr:F2}, SSIM={SsimLite:F4}, Hist={HistogramSimilarity:F4}, Edge={EdgeRetention:F4}, Verdict={Verdict}";
        }
    }

    public sealed class DatasetRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string InputPath { get; set; } = "";
        public string OutputPath { get; set; } = "";
        public string Label { get; set; } = "";
        public string Profile { get; set; } = "";
        public string ContentKind { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public double InputKb { get; set; }
        public double OutputKb { get; set; }
        public double SavingPercent { get; set; }
        public double Psnr { get; set; }
        public double SsimLite { get; set; }
        public double EdgeRetention { get; set; }
        public float[] Embedding { get; set; } = Array.Empty<float>();
        public Dictionary<string, double> Metrics { get; set; } = new();
    }

    public sealed class RagItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string SourcePath { get; set; } = "";
        public string Text { get; set; } = "";
        public float[] Embedding { get; set; } = Array.Empty<float>();
        public Dictionary<string, string> Metadata { get; set; } = new();
    }

    // =========================================================
    // 3) UTILIDADES / HASH / IO
    // =========================================================
    public static class CryptoUtil
    {
        public static string Sha256File(string path)
        {
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(path);
            var hash = sha.ComputeHash(fs);
            return Convert.ToHexString(hash);
        }

        public static string Sha256Bytes(byte[] data)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(data));
        }
    }

    public static class FileNameUtil
    {
        public static string BuildUniqueOutputPath(string inputPath, string outputDirectory, string suffix, string extension, int sucaHash)
        {
            string name = Path.GetFileNameWithoutExtension(inputPath);
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            string file = $"{name}_{suffix}_h{sucaHash}_{ts}{extension}";
            return Path.Combine(outputDirectory, file);
        }
    }

    public static class ConsoleEx
    {
        public static void Title(string t)
        {
            Console.WriteLine();
            Console.WriteLine(new string('=', 74));
            Console.WriteLine(t);
            Console.WriteLine(new string('=', 74));
        }

        public static void KV(string k, object? v)
        {
            Console.WriteLine($"{k,-28}: {v}");
        }
    }

    // =========================================================
    // 4) ANÁLISIS VISUAL
    // =========================================================
    public static class ImageAnalyzer
    {
        public static ImageAnalysis Analyze(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var img = Image.Load<Rgba32>(path);
            img.Mutate(x => x.AutoOrient());

            var analysis = new ImageAnalysis
            {
                InputPath = path,
                ExtensionOriginal = Path.GetExtension(path).ToUpperInvariant(),
                InputBytes = new FileInfo(path).Length,
                Width = img.Width,
                Height = img.Height,
            };

            analysis.InputKb = analysis.InputBytes / 1024.0;
            analysis.PixelCount = (long)analysis.Width * analysis.Height;
            analysis.AspectRatio = analysis.Height == 0 ? 0 : analysis.Width / (double)analysis.Height;

            // Convertir a escala de grises local
            var gray = ToGrayMatrix(img, cancellationToken);

            analysis.MeanLuma = Mean(gray);
            analysis.StdLuma = StdDev(gray, analysis.MeanLuma);
            analysis.Entropy = Entropy(gray);
            analysis.EdgeDensity = EdgeDensity(gray);
            analysis.HorizontalProjectionVariance = ProjectionVariance(gray, horizontal: true);
            analysis.VerticalProjectionVariance = ProjectionVariance(gray, horizontal: false);
            analysis.ForegroundRatio = ForegroundRatio(gray);
            analysis.RidgeLikeScore = RidgeLikeScore(gray);
            analysis.Kind = Classify(gray, analysis);

            return analysis;
        }

        public static double[,] ToGrayMatrix(Image<Rgba32> img, CancellationToken cancellationToken = default)
        {
            int w = img.Width;
            int h = img.Height;
            double[,] gray = new double[h, w];

            img.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < h; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Span<Rgba32> row = accessor.GetRowSpan(y);
                    for (int x = 0; x < w; x++)
                    {
                        var p = row[x];
                        gray[y, x] = 0.299 * p.R + 0.587 * p.G + 0.114 * p.B;
                    }
                }
            });

            return gray;
        }

        public static double Mean(double[,] m)
        {
            int h = m.GetLength(0), w = m.GetLength(1);
            double s = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    s += m[y, x];
            return s / (h * w);
        }

        public static double StdDev(double[,] m, double mean)
        {
            int h = m.GetLength(0), w = m.GetLength(1);
            double s = 0;
            int n = h * w;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double d = m[y, x] - mean;
                    s += d * d;
                }
            return Math.Sqrt(s / Math.Max(1, n));
        }

        public static double Entropy(double[,] m)
        {
            int[] hist = new int[256];
            int h = m.GetLength(0), w = m.GetLength(1), n = h * w;

            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int g = (int)Math.Clamp(Math.Round(m[y, x]), 0, 255);
                    hist[g]++;
                }

            double ent = 0;
            for (int i = 0; i < 256; i++)
            {
                if (hist[i] == 0) continue;
                double p = hist[i] / (double)n;
                ent -= p * Math.Log(p, 2);
            }
            return ent;
        }

        public static double EdgeDensity(double[,] m)
        {
            int h = m.GetLength(0), w = m.GetLength(1);
            if (h < 3 || w < 3) return 0;

            double edgeCount = 0;
            double total = (h - 2) * (w - 2);

            int[,] gxKernel = {
                { -1, 0, 1 },
                { -2, 0, 2 },
                { -1, 0, 1 }
            };
            int[,] gyKernel = {
                { -1,-2,-1 },
                {  0, 0, 0 },
                {  1, 2, 1 }
            };

            for (int y = 1; y < h - 1; y++)
            {
                for (int x = 1; x < w - 1; x++)
                {
                    double gx = 0, gy = 0;
                    for (int ky = -1; ky <= 1; ky++)
                        for (int kx = -1; kx <= 1; kx++)
                        {
                            double p = m[y + ky, x + kx];
                            gx += p * gxKernel[ky + 1, kx + 1];
                            gy += p * gyKernel[ky + 1, kx + 1];
                        }

                    double mag = Math.Sqrt(gx * gx + gy * gy);
                    if (mag > 80) edgeCount++;
                }
            }

            return edgeCount / Math.Max(1.0, total);
        }

        public static double ProjectionVariance(double[,] m, bool horizontal)
        {
            int h = m.GetLength(0), w = m.GetLength(1);
            if (horizontal)
            {
                double[] arr = new double[h];
                for (int y = 0; y < h; y++)
                {
                    double s = 0;
                    for (int x = 0; x < w; x++) s += m[y, x];
                    arr[y] = s / w;
                }
                return Variance(arr);
            }
            else
            {
                double[] arr = new double[w];
                for (int x = 0; x < w; x++)
                {
                    double s = 0;
                    for (int y = 0; y < h; y++) s += m[y, x];
                    arr[x] = s / h;
                }
                return Variance(arr);
            }
        }

        public static double ForegroundRatio(double[,] m)
        {
            // Otsu simplificado / umbral medio adaptativo híbrido
            int h = m.GetLength(0), w = m.GetLength(1);
            double mean = Mean(m);
            double std = StdDev(m, mean);
            double t = mean - std * 0.2;

            int fg = 0;
            int total = h * w;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (m[y, x] < t) fg++;

            return fg / (double)Math.Max(1, total);
        }

        public static double RidgeLikeScore(double[,] m)
        {
            // Aproximación barata para huellas / líneas repetitivas:
            // medimos alternancia local horizontal y vertical.
            int h = m.GetLength(0), w = m.GetLength(1);
            if (h < 5 || w < 5) return 0;

            double score = 0;
            int count = 0;

            for (int y = 2; y < h - 2; y++)
            {
                for (int x = 2; x < w - 2; x++)
                {
                    double c = m[y, x];
                    double l = m[y, x - 1];
                    double r = m[y, x + 1];
                    double u = m[y - 1, x];
                    double d = m[y + 1, x];

                    double altH = Math.Abs((l - c) * (r - c));
                    double altV = Math.Abs((u - c) * (d - c));
                    score += (altH + altV);
                    count++;
                }
            }

            return score / Math.Max(1, count) / 255.0 / 255.0;
        }

        public static ImageContentKind Classify(double[,] gray, ImageAnalysis a)
        {
            // Heurística explícita, sin humo.
            bool lowEntropy = a.Entropy < 5.2;
            bool highEdge = a.EdgeDensity > 0.18;
            bool textLike = a.HorizontalProjectionVariance > 150 && a.VerticalProjectionVariance > 120 && a.ForegroundRatio > 0.02 && a.ForegroundRatio < 0.45;
            bool uiLike = lowEntropy && highEdge && a.StdLuma > 35;
            bool fingerprintLike = a.RidgeLikeScore > 0.07 && a.EdgeDensity > 0.10 && a.ForegroundRatio > 0.08 && a.ForegroundRatio < 0.60;
            bool photoLike = a.Entropy >= 5.8 && a.EdgeDensity < 0.25;

            if (fingerprintLike) return ImageContentKind.Fingerprint;
            if (textLike) return ImageContentKind.Document;
            if (uiLike) return ImageContentKind.Ui;
            if (photoLike) return ImageContentKind.Photo;
            if (a.Entropy < 4.6 && a.EdgeDensity < 0.12) return ImageContentKind.Graphic;
            return ImageContentKind.Unknown;
        }

        public static double Variance(double[] x)
        {
            if (x.Length == 0) return 0;
            double mean = x.Average();
            double s = 0;
            for (int i = 0; i < x.Length; i++)
            {
                double d = x[i] - mean;
                s += d * d;
            }
            return s / x.Length;
        }
    }

    // =========================================================
    // 5) MOTOR DE DECISIÓN SUCA
    // =========================================================
    public static class SucaDecisionEngine
    {
        public static SucaDecision Decide(SucaProfile profile, ImageAnalysis analysis, SucaOptions options)
        {
            profile.Validate();

            int hash = profile.Hash();
            int exHash = profile.ExtendedHash();

            double scale = SucaMath.FactorEscalaBase(profile.A, profile.B, profile.C, profile.D, analysis.InputKb);
            int quality = SucaMath.CalidadBase(hash, analysis.InputKb);

            // Protección de imágenes pequeñas
            if (options.PreserveSmallImages)
            {
                long px = analysis.PixelCount;
                if (px <= 300_000) scale = Math.Max(scale, 0.72);
                else if (px <= 800_000) scale = Math.Max(scale, 0.58);
                else if (px <= 1_500_000) scale = Math.Max(scale, 0.46);
            }

            // Ajuste por contenido
            switch (analysis.Kind)
            {
                case ImageContentKind.Document:
                    scale = Math.Max(scale, 0.72);
                    quality = Math.Max(quality, 78);
                    break;

                case ImageContentKind.Ui:
                    scale = Math.Max(scale, 0.78);
                    quality = Math.Max(quality, 82);
                    break;

                case ImageContentKind.Fingerprint:
                    // Para huella: preservar crestas, no castigar demasiado.
                    scale = Math.Max(scale, 0.84);
                    quality = Math.Max(quality, 88);
                    break;

                case ImageContentKind.Graphic:
                    scale = Math.Max(scale, 0.85);
                    quality = Math.Max(quality, 84);
                    break;

                case ImageContentKind.Photo:
                    // Foto sí aguanta más compresión.
                    quality = Math.Min(quality, 90);
                    break;
            }

            // No dejar dimensiones absurdas
            int newW = Math.Max(options.MinWidth, (int)Math.Round(analysis.Width * scale));
            int newH = Math.Max(options.MinHeight, (int)Math.Round(analysis.Height * scale));

            if (newW > analysis.Width) newW = analysis.Width;
            if (newH > analysis.Height) newH = analysis.Height;

            // Selección de formato
            SucaOutputFormat format = options.PreferredFormat;
            bool nearLossless = false;
            int nearLosslessQ = 60;

            if (!options.AllowLossy)
            {
                format = SucaOutputFormat.Png;
                quality = 100;
            }
            else
            {
                switch (analysis.Kind)
                {
                    case ImageContentKind.Document:
                        format = SucaOutputFormat.Webp;
                        nearLossless = true;
                        nearLosslessQ = 70;
                        break;

                    case ImageContentKind.Ui:
                        format = options.UseNearLosslessForUi ? SucaOutputFormat.Webp : SucaOutputFormat.Png;
                        nearLossless = options.UseNearLosslessForUi;
                        nearLosslessQ = 75;
                        break;

                    case ImageContentKind.Fingerprint:
                        // Para biometría experimental:
                        // si el objetivo es integridad máxima, PNG.
                        // si peso: WebP con alta calidad.
                        format = options.PesoMoscaKbTarget <= 120 ? SucaOutputFormat.Webp : SucaOutputFormat.Png;
                        nearLossless = format == SucaOutputFormat.Webp;
                        nearLosslessQ = 80;
                        quality = Math.Max(quality, 92);
                        break;

                    case ImageContentKind.Photo:
                        format = SucaOutputFormat.Webp;
                        break;

                    default:
                        format = options.PreferredFormat;
                        break;
                }
            }

            return new SucaDecision
            {
                Hash = hash,
                ExtendedHash = exHash,
                Scale = scale,
                Quality = quality,
                OutputFormat = format,
                UseNearLosslessWebp = nearLossless,
                NearLosslessQuality = nearLosslessQ,
                RemoveMetadata = options.RemoveMetadata,
                ContentKind = analysis.Kind,
                NewWidth = newW,
                NewHeight = newH
            };
        }
    }

    // =========================================================
    // 6) VALIDACIÓN DE INTEGRIDAD
    // =========================================================
    public static class IntegrityValidator
    {
        public static IntegrityReport Compare(string originalPath, string compressedPath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var report = new IntegrityReport
            {
                OriginalSha256 = CryptoUtil.Sha256File(originalPath),
                CompressedSha256 = CryptoUtil.Sha256File(compressedPath)
            };

            report.BitwiseEqual = string.Equals(report.OriginalSha256, report.CompressedSha256, StringComparison.OrdinalIgnoreCase);

            using var a = Image.Load<Rgba32>(originalPath);
            using var b = Image.Load<Rgba32>(compressedPath);

            a.Mutate(x => x.AutoOrient());
            b.Mutate(x => x.AutoOrient());

            // Igualar tamaño para comparar perceptualmente
            int w = Math.Min(a.Width, b.Width);
            int h = Math.Min(a.Height, b.Height);

            using var a2 = a.Clone(ctx => ctx.Resize(w, h));
            using var b2 = b.Clone(ctx => ctx.Resize(w, h));

            double[,] ga = ImageAnalyzer.ToGrayMatrix(a2, cancellationToken);
            double[,] gb = ImageAnalyzer.ToGrayMatrix(b2, cancellationToken);

            report.Mode = report.BitwiseEqual ? "lossless-strong" : "perceptual";
            report.Mse = Mse(ga, gb);
            report.Mae = Mae(ga, gb);
            report.Psnr = Psnr(report.Mse);
            report.SsimLite = SsimLite(ga, gb);
            report.HistogramSimilarity = HistogramSimilarity(ga, gb);
            report.EdgeRetention = EdgeRetention(ga, gb);

            // Criterio técnico configurable:
            // huella / documento deberían ser más estrictos si se usan para lectura.
            report.IsStructurallyAcceptable =
                report.SsimLite >= 0.88 &&
                report.EdgeRetention >= 0.80 &&
                report.HistogramSimilarity >= 0.84;

            report.Verdict = report.BitwiseEqual
                ? "SIN CAMBIO BIT A BIT"
                : (report.IsStructurallyAcceptable ? "ACEPTABLE ESTRUCTURALMENTE" : "RIESGO DE PÉRDIDA RELEVANTE");

            return report;
        }

        public static double Mse(double[,] a, double[,] b)
        {
            int h = a.GetLength(0), w = a.GetLength(1);
            double s = 0;
            int n = h * w;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double d = a[y, x] - b[y, x];
                    s += d * d;
                }
            return s / Math.Max(1, n);
        }

        public static double Mae(double[,] a, double[,] b)
        {
            int h = a.GetLength(0), w = a.GetLength(1);
            double s = 0;
            int n = h * w;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    s += Math.Abs(a[y, x] - b[y, x]);
            return s / Math.Max(1, n);
        }

        public static double Psnr(double mse)
        {
            if (mse <= 1e-12) return 99.0;
            return 10.0 * Math.Log10((255.0 * 255.0) / mse);
        }

        public static double SsimLite(double[,] a, double[,] b)
        {
            // SSIM global simplificado
            double meanA = ImageAnalyzer.Mean(a);
            double meanB = ImageAnalyzer.Mean(b);
            double varA = Variance(a, meanA);
            double varB = Variance(b, meanB);
            double cov = Covariance(a, b, meanA, meanB);

            double c1 = Math.Pow(0.01 * 255, 2);
            double c2 = Math.Pow(0.03 * 255, 2);

            double num = (2 * meanA * meanB + c1) * (2 * cov + c2);
            double den = (meanA * meanA + meanB * meanB + c1) * (varA + varB + c2);

            if (Math.Abs(den) < 1e-12) return 0;
            return num / den;
        }

        public static double HistogramSimilarity(double[,] a, double[,] b)
        {
            int[] ha = Hist(a), hb = Hist(b);
            double inter = 0, total = 0;
            for (int i = 0; i < 256; i++)
            {
                inter += Math.Min(ha[i], hb[i]);
                total += ha[i];
            }
            return total == 0 ? 0 : inter / total;
        }

        public static double EdgeRetention(double[,] a, double[,] b)
        {
            double ea = ImageAnalyzer.EdgeDensity(a);
            double eb = ImageAnalyzer.EdgeDensity(b);
            if (ea <= 1e-9) return 1.0;
            return Math.Min(eb / ea, 1.5);
        }

        private static int[] Hist(double[,] m)
        {
            int[] h = new int[256];
            int hh = m.GetLength(0), ww = m.GetLength(1);
            for (int y = 0; y < hh; y++)
                for (int x = 0; x < ww; x++)
                    h[(int)Math.Clamp(Math.Round(m[y, x]), 0, 255)]++;
            return h;
        }

        private static double Variance(double[,] m, double mean)
        {
            int h = m.GetLength(0), w = m.GetLength(1), n = h * w;
            double s = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double d = m[y, x] - mean;
                    s += d * d;
                }
            return s / Math.Max(1, n);
        }

        private static double Covariance(double[,] a, double[,] b, double ma, double mb)
        {
            int h = a.GetLength(0), w = a.GetLength(1), n = h * w;
            double s = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    s += (a[y, x] - ma) * (b[y, x] - mb);
            return s / Math.Max(1, n);
        }
    }

    // =========================================================
    // 7) EMBEDDINGS VISUALES / OCR VISUAL
    // =========================================================
    public static class VisualEmbeddingEngine
    {
        // Embedding artesanal:
        // - histogramas grises
        // - densidad por celdas
        // - proyecciones
        // - borde
        // - entropía
        // - ridge/fingerprint score
        public static float[] BuildImageEmbedding(string path, CancellationToken cancellationToken = default)
        {
            double[,] gray = ImageSignal.LoadGrayMatrix(path, maxSide: 64, cancellationToken);
            var v = BuildImageEmbedding(gray);

            return v;
        }

        public static float[] BuildImageEmbedding(double[,] gray)
        {
            var list = new List<float>(128);

            int h = gray.GetLength(0);
            int w = gray.GetLength(1);

            // Histograma 16 bins
            int bins = 16;
            double[] hist = new double[bins];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int g = (int)Math.Clamp(gray[y, x], 0, 255);
                    hist[Math.Min(bins - 1, g / 16)]++;
                }

            double total = h * w;
            for (int i = 0; i < bins; i++)
                list.Add((float)(hist[i] / total));

            // Grid 4x4 intensidades medias
            for (int gy = 0; gy < 4; gy++)
            {
                for (int gx = 0; gx < 4; gx++)
                {
                    int y0 = gy * h / 4, y1 = (gy + 1) * h / 4;
                    int x0 = gx * w / 4, x1 = (gx + 1) * w / 4;
                    double s = 0; int c = 0;
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++)
                        {
                            s += gray[y, x];
                            c++;
                        }
                    list.Add((float)((s / Math.Max(1, c)) / 255.0));
                }
            }

            // Proyecciones H/V resumidas a 8 bins
            list.AddRange(ProjectionEmbedding(gray, true, 8));
            list.AddRange(ProjectionEmbedding(gray, false, 8));

            // Features globales
            double mean = ImageSignal.Mean(gray);
            double std = ImageSignal.StdDev(gray, mean);
            list.Add((float)(mean / 255.0));
            list.Add((float)(std / 255.0));
            list.Add((float)(ImageSignal.Entropy(gray) / 8.0));
            list.Add((float)ImageSignal.EdgeDensity(gray));
            list.Add((float)ImageSignal.ForegroundRatio(gray));
            list.Add((float)ImageSignal.RidgeLikeScore(gray));

            NormalizeL2(list);
            return list.ToArray();
        }

        private static IEnumerable<float> ProjectionEmbedding(double[,] gray, bool horizontal, int bins)
        {
            int h = gray.GetLength(0), w = gray.GetLength(1);
            double[] src;

            if (horizontal)
            {
                src = new double[h];
                for (int y = 0; y < h; y++)
                {
                    double s = 0;
                    for (int x = 0; x < w; x++) s += gray[y, x];
                    src[y] = s / w / 255.0;
                }
            }
            else
            {
                src = new double[w];
                for (int x = 0; x < w; x++)
                {
                    double s = 0;
                    for (int y = 0; y < h; y++) s += gray[y, x];
                    src[x] = s / h / 255.0;
                }
            }

            float[] dst = new float[bins];
            for (int i = 0; i < bins; i++)
            {
                int p0 = i * src.Length / bins;
                int p1 = (i + 1) * src.Length / bins;
                double sum = 0;
                int count = 0;
                for (int j = p0; j < p1; j++) { sum += src[j]; count++; }
                dst[i] = (float)(sum / Math.Max(1, count));
            }
            return dst;
        }

        private static void NormalizeL2(List<float> v)
        {
            double s = 0;
            for (int i = 0; i < v.Count; i++) s += v[i] * v[i];
            double n = Math.Sqrt(s);
            if (n <= 1e-12) return;
            for (int i = 0; i < v.Count; i++) v[i] = (float)(v[i] / n);
        }
    }

    public sealed class VisualOcrFeatures
    {
        public int EstimatedTextLines { get; set; }
        public int EstimatedColumns { get; set; }
        public double InkDensity { get; set; }
        public double CharacterTransitionScore { get; set; }
        public double ReadabilityScore { get; set; }
        public override string ToString() =>
            $"Lines={EstimatedTextLines}, Cols={EstimatedColumns}, Ink={InkDensity:F4}, Transition={CharacterTransitionScore:F4}, Readability={ReadabilityScore:F4}";
    }

    public static class VisualOcrEngine
    {
        // No promete OCR semántico real.
        // Sí aporta features útiles para evaluar legibilidad de documento.
        public static VisualOcrFeatures AnalyzeReadability(string path, CancellationToken cancellationToken = default)
        {
            double[,] gray = ImageSignal.LoadGrayMatrix(path, maxSide: 1200, cancellationToken);
            return AnalyzeReadability(gray);
        }

        public static VisualOcrFeatures AnalyzeReadability(double[,] gray)
        {
            int h = gray.GetLength(0), w = gray.GetLength(1);
            double mean = ImageSignal.Mean(gray);
            double std = ImageSignal.StdDev(gray, mean);
            double t = mean - std * 0.18;

            bool[,] bin = new bool[h, w];
            int ink = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    bool fg = gray[y, x] < t;
                    bin[y, x] = fg;
                    if (fg) ink++;
                }

            double inkDensity = ink / (double)Math.Max(1, h * w);

            int[] hProj = new int[h];
            int[] vProj = new int[w];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (bin[y, x])
                    {
                        hProj[y]++;
                        vProj[x]++;
                    }

            int lines = CountPeaks(hProj, Math.Max(2, w / 60));
            int cols = CountPeaks(vProj, Math.Max(2, h / 60));
            double trans = TransitionScore(bin);
            double readability =
                (Math.Min(1.0, inkDensity / 0.18) * 0.25) +
                (Math.Min(1.0, trans / 0.35) * 0.45) +
                (Math.Min(1.0, lines / 25.0) * 0.30);

            return new VisualOcrFeatures
            {
                EstimatedTextLines = lines,
                EstimatedColumns = cols,
                InkDensity = inkDensity,
                CharacterTransitionScore = trans,
                ReadabilityScore = readability
            };
        }

        private static int CountPeaks(int[] x, int threshold)
        {
            int peaks = 0;
            bool inside = false;
            for (int i = 0; i < x.Length; i++)
            {
                if (x[i] > threshold && !inside)
                {
                    inside = true;
                    peaks++;
                }
                else if (x[i] <= threshold && inside)
                {
                    inside = false;
                }
            }
            return peaks;
        }

        private static double TransitionScore(bool[,] bin)
        {
            int h = bin.GetLength(0), w = bin.GetLength(1);
            double transitions = 0;
            double total = 0;

            for (int y = 0; y < h; y++)
            {
                for (int x = 1; x < w; x++)
                {
                    total++;
                    if (bin[y, x] != bin[y, x - 1]) transitions++;
                }
            }

            return total == 0 ? 0 : transitions / total;
        }
    }

    // =========================================================
    // 8) DATASET BUILDER
    // =========================================================
    public static class DatasetBuilder
    {
        public static DatasetRecord FromResult(CompressionResult r, float[]? embedding = null, string label = "")
        {
            return new DatasetRecord
            {
                InputPath = r.InputPath,
                OutputPath = r.OutputPath,
                Label = label,
                Profile = $"{r.Decision.Hash}:{r.Analysis.Kind}:{r.Decision.ContentKind}",
                ContentKind = r.Analysis.Kind.ToString(),
                Width = r.Analysis.Width,
                Height = r.Analysis.Height,
                InputKb = r.Analysis.InputKb,
                OutputKb = r.OutputKb,
                SavingPercent = r.SavingPercent,
                Psnr = r.Integrity?.Psnr ?? 0,
                SsimLite = r.Integrity?.SsimLite ?? 0,
                EdgeRetention = r.Integrity?.EdgeRetention ?? 0,
                Embedding = embedding ?? Array.Empty<float>(),
                Metrics = new Dictionary<string, double>
                {
                    ["entropy"] = r.Analysis.Entropy,
                    ["edgeDensity"] = r.Analysis.EdgeDensity,
                    ["ridgeLikeScore"] = r.Analysis.RidgeLikeScore,
                    ["foregroundRatio"] = r.Analysis.ForegroundRatio,
                    ["scale"] = r.Decision.Scale,
                    ["quality"] = r.Decision.Quality
                }
            };
        }

        public static void AppendJsonl(string datasetPath, DatasetRecord record)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(datasetPath)!);
            string line = JsonSerializer.Serialize(record);
            File.AppendAllText(datasetPath, line + Environment.NewLine, Encoding.UTF8);
        }
    }

    // =========================================================
    // 9) RAG LOCAL SIMPLE
    // =========================================================
    public sealed class LocalRagIndex
    {
        public List<RagItem> Items { get; } = new();

        public void Add(RagItem item) => Items.Add(item);

        public List<(RagItem Item, double Score)> Search(float[] queryEmbedding, int topK = 5)
        {
            return Items
                .Select(i => (Item: i, Score: VectorMath.CosineSimilarity(queryEmbedding, i.Embedding)))
                .OrderByDescending(x => x.Score)
                .Take(topK)
                .ToList();
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(Items, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json, Encoding.UTF8);
        }

        public static LocalRagIndex Load(string path)
        {
            var idx = new LocalRagIndex();
            if (!File.Exists(path)) return idx;

            var items = JsonSerializer.Deserialize<List<RagItem>>(File.ReadAllText(path, Encoding.UTF8))
                        ?? new List<RagItem>();
            idx.Items.AddRange(items);
            return idx;
        }
    }

    public static class RagTextProjector
    {
        // Convierte análisis + integridad en texto consultable por RAG
        public static string BuildImageKnowledgeText(CompressionResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Archivo: {Path.GetFileName(result.InputPath)}");
            sb.AppendLine($"TipoDetectado: {result.Analysis.Kind}");
            sb.AppendLine($"Resolucion: {result.Analysis.Width}x{result.Analysis.Height}");
            sb.AppendLine($"TamanoOriginalKB: {result.Analysis.InputKb:F2}");
            sb.AppendLine($"TamanoFinalKB: {result.OutputKb:F2}");
            sb.AppendLine($"Ahorro: {result.SavingPercent:F2}%");
            sb.AppendLine($"EscalaSUCA: {result.Decision.Scale:F4}");
            sb.AppendLine($"CalidadSUCA: {result.Decision.Quality}");
            sb.AppendLine($"Formato: {result.Decision.OutputFormat}");
            if (result.Integrity is not null)
            {
                sb.AppendLine($"PSNR: {result.Integrity.Psnr:F3}");
                sb.AppendLine($"SSIM_Lite: {result.Integrity.SsimLite:F4}");
                sb.AppendLine($"EdgeRetention: {result.Integrity.EdgeRetention:F4}");
                sb.AppendLine($"Verdict: {result.Integrity.Verdict}");
            }
            return sb.ToString();
        }
    }

    // =========================================================
    // 10) TINY MLP EXPERIMENTAL
    // =========================================================
    public sealed class TinyMlp
    {
        private readonly int _input;
        private readonly int _hidden;
        private readonly int _output;
        private readonly Random _rng;

        private readonly double[,] _w1;
        private readonly double[] _b1;
        private readonly double[,] _w2;
        private readonly double[] _b2;

        public TinyMlp(int input, int hidden, int output, int seed = 42)
        {
            _input = input;
            _hidden = hidden;
            _output = output;
            _rng = new Random(seed);

            _w1 = new double[input, hidden];
            _b1 = new double[hidden];
            _w2 = new double[hidden, output];
            _b2 = new double[output];

            Init(_w1);
            Init(_w2);
        }

        private void Init(double[,] w)
        {
            int a = w.GetLength(0), b = w.GetLength(1);
            for (int i = 0; i < a; i++)
                for (int j = 0; j < b; j++)
                    w[i, j] = (_rng.NextDouble() * 2 - 1) * 0.05;
        }

        public float[] Predict(float[] x)
        {
            if (x.Length != _input) throw new ArgumentException("Dimensión inválida.");

            double[] h = new double[_hidden];
            for (int j = 0; j < _hidden; j++)
            {
                double s = _b1[j];
                for (int i = 0; i < _input; i++) s += x[i] * _w1[i, j];
                h[j] = Math.Max(0, s); // ReLU
            }

            double[] o = new double[_output];
            for (int k = 0; k < _output; k++)
            {
                double s = _b2[k];
                for (int j = 0; j < _hidden; j++) s += h[j] * _w2[j, k];
                o[k] = s;
            }

            // softmax
            double max = o.Max();
            double sum = 0;
            for (int k = 0; k < _output; k++)
            {
                o[k] = Math.Exp(o[k] - max);
                sum += o[k];
            }

            float[] probs = new float[_output];
            for (int k = 0; k < _output; k++)
                probs[k] = (float)(o[k] / Math.Max(1e-12, sum));

            return probs;
        }

        public void Train(List<(float[] X, int Y)> data, int epochs = 50, double lr = 0.01)
        {
            if (data.Count == 0) return;

            for (int epoch = 0; epoch < epochs; epoch++)
            {
                foreach (var sample in data)
                {
                    float[] x = sample.X;
                    int y = sample.Y;

                    // forward
                    double[] z1 = new double[_hidden];
                    double[] h1 = new double[_hidden];
                    for (int j = 0; j < _hidden; j++)
                    {
                        double s = _b1[j];
                        for (int i = 0; i < _input; i++) s += x[i] * _w1[i, j];
                        z1[j] = s;
                        h1[j] = Math.Max(0, s);
                    }

                    double[] z2 = new double[_output];
                    for (int k = 0; k < _output; k++)
                    {
                        double s = _b2[k];
                        for (int j = 0; j < _hidden; j++) s += h1[j] * _w2[j, k];
                        z2[k] = s;
                    }

                    double max = z2.Max();
                    double sum = 0;
                    for (int k = 0; k < _output; k++)
                    {
                        z2[k] = Math.Exp(z2[k] - max);
                        sum += z2[k];
                    }

                    double[] p = new double[_output];
                    for (int k = 0; k < _output; k++) p[k] = z2[k] / Math.Max(1e-12, sum);

                    // backward cross-entropy + softmax
                    double[] dz2 = new double[_output];
                    for (int k = 0; k < _output; k++) dz2[k] = p[k] - (k == y ? 1.0 : 0.0);

                    double[,] dw2 = new double[_hidden, _output];
                    double[] db2 = new double[_output];
                    for (int j = 0; j < _hidden; j++)
                        for (int k = 0; k < _output; k++)
                            dw2[j, k] = h1[j] * dz2[k];
                    for (int k = 0; k < _output; k++) db2[k] = dz2[k];

                    double[] dh1 = new double[_hidden];
                    for (int j = 0; j < _hidden; j++)
                    {
                        double s = 0;
                        for (int k = 0; k < _output; k++) s += _w2[j, k] * dz2[k];
                        dh1[j] = s;
                    }

                    double[] dz1 = new double[_hidden];
                    for (int j = 0; j < _hidden; j++) dz1[j] = z1[j] > 0 ? dh1[j] : 0;

                    double[,] dw1 = new double[_input, _hidden];
                    double[] db1 = new double[_hidden];
                    for (int i = 0; i < _input; i++)
                        for (int j = 0; j < _hidden; j++)
                            dw1[i, j] = x[i] * dz1[j];
                    for (int j = 0; j < _hidden; j++) db1[j] = dz1[j];

                    // update
                    for (int i = 0; i < _input; i++)
                        for (int j = 0; j < _hidden; j++)
                            _w1[i, j] -= lr * dw1[i, j];
                    for (int j = 0; j < _hidden; j++)
                        _b1[j] -= lr * db1[j];

                    for (int j = 0; j < _hidden; j++)
                        for (int k = 0; k < _output; k++)
                            _w2[j, k] -= lr * dw2[j, k];
                    for (int k = 0; k < _output; k++)
                        _b2[k] -= lr * db2[k];
                }
            }
        }
    }

    // =========================================================
    // 11) PESOS SUCA / MÓDULO "NEURAL"
    // =========================================================
    public sealed class SucaNeuralWeightMap
    {
        public int CellsX { get; init; } = 8;
        public int CellsY { get; init; } = 8;
        public double[,] Weights { get; init; } = new double[8, 8];

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"WeightMap {CellsX}x{CellsY}");
            for (int y = 0; y < CellsY; y++)
            {
                for (int x = 0; x < CellsX; x++)
                    sb.Append($"{Weights[y, x]:0.00} ");
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }

    public static class SucaNeuralMapEngine
    {
        // No es una red neuronal real.
        // Es un mapa de "pesos de preservación" guiado por rasgos visuales.
        // Te permite comprobar si zonas de alto borde / cresta requieren más cuidado.
        public static SucaNeuralWeightMap BuildWeightMap(string path, int profileSeed, CancellationToken cancellationToken = default)
        {
            double[,] gray = ImageSignal.LoadGrayMatrix(path, cancellationToken: cancellationToken);
            return BuildWeightMap(gray, profileSeed);
        }

        public static SucaNeuralWeightMap BuildWeightMap(double[,] gray, int profileSeed)
        {
            int h = gray.GetLength(0), w = gray.GetLength(1);
            int cellsX = 8, cellsY = 8;
            double[,] weights = new double[cellsY, cellsX];

            int normalizedSeed = profileSeed == int.MinValue ? int.MaxValue : Math.Abs(profileSeed);
            double bias = ((normalizedSeed % 17) / 16.0) * 0.15;

            for (int cy = 0; cy < cellsY; cy++)
            {
                for (int cx = 0; cx < cellsX; cx++)
                {
                    int y0 = cy * h / cellsY;
                    int y1 = (cy + 1) * h / cellsY;
                    int x0 = cx * w / cellsX;
                    int x1 = (cx + 1) * w / cellsX;

                    double sum = 0, sumSq = 0;
                    double edge = 0;
                    int n = 0;

                    for (int y = y0; y < y1; y++)
                    {
                        for (int x = x0; x < x1; x++)
                        {
                            double v = gray[y, x];
                            sum += v;
                            sumSq += v * v;
                            n++;

                            if (x > x0 && y > y0)
                            {
                                edge += Math.Abs(gray[y, x] - gray[y, x - 1]);
                                edge += Math.Abs(gray[y, x] - gray[y - 1, x]);
                            }
                        }
                    }

                    double mean = sum / Math.Max(1, n);
                    double var = sumSq / Math.Max(1, n) - mean * mean;
                    double detail = edge / Math.Max(1, n) / 255.0;
                    double texture = Math.Sqrt(Math.Max(0, var)) / 255.0;

                    // "Peso" de preservación: más detalle => más peso.
                    double preserveWeight =
                        0.45 * detail +
                        0.35 * texture +
                        0.20 * (1.0 - Math.Abs(mean / 255.0 - 0.5));

                    preserveWeight = Math.Clamp(preserveWeight + bias, 0, 1);
                    weights[cy, cx] = preserveWeight;
                }
            }

            return new SucaNeuralWeightMap
            {
                CellsX = cellsX,
                CellsY = cellsY,
                Weights = weights
            };
        }
    }

    // =========================================================
    // 12) SUCA COMPRESSOR REFACTORIZADO
    // =========================================================
    public sealed class SucaCompressor : ISucaCompressionKernel
    {
        public CompressionResult Process(string inputPath, string outputDirectory, SucaProfile profile, SucaOptions options, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(inputPath)) throw new ArgumentException("inputPath vacío");
            if (!File.Exists(inputPath)) throw new FileNotFoundException("No existe la imagen de entrada.", inputPath);

            profile.Validate();
            Directory.CreateDirectory(outputDirectory);

            var totalSw = Stopwatch.StartNew();

            var swAnalyze = Stopwatch.StartNew();
            var analysis = ImageAnalyzer.Analyze(inputPath, cancellationToken);
            swAnalyze.Stop();

            var decision = SucaDecisionEngine.Decide(profile, analysis, options);

            var result = new CompressionResult
            {
                InputPath = inputPath,
                Analysis = analysis,
                Decision = decision
            };

            string extension = decision.OutputFormat switch
            {
                SucaOutputFormat.Webp => ".webp",
                SucaOutputFormat.Png => ".png",
                _ => ".jpg"
            };

            string outputPath = FileNameUtil.BuildUniqueOutputPath(inputPath, outputDirectory, "suca", extension, decision.Hash);
            result.OutputPath = outputPath;

            var swLoad = Stopwatch.StartNew();
            using var image = Image.Load<Rgba32>(inputPath);
            image.Mutate(x => x.AutoOrient());
            swLoad.Stop();

            // Limpieza de metadatos
            if (decision.RemoveMetadata)
            {
                image.Metadata.ExifProfile = null;
                image.Metadata.IccProfile = null;
                image.Metadata.IptcProfile = null;
                image.Metadata.XmpProfile = null;
            }

            // Resize sólo si realmente cambia
            var swResize = Stopwatch.StartNew();
            if (decision.NewWidth != image.Width || decision.NewHeight != image.Height)
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Sampler = KnownResamplers.MitchellNetravali,
                    Size = new Size(decision.NewWidth, decision.NewHeight)
                }));
            }
            swResize.Stop();

            // Guardado inicial + refinamiento peso mosca
            var swSave = Stopwatch.StartNew();
            SaveWithOptionalPesoMosca(image, outputPath, decision, options, cancellationToken);
            swSave.Stop();

            var finfo = new FileInfo(outputPath);
            result.OutputBytes = finfo.Length;
            result.OutputKb = finfo.Length / 1024.0;
            result.OutputPixels = (long)decision.NewWidth * decision.NewHeight;
            result.SavingPercent = analysis.InputKb <= 0 ? 0 : 100.0 - ((result.OutputKb / analysis.InputKb) * 100.0);
            result.PixelReductionPercent = analysis.PixelCount <= 0 ? 0 : 100.0 - (result.OutputPixels / (double)analysis.PixelCount) * 100.0;

            result.LoadMs = swLoad.ElapsedMilliseconds;
            result.AnalyzeMs = swAnalyze.ElapsedMilliseconds;
            result.ResizeMs = swResize.ElapsedMilliseconds;
            result.SaveMs = swSave.ElapsedMilliseconds;

            if (options.GenerateIntegrityReport)
                result.Integrity = IntegrityValidator.Compare(inputPath, outputPath, cancellationToken);

            totalSw.Stop();
            result.TotalMs = totalSw.ElapsedMilliseconds;
            return result;
        }

        private static void SaveWithOptionalPesoMosca(Image<Rgba32> image, string outputPath, SucaDecision decision, SucaOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SaveByDecision(image, outputPath, decision, cancellationToken);

            if (!options.EnablePesoMosca) return;

            double kb = new FileInfo(outputPath).Length / 1024.0;
            if (kb <= options.PesoMoscaKbTarget) return;

            // Refinamiento: sólo calidad y, si no basta, escala incremental.
            int q = decision.Quality;
            int nlq = decision.NearLosslessQuality;
            int iterations = options.MaxRefineIterations;
            double target = options.PesoMoscaKbTarget;

            using var work = image.Clone();

            for (int i = 0; i < iterations; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool changed = false;

                if (decision.OutputFormat == SucaOutputFormat.Jpeg || decision.OutputFormat == SucaOutputFormat.Webp)
                {
                    if (q > 38)
                    {
                        q = Math.Max(35, q - 5);
                        changed = true;
                    }
                    if (decision.UseNearLosslessWebp && nlq > 50)
                    {
                        nlq = Math.Max(45, nlq - 5);
                        changed = true;
                    }
                }

                SaveByDecision(work, outputPath, new SucaDecision
                {
                    Hash = decision.Hash,
                    ExtendedHash = decision.ExtendedHash,
                    Scale = decision.Scale,
                    Quality = q,
                    OutputFormat = decision.OutputFormat,
                    UseNearLosslessWebp = decision.UseNearLosslessWebp,
                    NearLosslessQuality = nlq,
                    RemoveMetadata = decision.RemoveMetadata,
                    ContentKind = decision.ContentKind,
                    NewWidth = work.Width,
                    NewHeight = work.Height
                }, cancellationToken);

                kb = new FileInfo(outputPath).Length / 1024.0;
                if (kb <= target) break;

                if (!changed)
                {
                    // Si la calidad ya no puede bajar, aplicamos mini-resize.
                    int nw = Math.Max(128, (int)Math.Round(work.Width * 0.94));
                    int nh = Math.Max(128, (int)Math.Round(work.Height * 0.94));
                    if (nw == work.Width || nh == work.Height) break;

                    work.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Mode = ResizeMode.Max,
                        Sampler = KnownResamplers.MitchellNetravali,
                        Size = new Size(nw, nh)
                    }));
                }
            }
        }

        private static void SaveByDecision(Image<Rgba32> image, string outputPath, SucaDecision decision, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (decision.OutputFormat)
            {
                case SucaOutputFormat.Webp:
                    var webp = new WebpEncoder
                    {
                        FileFormat = WebpFileFormatType.Lossy,
                        Quality = decision.Quality,
                        Method = WebpEncodingMethod.BestQuality,
                        NearLossless = decision.UseNearLosslessWebp,
                        NearLosslessQuality = decision.NearLosslessQuality
                    };
                    image.Save(outputPath, webp);
                    break;

                case SucaOutputFormat.Png:
                    var png = new PngEncoder
                    {
                        CompressionLevel = PngCompressionLevel.BestCompression
                    };
                    image.Save(outputPath, png);
                    break;

                default:
                    var jpg = new JpegEncoder
                    {
                        Quality = decision.Quality
                    };
                    image.Save(outputPath, jpg);
                    break;
            }
        }
    }

    // =========================================================
    // 13) RUNNER POR LOTE
    // =========================================================
    public static class BatchRunner
    {
        public static List<CompressionResult> ProcessDirectory(
            string inputDirectory,
            string outputDirectory,
            SucaProfile profile,
            SucaOptions options,
            CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(inputDirectory))
                throw new DirectoryNotFoundException(inputDirectory);

            var files = Directory
                .EnumerateFiles(inputDirectory, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f =>
                {
                    string e = Path.GetExtension(f).ToLowerInvariant();
                    return e is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".gif" or ".tif" or ".tiff";
                })
                .ToList();

            var compressor = new SucaCompressor();
            var results = new List<CompressionResult>();

            foreach (var f in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var r = compressor.Process(f, outputDirectory, profile, options, cancellationToken);
                    results.Add(r);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] {Path.GetFileName(f)} -> {ex.Message}");
                }
            }

            return results;
        }
    }

    // =========================================================
    // 14) REPORTES
    // =========================================================
    public static class ReportPrinter
    {
        public static void Print(CompressionResult r)
        {
            ConsoleEx.Title("REPORTE TÉCNICO SUCA");
            ConsoleEx.KV("Archivo entrada", r.InputPath);
            ConsoleEx.KV("Archivo salida", r.OutputPath);
            ConsoleEx.KV("Tipo detectado", r.Analysis.Kind);
            ConsoleEx.KV("Resolución original", $"{r.Analysis.Width} x {r.Analysis.Height}");
            ConsoleEx.KV("Tamaño original KB", $"{r.Analysis.InputKb:F2}");
            ConsoleEx.KV("Hash SUCA", r.Decision.Hash);
            ConsoleEx.KV("Escala", $"{r.Decision.Scale:F4}");
            ConsoleEx.KV("Calidad", r.Decision.Quality);
            ConsoleEx.KV("Formato salida", r.Decision.OutputFormat);
            ConsoleEx.KV("Nueva resolución", $"{r.Decision.NewWidth} x {r.Decision.NewHeight}");
            ConsoleEx.KV("Tamaño final KB", $"{r.OutputKb:F2}");
            ConsoleEx.KV("Ahorro", $"{r.SavingPercent:F2}%");
            ConsoleEx.KV("Reducción de píxeles", $"{r.PixelReductionPercent:F2}%");

            Console.WriteLine();
            ConsoleEx.KV("Entropy", $"{r.Analysis.Entropy:F4}");
            ConsoleEx.KV("EdgeDensity", $"{r.Analysis.EdgeDensity:F4}");
            ConsoleEx.KV("ForegroundRatio", $"{r.Analysis.ForegroundRatio:F4}");
            ConsoleEx.KV("RidgeLikeScore", $"{r.Analysis.RidgeLikeScore:F4}");

            if (r.Integrity is not null)
            {
                Console.WriteLine();
                ConsoleEx.KV("Integrity.Mode", r.Integrity.Mode);
                ConsoleEx.KV("BitwiseEqual", r.Integrity.BitwiseEqual);
                ConsoleEx.KV("PSNR", $"{r.Integrity.Psnr:F3}");
                ConsoleEx.KV("SSIM-Lite", $"{r.Integrity.SsimLite:F4}");
                ConsoleEx.KV("HistSimilarity", $"{r.Integrity.HistogramSimilarity:F4}");
                ConsoleEx.KV("EdgeRetention", $"{r.Integrity.EdgeRetention:F4}");
                ConsoleEx.KV("Verdict", r.Integrity.Verdict);
            }

            Console.WriteLine();
            ConsoleEx.KV("Tiempo carga", $"{r.LoadMs} ms");
            ConsoleEx.KV("Tiempo análisis", $"{r.AnalyzeMs} ms");
            ConsoleEx.KV("Tiempo resize", $"{r.ResizeMs} ms");
            ConsoleEx.KV("Tiempo guardado", $"{r.SaveMs} ms");
            ConsoleEx.KV("Tiempo total", $"{r.TotalMs} ms");
        }

        public static void PrintOcr(VisualOcrFeatures x)
        {
            ConsoleEx.Title("OCR VISUAL / LEGIBILIDAD");
            ConsoleEx.KV("TextLines", x.EstimatedTextLines);
            ConsoleEx.KV("Columns", x.EstimatedColumns);
            ConsoleEx.KV("InkDensity", $"{x.InkDensity:F4}");
            ConsoleEx.KV("TransitionScore", $"{x.CharacterTransitionScore:F4}");
            ConsoleEx.KV("ReadabilityScore", $"{x.ReadabilityScore:F4}");
        }

        public static void PrintRagResults(List<(RagItem Item, double Score)> items)
        {
            ConsoleEx.Title("RAG RESULTADOS");
            int i = 1;
            foreach (var x in items)
            {
                Console.WriteLine($"[{i}] Score={x.Score:F4} | File={x.Item.SourcePath}");
                Console.WriteLine(x.Item.Text);
                Console.WriteLine(new string('-', 60));
                i++;
            }
        }

        public static void PrintWeightMap(SucaNeuralWeightMap map)
        {
            ConsoleEx.Title("MAPA DE PESOS SUCA");
            Console.WriteLine(map.ToString());
        }
    }

    // =========================================================
    // 15) PROGRAMA / DEMO
    // =========================================================
    internal static class Program
    {
        static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            SucaLaunchSelection selection;
            try
            {
                selection = SucaStartupLauncher.Select(args);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                SucaStartupLauncher.PrintUsage();
                return 2;
            }

            if (selection.Mode == SucaLaunchMode.Help)
            {
                SucaStartupLauncher.PrintUsage();
                return 0;
            }

            if (selection.Mode == SucaLaunchMode.Cancelled)
                return 0;

            if (selection.Mode == SucaLaunchMode.Web)
            {
                await SucaRestHost.RunAsync(selection.WebHostArguments);
                return 0;
            }

            return await RunConsoleDemoAsync(args);
        }

        private static async Task<int> RunConsoleDemoAsync(string[] args)
        {
            string? ruta = ReadOption(args, "--input");
            if (string.IsNullOrWhiteSpace(ruta) && !Console.IsInputRedirected)
            {
                Console.Write("Ruta de imagen de entrada: ");
                ruta = Console.ReadLine();
            }

            if (string.IsNullOrWhiteSpace(ruta))
            {
                Console.Error.WriteLine("El modo consola requiere --input <imagen>.");
                SucaStartupLauncher.PrintUsage();
                return 2;
            }

            ruta = ruta.Trim().Trim('"');
            if (!File.Exists(ruta))
            {
                Console.Error.WriteLine($"No existe la imagen de entrada: {ruta}");
                return 2;
            }

            string? carpetaSalida = ReadOption(args, "--output");
            if (string.IsNullOrWhiteSpace(carpetaSalida))
            {
                string inputDirectory = Path.GetDirectoryName(Path.GetFullPath(ruta)) ?? Environment.CurrentDirectory;
                carpetaSalida = Path.Combine(inputDirectory, "SUCA_OUT");
            }

            carpetaSalida = carpetaSalida.Trim().Trim('"');
            string datasetPath = Path.Combine(carpetaSalida, "dataset", "suca_dataset.jsonl");
            string ragIndexPath = Path.Combine(carpetaSalida, "rag", "suca_rag_index.json");

            var profile = new SucaProfile
            {
                A = 0,
                B = 1,
                C = 1,
                D = 3
            };

            var options = new SucaOptions
            {
                PreferredFormat = SucaOutputFormat.Webp,
                RemoveMetadata = true,
                AllowLossy = true,
                PreserveSmallImages = true,
                EnablePesoMosca = true,
                PesoMoscaKbTarget = 100.0,
                MaxRefineIterations = 10,
                MinWidth = 192,
                MinHeight = 192,
                GenerateIntegrityReport = true
            };

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(ReadTimeout(args));
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cts.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;

            try
            {
                // Registro opcional de cancelación
                using var reg = cts.Token.Register(() => Console.WriteLine("[CANCELADO] Operación abortada por token."));

                // ------------------------------------------------
                // 1. COMPRESIÓN SUCA
                // ------------------------------------------------
                var compressor = new SucaCompressor();
                var result = compressor.Process(ruta, carpetaSalida, profile, options, cts.Token);
                ReportPrinter.Print(result);

                // Tooling is an application concern and is deliberately outside the kernel.
                float[] embedding = VisualEmbeddingEngine.BuildImageEmbedding(result.OutputPath, cts.Token);
                DatasetRecord record = DatasetBuilder.FromResult(result, embedding, result.Analysis.Kind.ToString());
                DatasetBuilder.AppendJsonl(datasetPath, record);

                LocalRagIndex localIndex = LocalRagIndex.Load(ragIndexPath);
                localIndex.Add(new RagItem
                {
                    SourcePath = result.OutputPath,
                    Text = RagTextProjector.BuildImageKnowledgeText(result),
                    Embedding = embedding
                });
                localIndex.Save(ragIndexPath);

                // ------------------------------------------------
                // 2. OCR VISUAL / LEGIBILIDAD
                // ------------------------------------------------
                var ocr = VisualOcrEngine.AnalyzeReadability(result.OutputPath, cts.Token);
                ReportPrinter.PrintOcr(ocr);

                // ------------------------------------------------
                // 3. MAPA DE PESOS SUCA
                // ------------------------------------------------
                var weightMap = SucaNeuralMapEngine.BuildWeightMap(ruta, profile.ExtendedHash(), cts.Token);
                ReportPrinter.PrintWeightMap(weightMap);

                // ------------------------------------------------
                // 4. RAG: consulta por similitud visual
                // ------------------------------------------------
                if (File.Exists(ragIndexPath))
                {
                    var idx = LocalRagIndex.Load(ragIndexPath);
                    var hits = idx.Search(embedding, topK: 3);
                    ReportPrinter.PrintRagResults(hits);
                }

                // ------------------------------------------------
                // 5. Tiny MLP experimental
                //    Objetivo: clasificar el tipo de imagen
                // ------------------------------------------------
                if (embedding.Length > 0)
                {
                    // Dataset mínimo sintético de ejemplo.
                    // Lo correcto es que luego tú lo alimentes con tu JSONL real.
                    var samples = new List<(float[] X, int Y)>
                    {
                        (embedding, LabelToInt(result.Analysis.Kind))
                    };

                    // Añadimos pequeñas variaciones para bootstrap.
                    for (int i = 0; i < 8; i++)
                    {
                        var clone = (float[])embedding.Clone();
                        for (int j = 0; j < clone.Length; j++)
                            clone[j] = (float)Math.Clamp(clone[j] + ((i % 2 == 0 ? 1 : -1) * 0.002), -1, 1);
                        samples.Add((clone, LabelToInt(result.Analysis.Kind)));
                    }

                    var mlp = new TinyMlp(input: embedding.Length, hidden: 24, output: 6);
                    mlp.Train(samples, epochs: 25, lr: 0.02);

                    var probs = mlp.Predict(embedding);
                    ConsoleEx.Title("TINY MLP PREDICT");
                    for (int i = 0; i < probs.Length; i++)
                        Console.WriteLine($"{IntToLabel(i),-12}: {probs[i]:P2}");
                }

                // ------------------------------------------------
                // 6. DEMO ASYNC
                // ------------------------------------------------
                await DemoAsync(result.OutputPath, cts.Token);

                Console.WriteLine();
                Console.WriteLine("Proceso finalizado.");
                return 0;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Operación cancelada.");
                return 130;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR GENERAL: {ex}");
                return 1;
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        }

        private static string? ReadOption(string[] args, string optionName)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.Equals(arg, optionName, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    return args[i + 1];

                if (arg.StartsWith(optionName + "=", StringComparison.OrdinalIgnoreCase))
                    return arg.Substring(optionName.Length + 1);
            }

            return null;
        }

        private static TimeSpan ReadTimeout(string[] args)
        {
            string? value = ReadOption(args, "--timeout-seconds");
            if (int.TryParse(value, out int seconds) && seconds > 0)
                return TimeSpan.FromSeconds(seconds);

            return TimeSpan.FromMinutes(3);
        }

        static async Task DemoAsync(string outputPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Task.Run(() =>
            {
                ConsoleEx.Title("DEMO ASÍNCRONA");
                ConsoleEx.KV("Archivo procesado", outputPath);
                ConsoleEx.KV("Timestamp", DateTime.Now);
            }, cancellationToken);
        }

        static int LabelToInt(ImageContentKind kind) => kind switch
        {
            ImageContentKind.Photo => 0,
            ImageContentKind.Document => 1,
            ImageContentKind.Ui => 2,
            ImageContentKind.Fingerprint => 3,
            ImageContentKind.Graphic => 4,
            _ => 5
        };

        static string IntToLabel(int i) => i switch
        {
            0 => "Photo",
            1 => "Document",
            2 => "UI",
            3 => "Fingerprint",
            4 => "Graphic",
            _ => "Unknown"
        };
    }
}
