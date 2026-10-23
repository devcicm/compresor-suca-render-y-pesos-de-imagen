using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SUCA_Compressor.Application.Common;
using SUCA_Compressor.Application.Settings;

namespace SUCA_Compressor.Application.Conversion
{
    public sealed class ConsoleBatchOptions
    {
        public List<string> Files { get; } = new();
        public string? InputDirectory { get; set; }
        public bool Recursive { get; set; }
        public string? OrderFile { get; set; }
        public string? Collection { get; set; }
        public string? OutputDirectory { get; set; }
        public int Parallelism { get; set; } = 3;
        public string? Engine { get; set; }
        public string? Format { get; set; }
        public int? Quality { get; set; }
        public int? MaxWidth { get; set; }
        public int? MaxHeight { get; set; }
        public bool RemoveMetadata { get; set; } = true;

        public bool IsBatch => OrderFile is not null || InputDirectory is not null || Files.Count > 1;
    }

    /// <summary>Composition root (without DI) for console conversion flows.</summary>
    public sealed class SucaConversionRuntime
    {
        public ConversionStorageOptions Storage { get; }
        public IImageConversionService Conversion { get; }
        public IConversionOrderOrchestrator Orchestrator { get; }

        private SucaConversionRuntime(
            ConversionStorageOptions storage,
            IImageConversionService conversion,
            IConversionOrderOrchestrator orchestrator)
        {
            Storage = storage;
            Conversion = conversion;
            Orchestrator = orchestrator;
        }

        public static SucaConversionRuntime Create(string? storageRootOverride = null)
        {
            var storage = new ConversionStorageOptions
            {
                RootPath = string.IsNullOrWhiteSpace(storageRootOverride)
                    ? SucaRuntimePaths.DefaultWebStorageRoot()
                    : storageRootOverride
            };

            var keyedLock = new AsyncKeyedLock();
            var fileStore = new AtomicFileStore(keyedLock, NullLogger<AtomicFileStore>.Instance);
            var observability = new RuntimeObservability();
            var gallery = new JsonConversionGalleryStore(storage, fileStore, keyedLock, NullLogger<JsonConversionGalleryStore>.Instance);
            var registry = new CollectionsRegistryStore(storage, fileStore, keyedLock);
            var orderStore = new JsonConversionOrderStore(storage, fileStore, keyedLock);
            var conversion = new ImageConversionService(
                storage,
                gallery,
                fileStore,
                observability,
                NullLogger<ImageConversionService>.Instance,
                new SucaCompressor(),
                registry);
            var orchestrator = new ConversionOrderOrchestrator(
                storage,
                orderStore,
                conversion,
                observability,
                NullLogger<ConversionOrderOrchestrator>.Instance);

            return new SucaConversionRuntime(storage, conversion, orchestrator);
        }
    }

    public static class ConsoleConversionRunner
    {
        public static ConsoleBatchOptions BuildOptionsFromArgs(string[] args)
        {
            var options = new ConsoleBatchOptions();
            string? input = ReadOption(args, "--input");

            if (!string.IsNullOrWhiteSpace(input))
            {
                string fullPath = Path.GetFullPath(input.Trim().Trim('"'));
                if (Directory.Exists(fullPath))
                    options.InputDirectory = fullPath;
                else if (File.Exists(fullPath))
                    options.Files.Add(fullPath);
            }

            options.OrderFile = ReadOption(args, "--order")?.Trim().Trim('"');
            options.Collection = ReadOption(args, "--collection")?.Trim();
            options.OutputDirectory = ReadOption(args, "--output")?.Trim().Trim('"');
            options.Engine = ReadOption(args, "--engine");
            options.Format = ReadOption(args, "--format");
            options.Quality = ReadIntOption(args, "--quality");
            options.MaxWidth = ReadIntOption(args, "--max-width");
            options.MaxHeight = ReadIntOption(args, "--max-height");
            options.Parallelism = ReadIntOption(args, "--parallel") ?? 3;
            options.Recursive = HasFlag(args, "--recursive");

            if (HasFlag(args, "--keep-metadata"))
                options.RemoveMetadata = false;

            return options;
        }

