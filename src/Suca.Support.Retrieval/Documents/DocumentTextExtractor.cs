using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;
using UglyToad.PdfPig;

namespace SUCA_Compressor
{
    public static class DocumentTextExtractor
    {
        private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

        public static async Task<string> ExtractTextAsync(string path, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Ruta inválida.", nameof(path));
            if (!File.Exists(path)) throw new FileNotFoundException("Archivo no encontrado.", path);

            cancellationToken.ThrowIfCancellationRequested();

            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".txt" or ".md" or ".csv" or ".log" or ".json" or ".jsonl" or ".xml" or ".yaml" or ".yml" or ".ini" or ".cfg" or ".conf" or ".sql" or ".ps1" or ".cs" or ".ts" or ".js" or ".py" or ".java" or ".rb" or ".go" or ".rs" =>
                    Normalize(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)),

                ".html" or ".htm" or ".xhtml" =>
                    ExtractHtml(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)),

                ".rtf" =>
                    ExtractRtf(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)),

                ".pdf" =>
                    ExtractPdf(path, cancellationToken),

                ".docx" =>
                    ExtractOpenXmlText(path, "word/document.xml", "t"),

                ".xlsx" =>
                    ExtractSpreadsheetText(path),

                ".pptx" =>
                    ExtractPresentationText(path),

                ".odt" or ".ods" or ".odp" =>
                    ExtractOdfText(path),

                ".epub" =>
                    ExtractEpubText(path),

                _ =>
                    TryBestEffortBinaryAsText(path, cancellationToken)
            };
        }

        private static string ExtractPdf(string path, CancellationToken cancellationToken)
        {
            var sb = new StringBuilder();
            using var document = PdfDocument.Open(path);
            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                sb.AppendLine(page.Text);
            }
            return Normalize(sb.ToString());
        }

        private static string ExtractOpenXmlText(string path, string entryName, string localName)
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.GetEntry(entryName);
            if (entry is null) return string.Empty;

            using var stream = entry.Open();
            var xdoc = XDocument.Load(stream);
            var text = string.Join(" ", xdoc.Descendants().Where(x => x.Name.LocalName == localName).Select(x => x.Value));
            return Normalize(text);
        }

        private static string ExtractSpreadsheetText(string path)
        {
            using var archive = ZipFile.OpenRead(path);
            var sharedStrings = ReadSharedStrings(archive);
            var sheetTexts = new List<string>();

            foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
            {
                using var stream = entry.Open();
                var xdoc = XDocument.Load(stream);
                foreach (var cell in xdoc.Descendants().Where(x => x.Name.LocalName == "c"))
                {
                    string cellType = cell.Attribute("t")?.Value ?? string.Empty;
                    var valueNode = cell.Descendants().FirstOrDefault(x => x.Name.LocalName == "v");
                    if (valueNode is null) continue;

                    string value = valueNode.Value;
                    if (string.Equals(cellType, "s", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(value, out int index) &&
                        index >= 0 &&
                        index < sharedStrings.Count)
                    {
                        sheetTexts.Add(sharedStrings[index]);
                    }
                    else
                    {
                        sheetTexts.Add(value);
                    }
                }
            }

            return Normalize(string.Join(Environment.NewLine, sheetTexts));
        }

        private static List<string> ReadSharedStrings(ZipArchive archive)
        {
            var entry = archive.GetEntry("xl/sharedStrings.xml");
            if (entry is null) return new List<string>();

            using var stream = entry.Open();
            var xdoc = XDocument.Load(stream);
            return xdoc.Descendants()
                .Where(x => x.Name.LocalName == "t")
                .Select(x => x.Value)
                .ToList();
        }

        private static string ExtractPresentationText(string path)
        {
            using var archive = ZipFile.OpenRead(path);
            var texts = new List<string>();

            foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith("ppt/slides/", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
            {
                using var stream = entry.Open();
                var xdoc = XDocument.Load(stream);
                texts.AddRange(xdoc.Descendants().Where(x => x.Name.LocalName == "t").Select(x => x.Value));
            }

            return Normalize(string.Join(Environment.NewLine, texts));
        }

        private static string ExtractOdfText(string path)
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.GetEntry("content.xml");
            if (entry is null) return string.Empty;

            using var stream = entry.Open();
            var xdoc = XDocument.Load(stream);
            var text = string.Join(" ", xdoc.Descendants().Where(x =>
                x.Name.LocalName is "p" or "span" or "h" or "list-item" or "line-break").Select(x => x.Value));
            return Normalize(text);
        }

        private static string ExtractEpubText(string path)
        {
            using var archive = ZipFile.OpenRead(path);
            var htmlEntries = archive.Entries
                .Where(e => e.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) ||
                            e.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                            e.FullName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase);

            var sb = new StringBuilder();
            foreach (var entry in htmlEntries)
            {
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8, true);
                sb.AppendLine(ExtractHtml(reader.ReadToEnd()));
            }

            return Normalize(sb.ToString());
        }

        private static string ExtractHtml(string html)
        {
            string decoded = WebUtility.HtmlDecode(html ?? string.Empty);
            decoded = Regex.Replace(decoded, @"<script\b[^<]*(?:(?!</script>)<[^<]*)*</script>", " ", RegexOptions.IgnoreCase);
            decoded = Regex.Replace(decoded, @"<style\b[^<]*(?:(?!</style>)<[^<]*)*</style>", " ", RegexOptions.IgnoreCase);
            decoded = HtmlTagRegex.Replace(decoded, " ");
            return Normalize(decoded);
        }

        private static string ExtractRtf(string rtf)
        {
            using var box = new RichTextBox();
            box.Rtf = rtf;
            return Normalize(box.Text);
        }

        private static string TryBestEffortBinaryAsText(string path, CancellationToken cancellationToken)
        {
            byte[] bytes = File.ReadAllBytes(path);
            cancellationToken.ThrowIfCancellationRequested();

            if (bytes.Length == 0)
                return string.Empty;

            string text = Encoding.UTF8.GetString(bytes);
            return Normalize(text);
        }

        private static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            text = text.Replace("\u0000", " ");
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            text = WhitespaceRegex.Replace(text, " ");
            return text.Trim();
        }
    }
}
