using System;
using System.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Image = SixLabors.ImageSharp.Image;

namespace SUCA_Compressor.Shared.Imaging
{
    // Image primitives shared by the compression kernel and optional image tooling.
    public static class ImageSignal
    {
        public static double[,] LoadGrayMatrix(string path, int? maxSide = null, CancellationToken cancellationToken = default)
        {
            using var image = Image.Load<Rgba32>(path);
            image.Mutate(x => x.AutoOrient());

            if (maxSide is > 0 && (image.Width > maxSide.Value || image.Height > maxSide.Value))
            {
                using var resized = image.Clone(x => x.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new SixLabors.ImageSharp.Size(maxSide.Value, maxSide.Value)
                }));
                return ToGrayMatrix(resized, cancellationToken);
            }

            return ToGrayMatrix(image, cancellationToken);
        }

        public static double[,] ToGrayMatrix(Image<Rgba32> image, CancellationToken cancellationToken = default)
        {
            int width = image.Width;
            int height = image.Height;
            var gray = new double[height, width];

            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < height; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Span<Rgba32> row = accessor.GetRowSpan(y);
                    for (int x = 0; x < width; x++)
                    {
                        Rgba32 pixel = row[x];
                        gray[y, x] = 0.299 * pixel.R + 0.587 * pixel.G + 0.114 * pixel.B;
                    }
                }
            });

            return gray;
        }

        public static double Mean(double[,] matrix)
        {
            int height = matrix.GetLength(0);
            int width = matrix.GetLength(1);
            if (height == 0 || width == 0) return 0;

            double sum = 0;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    sum += matrix[y, x];

            return sum / (height * width);
        }

        public static double StdDev(double[,] matrix, double mean)
        {
            int height = matrix.GetLength(0);
            int width = matrix.GetLength(1);
            int count = height * width;
            if (count == 0) return 0;

            double sum = 0;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    double delta = matrix[y, x] - mean;
                    sum += delta * delta;
                }

            return System.Math.Sqrt(sum / count);
        }

        public static double Entropy(double[,] matrix)
        {
            var histogram = new int[256];
            int height = matrix.GetLength(0);
            int width = matrix.GetLength(1);
            int count = height * width;
            if (count == 0) return 0;

            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    histogram[(int)System.Math.Clamp(System.Math.Round(matrix[y, x]), 0, 255)]++;

            double entropy = 0;
            for (int i = 0; i < histogram.Length; i++)
            {
                if (histogram[i] == 0) continue;
                double probability = histogram[i] / (double)count;
                entropy -= probability * System.Math.Log(probability, 2);
            }

            return entropy;
        }

        public static double EdgeDensity(double[,] matrix)
        {
            int height = matrix.GetLength(0);
            int width = matrix.GetLength(1);
            if (height < 3 || width < 3) return 0;

            double edges = 0;
            for (int y = 1; y < height - 1; y++)
            {
                for (int x = 1; x < width - 1; x++)
                {
                    double gx =
                        -matrix[y - 1, x - 1] + matrix[y - 1, x + 1] +
                        -2 * matrix[y, x - 1] + 2 * matrix[y, x + 1] +
                        -matrix[y + 1, x - 1] + matrix[y + 1, x + 1];
                    double gy =
                        -matrix[y - 1, x - 1] - 2 * matrix[y - 1, x] - matrix[y - 1, x + 1] +
                        matrix[y + 1, x - 1] + 2 * matrix[y + 1, x] + matrix[y + 1, x + 1];

                    if (System.Math.Sqrt(gx * gx + gy * gy) > 80)
                        edges++;
                }
            }

            return edges / ((height - 2) * (width - 2));
        }

        public static double ForegroundRatio(double[,] matrix)
        {
            int height = matrix.GetLength(0);
            int width = matrix.GetLength(1);
            int count = height * width;
            if (count == 0) return 0;

            double mean = Mean(matrix);
            double threshold = mean - StdDev(matrix, mean) * 0.2;
            int foreground = 0;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    if (matrix[y, x] < threshold) foreground++;

            return foreground / (double)count;
        }

        public static double RidgeLikeScore(double[,] matrix)
        {
            int height = matrix.GetLength(0);
            int width = matrix.GetLength(1);
            if (height < 5 || width < 5) return 0;

            double score = 0;
            int count = 0;
            for (int y = 2; y < height - 2; y++)
            {
                for (int x = 2; x < width - 2; x++)
                {
                    double center = matrix[y, x];
                    double horizontal = System.Math.Abs((matrix[y, x - 1] - center) * (matrix[y, x + 1] - center));
                    double vertical = System.Math.Abs((matrix[y - 1, x] - center) * (matrix[y + 1, x] - center));
                    score += horizontal + vertical;
                    count++;
                }
            }

            return score / System.Math.Max(1, count) / 255.0 / 255.0;
        }
    }
}