        public static async Task<int> RunBatchAsync(ConsoleBatchOptions options)
        {
            if (options is null)
                throw new ArgumentNullException(nameof(options));

            var runtime = SucaConversionRuntime.Create();
            ConversionOrderRequest? request = await BuildRequestAsync(options).ConfigureAwait(false);
            if (request is null)
                return 2;

            Result<ConversionOrder> submitted = await runtime.Orchestrator
                .SubmitAsync(request)
                .ConfigureAwait(false);

            if (!submitted.IsSuccess)
            {
                Console.WriteLine($"ERROR [{submitted.ErrorCode}]: {submitted.Error}");
                return 2;
            }

            ConversionOrder order = submitted.Value;
            Console.WriteLine();
            Console.WriteLine($"Orden {order.OrderId} iniciada con {order.Items.Count} imagen(es).");
            Console.WriteLine($"Correlacion para eventos y trazabilidad: {order.CorrelationId}");
            Console.WriteLine("Controles: P pausa, R reanuda, C cancela.");
            Console.WriteLine();

            bool redirected = Console.IsOutputRedirected;
            int renderTop = redirected ? -1 : Console.CursorTop;
            string lastSignature = "";

            while (true)
            {
                if (!redirected)
                    HandleKeys(runtime, order.OrderId);

                ConversionOrder? current = await runtime.Orchestrator.GetAsync(order.OrderId).ConfigureAwait(false);
                if (current is null)
                    break;

                var summary = BuildSummarySnapshot(current);
                if (!redirected)
                {
                    string signature = summary;
                    if (signature != lastSignature)
                    {
                        RenderProgress(current, summary, renderTop);
                        lastSignature = signature;
                    }
                }
                else if (summary != lastSignature)
                {
                    Console.WriteLine(summary);
                    lastSignature = summary;
                }

                if (IsFinished(current))
                    break;

                await Task.Delay(300).ConfigureAwait(false);
            }

            ConversionOrder? final = await runtime.Orchestrator.GetAsync(order.OrderId).ConfigureAwait(false);
            if (final is null)
                return 1;

            Console.WriteLine();
            return await PrintSummaryAsync(runtime, final).ConfigureAwait(false);
        }

        private static async Task<ConversionOrderRequest?> BuildRequestAsync(ConsoleBatchOptions options)
        {
            if (options.OrderFile is not null)
            {
                try
                {
                    string json = await File.ReadAllTextAsync(options.OrderFile).ConfigureAwait(false);
                    var request = JsonSerializer.Deserialize<ConversionOrderRequest>(json, JsonDefaults.Loose);
                    if (request is null)
                    {
                        Console.WriteLine($"ERROR: el archivo de orden no contiene una orden valida: {options.OrderFile}");
                        return null;
                    }

                    request.Parallelism ??= options.Parallelism;
                    request.Collection ??= options.Collection;
                    return request;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ERROR al leer la orden: {ex.Message}");
                    return null;
                }
            }

            var defaults = new ConversionOrderDefaults
            {
                Engine = options.Engine,
                Format = options.Format,
                Quality = options.Quality,
                MaxWidth = options.MaxWidth,
                MaxHeight = options.MaxHeight,
                RemoveMetadata = options.RemoveMetadata,
                Collection = options.Collection
            };

            return new ConversionOrderRequest
            {
                SourceDirectory = options.InputDirectory,
                Recursive = options.Recursive,
                Items = options.Files.Count > 0
                    ? options.Files.Select(path => new ConversionOrderItemRequest { Path = path }).ToList()
                    : null,
                Defaults = defaults,
                Output = string.IsNullOrWhiteSpace(options.OutputDirectory)
                    ? new ConversionOrderOutput { Mode = "gallery" }
                    : new ConversionOrderOutput { Mode = "directory", Directory = options.OutputDirectory },
                Parallelism = options.Parallelism,
                Collection = options.Collection,
                // Correlacion legible y trazable desde el arranque del lote por CLI.
                CorrelationId = $"console-{DateTime.UtcNow:yyyyMMdd-HHmmss}"
            };
        }

        private static void HandleKeys(SucaConversionRuntime runtime, Guid orderId)
        {
            while (Console.KeyAvailable)
            {
                ConsoleKeyInfo key = Console.ReadKey(true);
                switch (key.Key)
                {
                    case ConsoleKey.P:
                        _ = runtime.Orchestrator.PauseAsync(orderId);
                        break;
                    case ConsoleKey.R:
                        _ = runtime.Orchestrator.ResumeAsync(orderId);
                        break;
                    case ConsoleKey.C:
                    case ConsoleKey.Escape:
                        _ = runtime.Orchestrator.CancelAsync(orderId);
                        break;
                }
            }
        }

        private static string BuildSummarySnapshot(ConversionOrder order)
        {
            int total = order.Items.Count;
            int terminal = order.Items.Count(x => ConversionJobStateMachine.IsTerminal(x.State));
            int completed = order.Items.Count(x => x.State == ConversionJobState.Completed);
            int failed = order.Items.Count(x => x.State == ConversionJobState.Failed);
            int cancelled = order.Items.Count(x => x.State == ConversionJobState.Cancelled);
            string paused = order.Paused ? " [PAUSADA]" : "";

            return $"Progreso: {terminal}/{total} ({(total == 0 ? 0 : Math.Round(terminal * 100.0 / total, 0))}%)  " +
                   $"ok={completed} fallos={failed} canceladas={cancelled}{paused}";
        }

