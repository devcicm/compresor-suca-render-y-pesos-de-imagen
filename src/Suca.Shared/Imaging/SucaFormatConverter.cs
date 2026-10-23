using System;
using System.IO;
using System.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace SUCA_Compressor.Shared.Imaging
{
    // Generic image re-encoding. It contains no SUCA compression rules.
    // Named SucaFormatConverter to avoid colliding with System.Drawing.ImageFormatConverter,
    // which WinForms projects import implicitly.
    public enum ConversionTargetFormat { Jpeg, Png, Webp }

    public sealed class ConversionRequest
    {
        public ConversionTargetFormat Format { get; set; } = ConversionTargetFormat.Webp;

        /// <summary>1..100. Ignored for PNG. Null keeps the encoder default.</summary>
        public int? Quality { get; set; }

        public int? MaxWidth { get; set; }
        public int? MaxHeight { get; set; }
        public bool RemoveMetadata { get; set; } = true;
    }

    public sealed class ConversionOutput
    {
        public byte[] Bytes { get; init; } = Array.Empty<byte>();
        public ConversionTargetFormat Format { get; init; }
        public string ContentType { get; init; } = "";
        public string FileExtension { get; init; } = "";
        public int Width { get; init; }
        public int Height { get; init; }
    }

    public static class SucaFormatConverter
    {
        public const string JpegContentType = "image/jpeg";
        public const string PngContentType = "image/png";
        public const string WebpContentType = "image/webp";

        public static ConversionOutput Convert(Stream input, ConversionRequest request, CancellationToken cancellationToken = default)
        {
            if (input is null) throw new ArgumentNullException(nameof(input));
            if (request is null) throw new ArgumentNullException(nameof(request));

            cancellationToken.ThrowIfCancellationRequested();

            using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(input);
            image.Mutate(x => x.AutoOrient());

            if (request.RemoveMetadata)
            {
                image.Metadata.ExifProfile = null;
                image.Metadata.IccProfile = null;
                image.Metadata.IptcProfile = null;
                image.Metadata.XmpProfile = null;
            }

            int? maxWidth = NormalizeDimension(request.MaxWidth);
            int? maxHeight = NormalizeDimension(request.MaxHeight);
            if (maxWidth.HasValue || maxHeight.HasValue)
            {
                var size = new SixLabors.ImageSharp.Size(
                    maxWidth ?? image.Width,
                    maxHeight ?? image.Height);

                if (size.Width < image.Width || size.Height < image.Height)
                {
                    image.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Mode = ResizeMode.Max,
                        Sampler = KnownResamplers.MitchellNetravali,
                        Size = size
                    }));
                }
            }

            using var buffer = new MemoryStream();
            image.Save(buffer, BuildEncoder(request));

            return new ConversionOutput
            {
                Bytes = buffer.ToArray(),
                Format = request.Format,
                ContentType = ContentTypeFor(request.Format),
                FileExtension = ExtensionFor(request.Format),
                Width = image.Width,
                Height = image.Height
            };
        }

        public static string ContentTypeFor(ConversionTargetFormat format) => format switch
        {
            ConversionTargetFormat.Jpeg => JpegContentType,
            ConversionTargetFormat.Png => PngContentType,
            _ => WebpContentType
        };

        public static string ExtensionFor(ConversionTargetFormat format) => format switch
        {
            ConversionTargetFormat.Jpeg => ".jpg",
            ConversionTargetFormat.Png => ".png",
            _ => ".webp"
        };

        public static bool TryParseFormat(string? value, out ConversionTargetFormat format)
        {
            switch (NormalizeToken(value))
            {
                case "jpeg":
                case "jpg":
                    format = ConversionTargetFormat.Jpeg;
                    return true;
                case "png":
                    format = ConversionTargetFormat.Png;
                    return true;
                case "webp":
                case "web":
                    format = ConversionTargetFormat.Webp;
                    return true;
                default:
                    format = ConversionTargetFormat.Webp;
                    return false;
            }
        }

        private static IImageEncoder BuildEncoder(ConversionRequest request) => request.Format switch
        {
            ConversionTargetFormat.Jpeg => new JpegEncoder { Quality = ClampQuality(request.Quality ?? 85) },
            ConversionTargetFormat.Webp => new WebpEncoder { Quality = ClampQuality(request.Quality ?? 80) },
            _ => new PngEncoder()
        };

        private static int ClampQuality(int quality) => System.Math.Clamp(quality, 1, 100);

        private static int? NormalizeDimension(int? value) =>
            value.HasValue && value.Value > 0 ? System.Math.Min(value.Value, 20000) : null;

        private static string NormalizeToken(string? value) =>
            (value ?? string.Empty).Trim().Trim('.').ToLowerInvariant();
    }
}