        private static void RenderProgress(ConversionOrder order, string summary, int renderTop)
        {
            try
            {
                Console.SetCursorPosition(0, Math.Max(0, renderTop));
            }
            catch
            {
            }

            var builder = new StringBuilder();
            builder.AppendLine(summary);
            builder.AppendLine();

            const int maxRows = 12;
            var rows = order.Items
                .Select((item, index) => (item, index))
                .Where(x => !ConversionJobStateMachine.IsTerminal(x.item.State))
                .Take(maxRows)
                .ToList();

            if (rows.Count == 0)
            {
                rows = order.Items
                    .Select((item, index) => (item, index))
                    .Where(x => ConversionJobStateMachine.IsTerminal(x.item.State))
                    .TakeLast(maxRows)
                    .ToList();
            }

            foreach (var (item, index) in rows)
            {
                string bar = BuildBar(item.State switch
                {
                    ConversionJobState.Completed => 100,
                    ConversionJobState.Running => 60,
                    ConversionJobState.Uploading => 30,
                    _ => 0
                });

                builder.AppendLine(
                    $"  [{bar}] {StateLabel(item.State),-10} {index + 1,3}. {Truncate(item.FileName, 42)}");
            }

            builder.AppendLine();
            builder.AppendLine("  (P pausa | R reanuda | C cancela)");

            int previousRow = Console.CursorTop;
            Console.Write(builder.ToString());

            try
            {
                int remaining = previousRow - Console.CursorTop;
                if (remaining > 0)
                    Console.Write(new string(' ', Console.WindowWidth * remaining));
            }
            catch
            {
            }
        }

        private static string BuildBar(int percent)
        {
            const int width = 20;
            int filled = Math.Clamp(percent * width / 100, 0, width);
            return new string('#', filled) + new string('-', width - filled);
        }

        private static string StateLabel(ConversionJobState state) => state switch
        {
            ConversionJobState.Pending => "pendiente",
            ConversionJobState.Running => "activo",
            ConversionJobState.Paused => "pausado",
            ConversionJobState.Cancelling => "cancelando",
            ConversionJobState.Cancelled => "cancelado",
            ConversionJobState.Failed => "fallo",
            ConversionJobState.Interrupted => "interrump.",
            ConversionJobState.Completed => "listo",
            _ => state.ToString().ToLowerInvariant()
        };

        private static async Task<int> PrintSummaryAsync(SucaConversionRuntime runtime, ConversionOrder order)
        {
            var records = (await runtime.Conversion.ListAsync().ConfigureAwait(false))
                .Where(x => order.Items.Any(item => item.RecordId == x.Id))
                .ToDictionary(x => x.Id);

            Console.WriteLine("Resumen de la orden");
            Console.WriteLine(
                $"  {"Archivo",-34} {"Estado",-10} {"Entrada",10} {"Salida",10} {"Ahorro",8} {"bpp",6} {"Ratio",7} {"ms",7}  Coleccion");

            foreach (ConversionOrderItem item in order.Items)
            {
                records.TryGetValue(item.RecordId ?? Guid.Empty, out ConversionRecord? record);

                string input = record is null ? "-" : FormatBytes(record.OriginalBytes);
                string output = record is null ? "-" : FormatBytes(record.ConvertedBytes);
                string saving = record is null ? "-" : $"{record.SavingPercent:F1}%";
                string bpp = record is null ? "-" : record.OutputBitsPerPixel.ToString("F3");
                string ratio = record is null ? "-" : $"{record.OutputCompressionRatio:F1}:1";
                string duration = record is null ? "-" : record.DurationMs.ToString("F0");
                string collection = record?.Collection ?? "-";

                Console.WriteLine(
                    $"  {Truncate(item.FileName, 34),-34} {StateLabel(item.State),-10} {input,10} {output,10} {saving,8} {bpp,6} {ratio,7} {duration,7}  {collection}");
            }

            var summary = ToStatus(order);
            Console.WriteLine();
            Console.WriteLine($"Estado final de la orden: {summary}");
            return summary == "completed" ? 0 : 1;
        }

        private static string ToStatus(ConversionOrder order)
        {
            int total = order.Items.Count;
            int terminal = order.Items.Count(x => ConversionJobStateMachine.IsTerminal(x.State));
            int failed = order.Items.Count(x => x.State is ConversionJobState.Failed or ConversionJobState.Interrupted);
            bool allCancelled = order.Items.All(x => x.State == ConversionJobState.Cancelled);

            if (allCancelled)
                return "cancelled";
            if (terminal == total)
                return failed > 0 ? "completedWithErrors" : "completed";
            return "incomplete";
        }

        private static bool IsFinished(ConversionOrder order) =>
            order.Items.Count > 0 && order.Items.All(x => ConversionJobStateMachine.IsTerminal(x.State));

        private static string Truncate(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..(maxLength - 1)] + "\u2026";

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";
            if (bytes < 1024 * 1024)
                return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F2} MB";
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

        private static int? ReadIntOption(string[] args, string optionName) =>
            int.TryParse(ReadOption(args, optionName), out int parsed) ? parsed : null;

        private static bool HasFlag(string[] args, string name) =>
            args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
    }
}
