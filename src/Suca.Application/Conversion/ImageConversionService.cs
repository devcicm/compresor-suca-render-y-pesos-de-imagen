using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp.Formats;
using SUCA_Compressor.Application.Common;
using SUCA_Compressor.Kernel;
using SUCA_Compressor.Shared.Imaging;
using Image = SixLabors.ImageSharp.Image;

namespace SUCA_Compressor.Application.Conversion
{
    public sealed class ConversionStorageOptions
    {
        public string RootPath { get; set; } = "";
        public const string ManifestFileName = "gallery.json";
        public const string ImagesFolderName = "images";
        public const string DefaultCollection = "general";
    }

    public sealed class ConvertImageCommand
    {
        public string FileName { get; set; } = "";
        /// <summary>"direct" re-encodes the pixels; "suca" runs the full SUCA kernel pipeline.</summary>
        public string Engine { get; set; } = "direct";
        public string Format { get; set; } = "webp";
        public int? Quality { get; set; }
        public int? MaxWidth { get; set; }
        public int? MaxHeight { get; set; }
        public bool RemoveMetadata { get; set; } = true;
        public string? Collection { get; set; }
        public string? CorrelationId { get; set; }
        public SucaProfile? Profile { get; set; }
        public SucaOptions? Options { get; set; }
    }

    public sealed class ConversionRecord
    {
        public Guid Id { get; set; }
        public string Collection { get; set; } = ConversionStorageOptions.DefaultCollection;
        public string CorrelationId { get; set; } = "";
        public ConversionJobState State { get; set; } = ConversionJobState.Completed;
        public List<StateHistoryEntry> StateHistory { get; set; } = new();
        public string OriginalFileName { get; set; } = "";
        public string OriginalContentType { get; set; } = "";
        public string InputFormat { get; set; } = "";
        public bool? InputHasExif { get; set; }
        public string Engine { get; set; } = "direct";
        public string Format { get; set; } = "webp";
        public string ContentType { get; set; } = "";
        public int? Quality { get; set; }
        public int? MaxWidth { get; set; }
        public int? MaxHeight { get; set; }
        public bool RemoveMetadata { get; set; } = true;
        public long OriginalBytes { get; set; }
        public int OriginalWidth { get; set; }
        public int OriginalHeight { get; set; }
        public long ConvertedBytes { get; set; }
        public int ConvertedWidth { get; set; }
        public int ConvertedHeight { get; set; }
        public bool? OutputHasAlpha { get; set; }
        public double OutputBitsPerPixel { get; set; }
        public double OutputCompressionRatio { get; set; }
        public long DurationMs { get; set; }
        public double SavingPercent { get; set; }
        public int? SucaHash { get; set; }

        // Reporte de integridad perceptual (solo motor SUCA). Nulos = no generado
        // (motor directo) o registro antiguo.
        public double? IntegrityPsnr { get; set; }
        public double? IntegritySsim { get; set; }
        public double? IntegrityEdgeRetention { get; set; }
        public double? IntegrityHistogram { get; set; }
        public string? IntegrityMode { get; set; }
        public string? IntegrityVerdict { get; set; }
        public bool IntegrityBitwiseEqual { get; set; }

        // Peso mosca: techo pedido, si se cumplio y cuantas iteraciones costo.
        public double? PesoObjetivoKb { get; set; }
        public bool? PesoObjetivoCumplido { get; set; }
        public int PesoIteraciones { get; set; }

        // Optimizacion por busqueda (escala x calidad) y su puntuacion de legibilidad.
        public bool Optimized { get; set; }
        public double? OptimizedScale { get; set; }
        public int? OptimizedQuality { get; set; }
        public int CandidatesEvaluated { get; set; }
        public double? QualityScore { get; set; }
        public bool? LegibilityAcceptable { get; set; }

        public string OriginalFile { get; set; } = "";
        public string ConvertedFile { get; set; } = "";
        public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    public sealed class ConversionFileContent
    {
        public byte[] Bytes { get; init; } = Array.Empty<byte>();
        public string ContentType { get; init; } = "";
        public string DownloadName { get; init; } = "";
    }

    public sealed class ConversionFilePath
    {
        public Guid Id { get; set; }
        public string Variant { get; set; } = "converted";
        public string Path { get; set; } = "";
        public string Directory { get; set; } = "";
        public string FileName { get; set; } = "";
        public bool Exists { get; set; }
    }

    /// <summary>Progreso observable de una conversion en vuelo (por correlacion).</summary>
    public sealed class ConversionProgress
    {
        public string CorrelationId { get; init; } = "";
        public string Phase { get; set; } = "queued";
        public int Percent { get; set; }
        public string? Detail { get; set; }
        public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>Ultimo latido del request: se refresca cada segundo aunque la etapa no avance.</summary>
        public DateTimeOffset HeartbeatAtUtc { get; set; } = DateTimeOffset.UtcNow;

        public bool Completed { get; set; }

        public long ElapsedMs => (long)(DateTimeOffset.UtcNow - StartedAtUtc).TotalMilliseconds;
        public long SinceUpdateMs => (long)(DateTimeOffset.UtcNow - UpdatedAtUtc).TotalMilliseconds;
        public long HeartbeatMs => (long)(DateTimeOffset.UtcNow - HeartbeatAtUtc).TotalMilliseconds;
    }

    public sealed class CollectionSummary
    {
        public string Collection { get; set; } = "";
        public int Total { get; set; }
        public long OriginalBytes { get; set; }
        public long ConvertedBytes { get; set; }
        public double SavingPercent { get; set; }
        public DateTimeOffset LastAtUtc { get; set; }
    }

    public sealed class CollectionImagesRequest
    {
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 24;
        public bool IncludeBase64 { get; set; } = true;
        public bool IncludeOriginalBase64 { get; set; }
    }

    public sealed class CollectionImageInputDto
    {
        public string Format { get; set; } = "";
        public string ContentType { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public long Bytes { get; set; }
        public double Megapixels { get; set; }
        public bool? HasExif { get; set; }
        public DateTimeOffset? LastWriteTimeUtc { get; set; }
    }

    public sealed class CollectionImageOutputDto
    {
        public string Format { get; set; } = "";
        public string ContentType { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public long Bytes { get; set; }
        public int? Quality { get; set; }
        public bool? HasAlpha { get; set; }
        public double BitsPerPixel { get; set; }
        public double CompressionRatio { get; set; }
        public long DurationMs { get; set; }
    }

    public sealed class CollectionImageMetricsDto
    {
        public double SavingPercent { get; set; }
        public string CompressionLabel { get; set; } = "";
    }

    public sealed class CollectionImageLinksDto
    {
        public string File { get; set; } = "";
        public string Original { get; set; } = "";
        public string Detail { get; set; } = "";
    }

    public sealed class CollectionImageBase64Dto
    {
        public string? Converted { get; set; }
        public string? Original { get; set; }
    }

    public sealed class CollectionImageDataUrlDto
    {
        public string? Converted { get; set; }
    }

    public sealed class CollectionImageDto
    {
        public Guid Id { get; set; }
        public string Collection { get; set; } = "";
        public string OriginalFileName { get; set; } = "";
        public DateTimeOffset CreatedAtUtc { get; set; }
        public string Engine { get; set; } = "";
        public string State { get; set; } = "";
        public string CorrelationId { get; set; } = "";
        public int? SucaHash { get; set; }
        public CollectionImageInputDto Input { get; set; } = new();
        public CollectionImageOutputDto Output { get; set; } = new();
        public CollectionImageMetricsDto Metrics { get; set; } = new();
        public CollectionImageLinksDto Links { get; set; } = new();
        public CollectionImageBase64Dto? Base64 { get; set; }
        public CollectionImageDataUrlDto? DataUrl { get; set; }
    }

    public sealed class CollectionImagesResult
    {
        public string Collection { get; set; } = "";
        public int Total { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public IReadOnlyList<CollectionImageDto> Items { get; set; } = Array.Empty<CollectionImageDto>();
    }

    public sealed class CollectionsRegistryStore
    {
        private readonly string _registryPath;
        private readonly IAtomicFileStore _fileStore;
        private readonly IAsyncKeyedLock _keyedLock;

        public CollectionsRegistryStore(ConversionStorageOptions options, IAtomicFileStore fileStore, IAsyncKeyedLock keyedLock)
        {
            if (options is null || string.IsNullOrWhiteSpace(options.RootPath))
                throw new ArgumentException("ConversionStorageOptions.RootPath es obligatorio.", nameof(options));

            _registryPath = Path.Combine(Path.GetFullPath(options.RootPath), "collections.json");
            _fileStore = fileStore;
            _keyedLock = keyedLock;
        }

        public async Task<List<string>> LoadNamesAsync(CancellationToken cancellationToken = default)
        {
            await using var lease = await _keyedLock.AcquireAsync(_registryPath + "#compound", cancellationToken).ConfigureAwait(false);
            return LoadNamesUnsafe();
        }

        private List<string> LoadNamesUnsafe()
        {
            if (!File.Exists(_registryPath))
                return new List<string>();

            try
            {
                return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_registryPath), JsonDefaults.Loose)
                    ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        public async Task SaveNamesAsync(IEnumerable<string> names, CancellationToken cancellationToken = default)
        {
            await using var lease = await _keyedLock.AcquireAsync(_registryPath + "#compound", cancellationToken).ConfigureAwait(false);
            string json = JsonSerializer.Serialize(names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(), JsonDefaults.Indented);
            await _fileStore.WriteAllTextAtomicAsync(_registryPath, json, cancellationToken).ConfigureAwait(false);
        }

        public async Task EnsureAsync(string name, CancellationToken cancellationToken = default)
        {
            await using var lease = await _keyedLock.AcquireAsync(_registryPath + "#compound", cancellationToken).ConfigureAwait(false);
            var names = LoadNamesUnsafe();
            if (names.Contains(name, StringComparer.OrdinalIgnoreCase))
                return;

            names.Add(name);
            string json = JsonSerializer.Serialize(names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(), JsonDefaults.Indented);
            await _fileStore.WriteAllTextAtomicAsync(_registryPath, json, cancellationToken).ConfigureAwait(false);
        }

        public async Task RemoveAsync(string name, CancellationToken cancellationToken = default)
        {
            await using var lease = await _keyedLock.AcquireAsync(_registryPath + "#compound", cancellationToken).ConfigureAwait(false);
            var names = LoadNamesUnsafe();
            names.RemoveAll(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
            string json = JsonSerializer.Serialize(names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(), JsonDefaults.Indented);
            await _fileStore.WriteAllTextAtomicAsync(_registryPath, json, cancellationToken).ConfigureAwait(false);
        }
    }

    public interface IConversionGalleryStore
    {
        Task<IReadOnlyList<ConversionRecord>> ListAsync(CancellationToken cancellationToken = default);
        Task AddAsync(ConversionRecord record, CancellationToken cancellationToken = default);
        Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default);
    }

    public sealed class JsonConversionGalleryStore : IConversionGalleryStore
    {
        private readonly string _manifestPath;
        private readonly IAtomicFileStore _fileStore;
        private readonly IAsyncKeyedLock _keyedLock;
        private readonly ILogger<JsonConversionGalleryStore> _logger;

        public JsonConversionGalleryStore(
            ConversionStorageOptions options,
            IAtomicFileStore fileStore,
            IAsyncKeyedLock keyedLock,
            ILogger<JsonConversionGalleryStore> logger)
        {
            if (options is null || string.IsNullOrWhiteSpace(options.RootPath))
                throw new ArgumentException("ConversionStorageOptions.RootPath es obligatorio.", nameof(options));

            _manifestPath = Path.Combine(Path.GetFullPath(options.RootPath), ConversionStorageOptions.ManifestFileName);
            _fileStore = fileStore;
            _keyedLock = keyedLock;
            _logger = logger;
        }

        public async Task<IReadOnlyList<ConversionRecord>> ListAsync(CancellationToken cancellationToken = default)
        {
            // Compound key: AtomicFileStore takes the manifest key itself inside
            // WriteAllTextAtomicAsync, and AsyncKeyedLock is not reentrant.
            await using var lease = await _keyedLock.AcquireAsync(_manifestPath + "#compound", cancellationToken).ConfigureAwait(false);
            return ReadRecords().OrderByDescending(x => x.CreatedAtUtc).ToList();
        }

        public async Task AddAsync(ConversionRecord record, CancellationToken cancellationToken = default)
        {
            if (record is null) throw new ArgumentNullException(nameof(record));

            await using var lease = await _keyedLock.AcquireAsync(_manifestPath + "#compound", cancellationToken).ConfigureAwait(false);
            List<ConversionRecord> records = ReadRecords();
            records.RemoveAll(x => x.Id == record.Id);
            records.Add(record);

            string json = JsonSerializer.Serialize(records, JsonDefaults.Indented);
            await _fileStore.WriteAllTextAtomicAsync(_manifestPath, json, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Gallery manifest updated: {ManifestPath} ({Count} records)", _manifestPath, records.Count);
        }

        public async Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default)
        {
            await using var lease = await _keyedLock.AcquireAsync(_manifestPath + "#compound", cancellationToken).ConfigureAwait(false);
            List<ConversionRecord> records = ReadRecords();
            int removed = records.RemoveAll(x => x.Id == id);
            if (removed == 0)
                return false;

            string json = JsonSerializer.Serialize(records, JsonDefaults.Indented);
            await _fileStore.WriteAllTextAtomicAsync(_manifestPath, json, cancellationToken).ConfigureAwait(false);
            return true;
        }

        /// <summary>Atomically replaces the whole manifest (collection moves, dissolves).</summary>
        public async Task<Result> ReplaceAllAsync(List<ConversionRecord> records, CancellationToken cancellationToken = default)
        {
            if (records is null)
                return Result.Fail("Faltan los registros.", Common.ConversionErrorCodes.Internal);

            await using var lease = await _keyedLock.AcquireAsync(_manifestPath + "#compound", cancellationToken).ConfigureAwait(false);
            string json = JsonSerializer.Serialize(records, JsonDefaults.Indented);
            await _fileStore.WriteAllTextAtomicAsync(_manifestPath, json, cancellationToken).ConfigureAwait(false);
            return Result.Ok();
        }

        private List<ConversionRecord> ReadRecords()
        {
            if (!File.Exists(_manifestPath))
                return new List<ConversionRecord>();

            try
            {
                string json = File.ReadAllText(_manifestPath);
                var records = JsonSerializer.Deserialize<List<ConversionRecord>>(json, JsonDefaults.Loose);
                return records ?? new List<ConversionRecord>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gallery manifest unreadable at {ManifestPath}; starting from an empty list.", _manifestPath);
                return new List<ConversionRecord>();
            }
        }
    }

    public interface IImageConversionService
    {
        Task<Result<ConversionRecord>> ConvertAsync(ConvertImageCommand command, Stream content, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<ConversionRecord>> ListAsync(CancellationToken cancellationToken = default);
        Task<ConversionRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);
        Task<ConversionFileContent?> OpenFileAsync(Guid id, string variant, CancellationToken cancellationToken = default);
        Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
        Task<Result> SetCollectionAsync(Guid id, string collection, CancellationToken cancellationToken = default);
        Task<Result<int>> SetCollectionForManyAsync(IEnumerable<Guid> ids, string collection, CancellationToken cancellationToken = default);
        Task<Result<ConversionFileContent>> ExportArchiveAsync(IEnumerable<Guid> ids, string variant, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<CollectionSummary>> ListCollectionsAsync(CancellationToken cancellationToken = default);
        Task<Result<CollectionImagesResult>> GetCollectionAsync(string collection, CollectionImagesRequest request, CancellationToken cancellationToken = default);
        Task<Result<int>> DissolveCollectionAsync(string collection, CancellationToken cancellationToken = default);
        Task<Result<ConversionFilePath>> GetFilePathAsync(Guid id, string variant, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<ConversionFilePath>> GetFilePathsAsync(IEnumerable<Guid> ids, string variant, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<ConversionFilePath>> GetCollectionFilePathsAsync(string collection, string variant, CancellationToken cancellationToken = default);
        Task<Result> RevealInExplorerAsync(Guid id, string variant, CancellationToken cancellationToken = default);
        Result RevealStorageRoot();
        Result<bool> CancelConversion(string correlationId);
        ConversionProgress? GetProgress(string correlationId);
        Task<Result<string>> CreateCollectionAsync(string name, CancellationToken cancellationToken = default);
        Task<Result<string>> RenameCollectionAsync(string currentName, string newName, CancellationToken cancellationToken = default);
    }

    public sealed class ImageConversionService : IImageConversionService, IDisposable
    {
        /// <summary>Limite de pixeles por imagen (120 MP). Evita bloqueos por decodificacion.</summary>
        private const long MaxPixels = 120_000_000;
        private static readonly ActivitySource ActivitySource = new("SUCA.Conversion");
        private static readonly string[] KnownImageExtensions =
            { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tiff" };

        private readonly ConversionStorageOptions _options;
        private readonly IConversionGalleryStore _gallery;
        private readonly IAtomicFileStore _fileStore;
        private readonly ISucaCompressionKernel? _kernel;
        private readonly IRuntimeObservability _observability;
        private readonly ILogger<ImageConversionService> _logger;
        private readonly CollectionsRegistryStore? _registry;

        public ImageConversionService(
            ConversionStorageOptions options,
            IConversionGalleryStore gallery,
            IAtomicFileStore fileStore,
            IRuntimeObservability observability,
            ILogger<ImageConversionService> logger,
            ISucaCompressionKernel? kernel = null,
            CollectionsRegistryStore? registry = null)
        {
            if (options is null || string.IsNullOrWhiteSpace(options.RootPath))
                throw new ArgumentException("ConversionStorageOptions.RootPath es obligatorio.", nameof(options));

            _options = options;
            _gallery = gallery;
            _fileStore = fileStore;
            _kernel = kernel;
            _observability = observability;
            _logger = logger;
            _registry = registry;

            // Barrido periodico: el mapa de progreso no debe crecer sin limite
            // con cada conversion completada.
            _progressSweeper = new System.Threading.Timer(
                _ => SweepProgress(),
                null,
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(1));
        }

        private readonly System.Threading.Timer _progressSweeper;

        /// <summary>Descarta estados de progreso terminados hace mas de 2 minutos.</summary>
        private void SweepProgress()
        {
            foreach (var pair in _progress)
            {
                if (pair.Value.Completed &&
                    pair.Value.SinceUpdateMs > TimeSpan.FromMinutes(2).TotalMilliseconds)
                {
                    _progress.TryRemove(pair.Key, out _);
                }
            }
        }

        public void Dispose() => _progressSweeper?.Dispose();

        // Conversiones en vuelo por correlacion: permite a otra peticion (o al
        // propio panel de cola) cancelar con efectos reales mientras aun corre.
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellable =
            new(StringComparer.OrdinalIgnoreCase);

        // Progreso observable por correlacion (etapa + porcentaje + heartbeat).
        private readonly ConcurrentDictionary<string, ConversionProgress> _progress =
            new(StringComparer.OrdinalIgnoreCase);

        public async Task<Result<ConversionRecord>> ConvertAsync(ConvertImageCommand command, Stream content, CancellationToken cancellationToken = default)
        {
            if (command is null)
                return Result<ConversionRecord>.Fail("Falta el comando de conversion.", ConversionErrorCodes.Internal);
            if (content is null)
                return Result<ConversionRecord>.Fail("Falta el contenido de la imagen.", ConversionErrorCodes.EmptyFile);

            bool suca = IsSucaEngine(command.Engine);
            if (suca && _kernel is null)
                return Result<ConversionRecord>.Fail("El modo SUCA requiere el kernel de compresion registrado.", ConversionErrorCodes.Internal);

            if (!SucaFormatConverter.TryParseFormat(command.Format, out ConversionTargetFormat format))
                return Result<ConversionRecord>.Fail(
                    $"Formato no soportado: {command.Format}. Use webp, jpeg o png.",
                    ConversionErrorCodes.InvalidFormat);

            string operation = suca ? "convert-suca" : "convert";
            string correlationId = string.IsNullOrWhiteSpace(command.CorrelationId)
                ? Guid.NewGuid().ToString("N")
                : command.CorrelationId.Trim();

            var externalCancel = new CancellationTokenSource();
            _cancellable[correlationId] = externalCancel;
            using var linkedCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, externalCancel.Token);
            CancellationToken cancelToken = linkedCancel.Token;

            // Progreso observable: la UI lo consulta para porcentaje real y heartbeat.
            var progress = new ConversionProgress { CorrelationId = correlationId };
            _progress[correlationId] = progress;

            // Latido: una etapa (codificar o verificar) puede tardar segundos sin
            // avanzar. Este sello se refresca cada segundo mientras el request
            // sigue vivo y permite distinguir "etapa larga" de "proceso colgado".
            using var heartbeatTimer = new System.Threading.Timer(
                static state => ((ConversionProgress)state!).HeartbeatAtUtc = DateTimeOffset.UtcNow,
                progress,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1));

            void ReportProgress(int percent, string phase, string? detail = null)
            {
                // Monotono: un respaldo interno (optimizador -> camino geometrico)
                // no debe hacer retroceder la barra en la UI.
                progress.Percent = Math.Max(progress.Percent, Math.Clamp(percent, 0, 100));
                progress.Phase = phase;
                progress.Detail = detail;
                progress.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }

            ReportProgress(5, "leyendo", "recibiendo archivo");

            _observability.RecordStart(operation, correlationId);
            using var activity = ActivitySource.StartActivity("suca.convert");

            var history = new List<StateHistoryEntry>();
            long startElapsed = Stopwatch.GetTimestamp();
            ConversionJobState state = ConversionJobState.Pending;

            void Transition(ConversionJobState next)
            {
                if (!ConversionJobStateMachine.CanTransition(state, next))
                {
                    _logger.LogWarning("Transicion de estado ignorada {From} -> {To}", state, next);
                    return;
                }

                state = next;
                history.Add(new StateHistoryEntry
                {
                    State = state.ToString(),
                    AtUtc = DateTimeOffset.UtcNow,
                    ElapsedMs = ElapsedMs(startElapsed)
                });
                _observability.RecordSuccess($"{operation}:{state.ToString().ToLowerInvariant()}", correlationId, correlationId);
            }

            string root = Path.GetFullPath(_options.RootPath);
            string stagingDir = Path.Combine(root, ".staging", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDir);

            try
            {
                Transition(ConversionJobState.Running);

                byte[] sourceBytes = await ReadAllBytes(content, cancelToken).ConfigureAwait(false);
                if (sourceBytes.Length == 0)
                {
                    Transition(ConversionJobState.Failed);
                    ReportProgress(100, "error", "archivo vacio");
                    progress.Completed = true;
                    _observability.RecordFailure(operation, new InvalidOperationException("Archivo vacio."), correlationId);
                    return Result<ConversionRecord>.Fail("El archivo esta vacio.", ConversionErrorCodes.EmptyFile);
                }

                ReportProgress(10, "preparando", $"{sourceBytes.Length / 1024} KB recibidos");

                // Guardia de tamano: una imagen enorme puede bloquear la codificacion.
                // Se mide con Identify (solo cabecera) antes de decodificar de verdad.
                Result? tooLarge = CheckPixelBudget(sourceBytes);
                if (tooLarge is not null)
                {
                    Transition(ConversionJobState.Failed);
                    ReportProgress(100, "error", tooLarge.Error);
                    progress.Completed = true;
                    return Result<ConversionRecord>.Fail(tooLarge.Error!, tooLarge.ErrorCode);
                }

                string safeName = SanitizeFileName(command.FileName);
                string originalExtension = Path.GetExtension(safeName);
                if (string.IsNullOrWhiteSpace(originalExtension))
                    originalExtension = ".img";

                var record = new ConversionRecord
                {
                    Id = Guid.NewGuid(),
                    Collection = NormalizeCollection(command.Collection),
                    CorrelationId = correlationId,
                    OriginalFileName = safeName,
                    Engine = suca ? "suca" : "direct",
                    Format = SucaFormatConverter.ExtensionFor(format).TrimStart('.'),
                    ContentType = SucaFormatConverter.ContentTypeFor(format),
                    Quality = ClampNullable(command.Quality, 1, 100),
                    MaxWidth = NormalizeDimension(command.MaxWidth),
                    MaxHeight = NormalizeDimension(command.MaxHeight),
                    RemoveMetadata = command.RemoveMetadata,
                    OriginalBytes = sourceBytes.LongLength,
                    StateHistory = history
                };

                string itemDir = Path.Combine(root, ConversionStorageOptions.ImagesFolderName, record.Id.ToString("N"));
                // Puente entre el progreso del kernel (0-100) y el total del request (10-95).
                command.Options ??= new SucaOptions { EnablePesoMosca = false };
                command.Options.Progress = (percent, phase) =>
                    ReportProgress(10 + (int)Math.Round(percent * 0.85), phase);

                // El motor SUCA reporta sus etapas desde el kernel; el directo no tiene
                // desglose, asi que se marca la codificacion antes de empezar.
                if (!suca)
                    ReportProgress(40, "codificando");

                Result<ConversionRecord> outcome = suca
                    ? await FillSucaResultAsync(record, command, sourceBytes, originalExtension, stagingDir, itemDir, format, cancelToken).ConfigureAwait(false)
                    : await FillDirectResultAsync(record, sourceBytes, originalExtension, stagingDir, itemDir, format, cancelToken).ConfigureAwait(false);
                ReportProgress(96, "registrando", "guardando en la galeria");

                if (!outcome.IsSuccess)
                {
                    Transition(ConversionJobState.Failed);
                    ReportProgress(100, "error", outcome.Error);
                    progress.Completed = true;
                    _observability.RecordFailure(operation, new InvalidOperationException(outcome.Error), correlationId);
                    return outcome;
                }

                record.DurationMs = ElapsedMs(startElapsed);
                record.State = ConversionJobState.Completed;
                Transition(ConversionJobState.Completed);

                if (_registry is not null && !string.Equals(record.Collection, ConversionStorageOptions.DefaultCollection, StringComparison.OrdinalIgnoreCase))
                    await _registry.EnsureAsync(record.Collection, cancelToken).ConfigureAwait(false);

                await _gallery.AddAsync(record, cancelToken).ConfigureAwait(false);
                ReportProgress(100, "listo", $"{record.ConvertedBytes / 1024} KB");
                progress.Completed = true;
                _observability.RecordSuccess(operation, $"{record.OriginalFileName} -> {record.Format} ({record.SavingPercent:F1}% saved)", correlationId);
                return Result<ConversionRecord>.Ok(record);
            }
            catch (OperationCanceledException)
            {
                Transition(ConversionJobState.Cancelled);
                ReportProgress(100, "cancelada");
                progress.Completed = true;
                _observability.RecordCancellation(operation, correlationId);
                return Result<ConversionRecord>.Fail("Conversion cancelada.", ConversionErrorCodes.Cancelled);
            }
            catch (Exception ex) when (ex is SixLabors.ImageSharp.UnknownImageFormatException
                                          or SixLabors.ImageSharp.InvalidImageContentException)
            {
                Transition(ConversionJobState.Failed);
                ReportProgress(100, "error", "imagen no soportada");
                progress.Completed = true;
                _observability.RecordFailure(operation, ex, correlationId);
                return Result<ConversionRecord>.Fail(
                    $"El archivo no es una imagen soportada: {ex.Message}",
                    ConversionErrorCodes.UnsupportedImage);
            }
            catch (Exception ex)
            {
                Transition(ConversionJobState.Failed);
                ReportProgress(100, "error", ex.Message);
                progress.Completed = true;
                _observability.RecordFailure(operation, ex, correlationId);
                _logger.LogError(ex, "Image conversion failed for {FileName} (engine={Engine})", command.FileName, command.Engine);
                return Result<ConversionRecord>.Fail(ex.Message, ConversionErrorCodes.IoError);
            }
            finally
            {
                if (_cancellable.TryGetValue(correlationId, out CancellationTokenSource? registered) &&
                    ReferenceEquals(registered, externalCancel))
                {
                    _cancellable.TryRemove(KeyValuePair.Create(correlationId, registered));
                }

                externalCancel.Dispose();
                TryDeleteDirectory(stagingDir);
            }
        }

        public Result<bool> CancelConversion(string correlationId)
        {
            if (string.IsNullOrWhiteSpace(correlationId))
                return Result<bool>.Fail("Falta la correlacion de la conversion.", ConversionErrorCodes.InvalidStateTransition);

            if (!_cancellable.TryGetValue(correlationId.Trim(), out CancellationTokenSource? source))
                return Result<bool>.Fail(
                    "No hay una conversion en curso con esa correlacion (puede que ya haya terminado).",
                    ConversionErrorCodes.RecordNotFound);

            source.Cancel();
            _observability.RecordSuccess("convert-cancel-requested", correlationId, correlationId);
            return Result<bool>.Ok(true);
        }

        /// <summary>Progreso de una conversion en vuelo (o su ultimo estado). Null si no existe.</summary>
        public ConversionProgress? GetProgress(string correlationId)
        {
            if (string.IsNullOrWhiteSpace(correlationId))
                return null;

            string key = correlationId.Trim();
            if (!_progress.TryGetValue(key, out ConversionProgress? progress))
                return null;

            // Limpieza perezosa: los estados terminados se conservan 5 minutos.
            if (progress.Completed && progress.SinceUpdateMs > TimeSpan.FromMinutes(5).TotalMilliseconds)
            {
                _progress.TryRemove(key, out _);
                return null;
            }

            return progress;
        }

        /// <summary>
        /// Rechaza imagenes que exceden el presupuesto de pixeles antes de decodificarlas
        /// completas: una imagen enorme puede tardar minutos o agotar memoria.
        /// </summary>
        private Result? CheckPixelBudget(byte[] sourceBytes)
        {
            try
            {
                using var probe = new MemoryStream(sourceBytes, writable: false);
                var info = Image.Identify(probe);
                if (info is null)
                    return null; // deja que la decodificacion normal reporte unsupported-image

                long pixels = (long)info.Width * info.Height;
                if (pixels > MaxPixels)
                {
                    return Result.Fail(
                        $"La imagen tiene {info.Width}x{info.Height} ({pixels / 1_000_000} MP) y el limite es {MaxPixels / 1_000_000} MP. Redimensionala antes de convertirla.",
                        ConversionErrorCodes.TooLarge);
                }

                return null;
            }
            catch
            {
                return null; // cabecera no reconocida: lo maneja la decodificacion
            }
        }

        public Task<IReadOnlyList<ConversionRecord>> ListAsync(CancellationToken cancellationToken = default) =>
            _gallery.ListAsync(cancellationToken);

        public async Task<ConversionRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var all = await _gallery.ListAsync(cancellationToken).ConfigureAwait(false);
            return all.FirstOrDefault(x => x.Id == id);
        }

        public async Task<ConversionFileContent?> OpenFileAsync(Guid id, string variant, CancellationToken cancellationToken = default)
        {
            ConversionRecord? record = await GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (record is null)
                return null;

            bool wantConverted = !string.Equals(variant, "original", StringComparison.OrdinalIgnoreCase);
            string fileName = wantConverted ? record.ConvertedFile : record.OriginalFile;
            if (string.IsNullOrWhiteSpace(fileName))
                return null;

            string path = Path.Combine(ItemDirectory(record.Id), fileName);
            if (!File.Exists(path))
                return null;

            return new ConversionFileContent
            {
                Bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false),
                ContentType = wantConverted ? record.ContentType : record.OriginalContentType,
                DownloadName = BuildDownloadName(record.OriginalFileName, record.Format, wantConverted)
            };
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            bool removed = await _gallery.RemoveAsync(id, cancellationToken).ConfigureAwait(false);
            if (!removed)
                return false;

            TryDeleteDirectory(ItemDirectory(id));
            _observability.RecordSuccess("convert-delete", id.ToString("N"));
            return true;
        }

        public async Task<Result> SetCollectionAsync(Guid id, string collection, CancellationToken cancellationToken = default)
        {
            string normalized = NormalizeCollection(collection);
            if (string.IsNullOrWhiteSpace(normalized))
                return Result.Fail("El nombre de coleccion no es valido.", ConversionErrorCodes.InvalidCollection);

            var records = (await _gallery.ListAsync(cancellationToken).ConfigureAwait(false)).ToList();
            ConversionRecord? target = records.FirstOrDefault(x => x.Id == id);
            if (target is null)
                return Result.Fail("No existe la imagen indicada.", ConversionErrorCodes.RecordNotFound);

            target.Collection = normalized;
            if (_registry is not null)
                await _registry.EnsureAsync(normalized, cancellationToken).ConfigureAwait(false);

            return await PersistRecordsAsync(records, cancellationToken).ConfigureAwait(false);
        }

        public async Task<Result<int>> SetCollectionForManyAsync(IEnumerable<Guid> ids, string collection, CancellationToken cancellationToken = default)
        {
            string normalized = NormalizeCollection(collection);
            if (string.IsNullOrWhiteSpace(normalized))
                return Result<int>.Fail("El nombre de coleccion no es valido.", ConversionErrorCodes.InvalidCollection);

            var wanted = (ids ?? Enumerable.Empty<Guid>()).Where(x => x != Guid.Empty).Distinct().ToList();
            if (wanted.Count == 0)
                return Result<int>.Fail("Selecciona al menos una imagen.", ConversionErrorCodes.InvalidCollection);

            if (wanted.Count > 500)
                return Result<int>.Fail("El lote no puede superar 500 imagenes.", ConversionErrorCodes.TooManyItems);

            var records = (await _gallery.ListAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var set = wanted.ToHashSet();
            var targets = records.Where(x => set.Contains(x.Id)).ToList();

            if (targets.Count == 0)
                return Result<int>.Fail("Ninguna de las imagenes indicadas existe.", ConversionErrorCodes.RecordNotFound);

            foreach (ConversionRecord record in targets)
                record.Collection = normalized;

            if (_registry is not null)
                await _registry.EnsureAsync(normalized, cancellationToken).ConfigureAwait(false);

            Result persist = await PersistRecordsAsync(records, cancellationToken).ConfigureAwait(false);
            if (!persist.IsSuccess)
                return Result<int>.Fail(persist.Error ?? "No se pudo actualizar el manifiesto.", persist.ErrorCode);

            _observability.RecordSuccess("collection-move", $"{targets.Count} -> {normalized}");
            return Result<int>.Ok(targets.Count);
        }

        public async Task<Result<ConversionFileContent>> ExportArchiveAsync(IEnumerable<Guid> ids, string variant, CancellationToken cancellationToken = default)
        {
            var wanted = (ids ?? Enumerable.Empty<Guid>()).Where(x => x != Guid.Empty).Distinct().ToList();
            if (wanted.Count == 0)
                return Result<ConversionFileContent>.Fail("Selecciona al menos una imagen.", ConversionErrorCodes.InvalidCollection);

            if (wanted.Count > 500)
                return Result<ConversionFileContent>.Fail("El archivo no puede superar 500 imagenes.", ConversionErrorCodes.TooManyItems);

            var set = wanted.ToHashSet();
            var records = (await _gallery.ListAsync(cancellationToken).ConfigureAwait(false))
                .Where(x => set.Contains(x.Id))
                .OrderBy(x => x.OriginalFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (records.Count == 0)
                return Result<ConversionFileContent>.Fail("Ninguna de las imagenes indicadas existe.", ConversionErrorCodes.RecordNotFound);

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var buffer = new MemoryStream();

            try
            {
                using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (ConversionRecord record in records)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        ConversionFilePath info = BuildFilePath(record, variant);
                        if (!info.Exists)
                            continue;

                        string entryName = UniqueEntryName(used, record, variant, info.FileName);
                        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);

                        await using Stream entryStream = entry.Open();
                        await using FileStream source = new(
                            info.Path,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            81920,
                            useAsync: true);

                        await source.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
                    }
                }

                _observability.RecordSuccess("images-export", $"{records.Count} archivo(s), variante={variant}");
                return Result<ConversionFileContent>.Ok(new ConversionFileContent
                {
                    Bytes = buffer.ToArray(),
                    ContentType = "application/zip",
                    DownloadName = $"suca-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip"
                });
            }
            catch (OperationCanceledException)
            {
                return Result<ConversionFileContent>.Fail("Exportacion cancelada.", ConversionErrorCodes.Cancelled);
            }
            catch (Exception ex)
            {
                _observability.RecordFailure("images-export", ex, "");
                return Result<ConversionFileContent>.Fail($"No se pudo crear el ZIP: {ex.Message}", ConversionErrorCodes.IoError);
            }
            finally
            {
                await buffer.DisposeAsync().ConfigureAwait(false);
            }
        }

        private static string UniqueEntryName(HashSet<string> used, ConversionRecord record, string variant, string fileName)
        {
            string baseName = SanitizeFileName(fileName);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = record.Id.ToString("N");

            string candidate = baseName;
            int suffix = 2;
            while (!used.Add(candidate))
            {
                string stem = Path.GetFileNameWithoutExtension(baseName);
                string extension = Path.GetExtension(baseName);
                candidate = $"{stem}-{suffix}{extension}";
                suffix += 1;
            }

            return variant == "original" ? $"original/{candidate}" : candidate;
        }

        public async Task<IReadOnlyList<CollectionSummary>> ListCollectionsAsync(CancellationToken cancellationToken = default)
        {
            var records = await _gallery.ListAsync(cancellationToken).ConfigureAwait(false);
            var registered = _registry is not null
                ? await _registry.LoadNamesAsync(cancellationToken).ConfigureAwait(false)
                : new List<string>();

            var summaries = records
                .GroupBy(x => NormalizeCollection(x.Collection))
                .Select(group => new CollectionSummary
                {
                    Collection = group.Key,
                    Total = group.Count(),
                    OriginalBytes = group.Sum(x => x.OriginalBytes),
                    ConvertedBytes = group.Sum(x => x.ConvertedBytes),
                    SavingPercent = group.Sum(x => x.OriginalBytes) <= 0
                        ? 0
                        : 100.0 - (group.Sum(x => x.ConvertedBytes) / (double)group.Sum(x => x.OriginalBytes) * 100.0),
                    LastAtUtc = group.Max(x => x.CreatedAtUtc)
                })
                .ToDictionary(x => x.Collection, StringComparer.OrdinalIgnoreCase);

            // Colecciones vacias creadas explicitamente (aun sin imagenes).
            foreach (string name in registered)
            {
                if (!summaries.ContainsKey(NormalizeCollection(name)))
                {
                    summaries[NormalizeCollection(name)] = new CollectionSummary
                    {
                        Collection = NormalizeCollection(name),
                        Total = 0
                    };
                }
            }

            return summaries.Values.OrderByDescending(x => x.LastAtUtc).ThenBy(x => x.Collection).ToList();
        }

        public async Task<Result<CollectionImagesResult>> GetCollectionAsync(
            string collection,
            CollectionImagesRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request is null)
                return Result<CollectionImagesResult>.Fail("Faltan los parametros de consulta.", ConversionErrorCodes.Internal);

            string normalized = NormalizeCollection(collection);
            var records = (await _gallery.ListAsync(cancellationToken).ConfigureAwait(false))
                .Where(x => string.Equals(NormalizeCollection(x.Collection), normalized, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.CreatedAtUtc)
                .ToList();

            if (records.Count == 0 && !string.Equals(normalized, ConversionStorageOptions.DefaultCollection, StringComparison.OrdinalIgnoreCase))
                return Result<CollectionImagesResult>.Fail(
                    $"La coleccion '{normalized}' no existe o esta vacia.",
                    ConversionErrorCodes.CollectionNotFound);

            int pageSize = Math.Clamp(request.PageSize <= 0 ? 24 : request.PageSize, 1, 200);
            int page = Math.Max(1, request.Page);
            var pageItems = records.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            var items = new List<CollectionImageDto>(pageItems.Count);
            foreach (ConversionRecord record in pageItems)
            {
                var dto = ToDto(record);
                if (request.IncludeBase64 || request.IncludeOriginalBase64)
                    dto.Base64 = new CollectionImageBase64Dto();

                if (request.IncludeBase64)
                {
                    ConversionFileContent? converted = await OpenFileAsync(record.Id, "converted", cancellationToken).ConfigureAwait(false);
                    if (converted is not null)
                    {
                        dto.Base64!.Converted = Convert.ToBase64String(converted.Bytes);
                        dto.DataUrl ??= new CollectionImageDataUrlDto();
                        dto.DataUrl.Converted = $"data:{converted.ContentType};base64,{dto.Base64.Converted}";
                    }
                }

                if (request.IncludeOriginalBase64)
                {
                    ConversionFileContent? original = await OpenFileAsync(record.Id, "original", cancellationToken).ConfigureAwait(false);
                    if (original is not null)
                        dto.Base64!.Original = Convert.ToBase64String(original.Bytes);
                }

                items.Add(dto);
            }

            return Result<CollectionImagesResult>.Ok(new CollectionImagesResult
            {
                Collection = normalized,
                Total = records.Count,
                Page = page,
                PageSize = pageSize,
                Items = items
            });
        }

        public async Task<Result<int>> DissolveCollectionAsync(string collection, CancellationToken cancellationToken = default)
        {
            string normalized = NormalizeCollection(collection);
            if (string.Equals(normalized, ConversionStorageOptions.DefaultCollection, StringComparison.OrdinalIgnoreCase))
                return Result<int>.Fail("La coleccion general no se puede disolver.", ConversionErrorCodes.InvalidCollection);

            var records = (await _gallery.ListAsync(cancellationToken).ConfigureAwait(false)).ToList();
            int affected = records.Count(x =>
                string.Equals(NormalizeCollection(x.Collection), normalized, StringComparison.OrdinalIgnoreCase));

            bool registered = _registry is not null &&
                (await _registry.LoadNamesAsync(cancellationToken).ConfigureAwait(false))
                .Contains(normalized, StringComparer.OrdinalIgnoreCase);

            if (affected == 0 && !registered)
                return Result<int>.Fail(
                    $"La coleccion '{normalized}' no existe.",
                    ConversionErrorCodes.CollectionNotFound);

            foreach (ConversionRecord record in records)
            {
                if (string.Equals(NormalizeCollection(record.Collection), normalized, StringComparison.OrdinalIgnoreCase))
                    record.Collection = ConversionStorageOptions.DefaultCollection;
            }

            Result persist = await PersistRecordsAsync(records, cancellationToken).ConfigureAwait(false);
            if (!persist.IsSuccess)
                return Result<int>.Fail(persist.Error ?? "No se pudo actualizar el manifiesto.", persist.ErrorCode);

            if (_registry is not null)
                await _registry.RemoveAsync(normalized, cancellationToken).ConfigureAwait(false);

            return Result<int>.Ok(affected);
        }

        public async Task<Result<string>> CreateCollectionAsync(string name, CancellationToken cancellationToken = default)
        {
            string normalized = NormalizeCollection(name);
            if (string.IsNullOrWhiteSpace(name) || normalized.Length == 0)
                return Result<string>.Fail("El nombre de coleccion no es valido.", ConversionErrorCodes.InvalidCollection);

            if (string.Equals(normalized, ConversionStorageOptions.DefaultCollection, StringComparison.OrdinalIgnoreCase))
                return Result<string>.Fail("La coleccion general ya existe por defecto.", ConversionErrorCodes.InvalidCollection);

            if (await CollectionExistsAsync(normalized, cancellationToken).ConfigureAwait(false))
                return Result<string>.Fail(
                    $"La coleccion '{normalized}' ya existe.",
                    ConversionErrorCodes.CollectionExists);

            if (_registry is not null)
                await _registry.EnsureAsync(normalized, cancellationToken).ConfigureAwait(false);

            _observability.RecordSuccess("collection-create", normalized);
            return Result<string>.Ok(normalized);
        }

        public async Task<Result<string>> RenameCollectionAsync(string currentName, string newName, CancellationToken cancellationToken = default)
        {
            string oldNormalized = NormalizeCollection(currentName);
            string newNormalized = NormalizeCollection(newName);

            if (string.Equals(oldNormalized, ConversionStorageOptions.DefaultCollection, StringComparison.OrdinalIgnoreCase))
                return Result<string>.Fail("La coleccion general no se puede renombrar.", ConversionErrorCodes.InvalidCollection);

            if (string.IsNullOrWhiteSpace(newName) || newNormalized.Length == 0)
                return Result<string>.Fail("El nuevo nombre no es valido.", ConversionErrorCodes.InvalidCollection);

            var records = (await _gallery.ListAsync(cancellationToken).ConfigureAwait(false)).ToList();
            bool oldHasRecords = records.Any(x => string.Equals(NormalizeCollection(x.Collection), oldNormalized, StringComparison.OrdinalIgnoreCase));
            bool oldRegistered = _registry is not null &&
                (await _registry.LoadNamesAsync(cancellationToken).ConfigureAwait(false))
                .Contains(oldNormalized, StringComparer.OrdinalIgnoreCase);

            if (!oldHasRecords && !oldRegistered)
                return Result<string>.Fail(
                    $"La coleccion '{oldNormalized}' no existe.",
                    ConversionErrorCodes.CollectionNotFound);

            if (await CollectionExistsAsync(newNormalized, cancellationToken).ConfigureAwait(false))
                return Result<string>.Fail(
                    $"Ya existe una coleccion con el nombre '{newNormalized}'.",
                    ConversionErrorCodes.CollectionExists);

            foreach (ConversionRecord record in records)
            {
                if (string.Equals(NormalizeCollection(record.Collection), oldNormalized, StringComparison.OrdinalIgnoreCase))
                    record.Collection = newNormalized;
            }

            Result persist = await PersistRecordsAsync(records, cancellationToken).ConfigureAwait(false);
            if (!persist.IsSuccess)
                return Result<string>.Fail(persist.Error ?? "No se pudo actualizar el manifiesto.", persist.ErrorCode);

            if (_registry is not null)
            {
                if (!newNormalized.Equals(ConversionStorageOptions.DefaultCollection, StringComparison.OrdinalIgnoreCase))
                    await _registry.EnsureAsync(newNormalized, cancellationToken).ConfigureAwait(false);
                await _registry.RemoveAsync(oldNormalized, cancellationToken).ConfigureAwait(false);
            }

            _observability.RecordSuccess("collection-rename", $"{oldNormalized} -> {newNormalized}");
            return Result<string>.Ok(newNormalized);
        }

        private async Task<bool> CollectionExistsAsync(string normalized, CancellationToken cancellationToken = default)
        {
            var records = await _gallery.ListAsync(cancellationToken).ConfigureAwait(false);
            if (records.Any(x => string.Equals(NormalizeCollection(x.Collection), normalized, StringComparison.OrdinalIgnoreCase)))
                return true;

            if (_registry is null)
                return false;

            return (await _registry.LoadNamesAsync(cancellationToken).ConfigureAwait(false))
                .Contains(normalized, StringComparer.OrdinalIgnoreCase);
        }

        private async Task<Result> PersistRecordsAsync(List<ConversionRecord> records, CancellationToken cancellationToken)
        {
            if (_gallery is JsonConversionGalleryStore concrete)
                return await concrete.ReplaceAllAsync(records, cancellationToken).ConfigureAwait(false);

            foreach (ConversionRecord record in records)
                await _gallery.AddAsync(record, cancellationToken).ConfigureAwait(false);

            return Result.Ok();
        }

        private CollectionImageDto ToDto(ConversionRecord record)
        {
            string id = record.Id.ToString();
            return new CollectionImageDto
            {
                Id = record.Id,
                Collection = NormalizeCollection(record.Collection),
                OriginalFileName = record.OriginalFileName,
                CreatedAtUtc = record.CreatedAtUtc,
                Engine = record.Engine,
                State = record.State.ToString(),
                CorrelationId = record.CorrelationId,
                SucaHash = record.SucaHash,
                Input = new CollectionImageInputDto
                {
                    Format = string.IsNullOrWhiteSpace(record.InputFormat) ? FromContentType(record.OriginalContentType) : record.InputFormat,
                    ContentType = record.OriginalContentType,
                    Width = record.OriginalWidth,
                    Height = record.OriginalHeight,
                    Bytes = record.OriginalBytes,
                    Megapixels = Math.Round(record.OriginalWidth * record.OriginalHeight / 1_000_000.0, 3),
                    HasExif = record.InputHasExif,
                    LastWriteTimeUtc = TryGetLastWrite(record.Id, record.OriginalFile)
                },
                Output = new CollectionImageOutputDto
                {
                    Format = record.Format,
                    ContentType = record.ContentType,
                    Width = record.ConvertedWidth,
                    Height = record.ConvertedHeight,
                    Bytes = record.ConvertedBytes,
                    Quality = record.Quality,
                    HasAlpha = record.OutputHasAlpha,
                    BitsPerPixel = record.OutputBitsPerPixel,
                    CompressionRatio = record.OutputCompressionRatio,
                    DurationMs = record.DurationMs
                },
                Metrics = new CollectionImageMetricsDto
                {
                    SavingPercent = Math.Round(record.SavingPercent, 2),
                    CompressionLabel = record.OutputCompressionRatio > 0
                        ? $"{record.OutputCompressionRatio:F1}:1"
                        : ""
                },
                Links = new CollectionImageLinksDto
                {
                    File = $"/api/web/images/{id}/file?variant=converted",
                    Original = $"/api/web/images/{id}/file?variant=original",
                    Detail = $"/api/web/images/{id}"
                }
            };
        }

        private async Task<Result<ConversionRecord>> FillDirectResultAsync(
            ConversionRecord record,
            byte[] sourceBytes,
            string originalExtension,
            string stagingDir,
            string itemDir,
            ConversionTargetFormat format,
            CancellationToken cancellationToken)
        {
            using var source = new MemoryStream(sourceBytes, writable: false);
            var info = Image.Identify(source);
            if (info is null)
                return Result<ConversionRecord>.Fail("El archivo no es una imagen reconocida.", ConversionErrorCodes.UnsupportedImage);

            // ImageSharp 2.1 no expone DecodedImageFormat ni ImageFormatDetector: el
            // formato real del archivo se obtiene con Image.DetectFormat.
            IImageFormat? detected = Image.DetectFormat(sourceBytes);

            record.OriginalWidth = info.Width;
            record.OriginalHeight = info.Height;
            record.OriginalContentType = detected?.DefaultMimeType ?? "application/octet-stream";
            record.InputFormat = detected?.Name ?? "";
            record.InputHasExif = info.Metadata.ExifProfile is not null;

            source.Position = 0;
            var output = SucaFormatConverter.Convert(source, new ConversionRequest
            {
                Format = format,
                Quality = record.Quality,
                MaxWidth = record.MaxWidth,
                MaxHeight = record.MaxHeight,
                RemoveMetadata = record.RemoveMetadata
            }, cancellationToken);

            FillOutputMetrics(record, output.Bytes, output.Width, output.Height, format);
            await PersistFilesAsync(record, sourceBytes, originalExtension, output.Bytes, output.FileExtension, stagingDir, itemDir, cancellationToken).ConfigureAwait(false);
            return Result<ConversionRecord>.Ok(record);
        }

        private async Task<Result<ConversionRecord>> FillSucaResultAsync(
            ConversionRecord record,
            ConvertImageCommand command,
            byte[] sourceBytes,
            string originalExtension,
            string stagingDir,
            string itemDir,
            ConversionTargetFormat format,
            CancellationToken cancellationToken)
        {
            string sourcePath = Path.Combine(stagingDir, "source" + originalExtension);
            await File.WriteAllBytesAsync(sourcePath, sourceBytes, cancellationToken).ConfigureAwait(false);

            string stagingOut = Path.Combine(stagingDir, "out");
            Directory.CreateDirectory(stagingOut);

            var profile = command.Profile ?? new SucaProfile { A = 0, B = 1, C = 1, D = 3 };
            profile.Validate();

            // Sin opciones explicitas NO se aplica techo de peso: la escala la decide el
            // perfil/hash. El techo (peso mosca) es opt-in via pesoMoscaKbTarget.
            var options = command.Options ?? new SucaOptions { EnablePesoMosca = false };

            // La calidad pedida manda en el kernel: antes se ignoraba y el registro
            // mostraba una calidad que no se habia aplicado.
            if (command.Quality is >= 1 and <= 100)
                options.QualityOverride = command.Quality;
            options.PreferredFormat = format switch
            {
                ConversionTargetFormat.Jpeg => SucaOutputFormat.Jpeg,
                ConversionTargetFormat.Png => SucaOutputFormat.Png,
                _ => SucaOutputFormat.Webp
            };

            CompressionResult result = _kernel!.Process(sourcePath, stagingOut, profile, options, cancellationToken);
            byte[] convertedBytes = await File.ReadAllBytesAsync(result.OutputPath, cancellationToken).ConfigureAwait(false);

            using var convertedInfoStream = new MemoryStream(convertedBytes, writable: false);
            var info = Image.Identify(convertedInfoStream);

            using var originalInfoStream = new MemoryStream(sourceBytes, writable: false);
            var originalInfo = Image.Identify(originalInfoStream);
            if (originalInfo is null)
                return Result<ConversionRecord>.Fail("El archivo no es una imagen reconocida.", ConversionErrorCodes.UnsupportedImage);

            record.OriginalContentType = Image.DetectFormat(sourceBytes)?.DefaultMimeType ?? "application/octet-stream";
            record.InputFormat = Image.DetectFormat(sourceBytes)?.Name ?? "";
            record.InputHasExif = originalInfo.Metadata.ExifProfile is not null;
            record.OriginalWidth = originalInfo.Width;
            record.OriginalHeight = originalInfo.Height;
            record.ConvertedBytes = convertedBytes.LongLength;
            record.ConvertedWidth = info?.Width ?? result.Decision.NewWidth;
            record.ConvertedHeight = info?.Height ?? result.Decision.NewHeight;
            record.SavingPercent = result.SavingPercent;
            record.SucaHash = result.Decision.Hash;

            // Calidad REAL aplicada por el kernel (puede diferir de la pedida si el peso
            // mosca la bajo para cumplir el techo). Antes se guardaba la pedida y la UI
            // mostraba un valor que no correspondia al archivo.
            record.Quality = result.Decision.Quality;

            // El kernel genera el reporte de integridad perceptual (SSIM, PSNR,
            // retencion de bordes). Deja de ser un dato interno: viaja con el registro.
            if (result.Integrity is { } integrity)
            {
                record.IntegrityPsnr = Math.Round(integrity.Psnr, 2);
                record.IntegritySsim = Math.Round(integrity.SsimLite, 4);
                record.IntegrityEdgeRetention = Math.Round(integrity.EdgeRetention, 4);
                record.IntegrityHistogram = Math.Round(integrity.HistogramSimilarity, 4);
                record.IntegrityMode = integrity.Mode;
                record.IntegrityVerdict = integrity.Verdict;
                record.IntegrityBitwiseEqual = integrity.BitwiseEqual;
            }

            // Peso mosca: queda constancia de si el techo pedido se cumplio.
            record.PesoObjetivoKb = result.PesoObjetivoKb;
            record.PesoObjetivoCumplido = result.PesoObjetivoCumplido;
            record.PesoIteraciones = result.PesoIteraciones;
            record.Optimized = result.Optimized;
            record.OptimizedScale = result.OptimizedScale;
            record.OptimizedQuality = result.OptimizedQuality;
            record.CandidatesEvaluated = result.CandidatesEvaluated;
            record.QualityScore = result.QualityScore;
            record.LegibilityAcceptable = result.LegibilityAcceptable;

            FillOutputMetrics(record, convertedBytes, record.ConvertedWidth, record.ConvertedHeight, format);

            string extension = Path.GetExtension(result.OutputPath);
            if (string.IsNullOrWhiteSpace(extension))
                extension = SucaFormatConverter.ExtensionFor(format);

            await PersistFilesAsync(record, sourceBytes, originalExtension, convertedBytes, extension, stagingDir, itemDir, cancellationToken).ConfigureAwait(false);
            return Result<ConversionRecord>.Ok(record);
        }

        private static void FillOutputMetrics(ConversionRecord record, byte[] convertedBytes, int width, int height, ConversionTargetFormat format)
        {
            record.ConvertedBytes = convertedBytes.LongLength;
            record.ConvertedWidth = width;
            record.ConvertedHeight = height;
            record.OutputHasAlpha = format != ConversionTargetFormat.Jpeg;
            record.OutputBitsPerPixel = width > 0 && height > 0
                ? Math.Round(convertedBytes.LongLength * 8.0 / ((double)width * height), 3)
                : 0;
            record.OutputCompressionRatio = record.ConvertedBytes > 0
                ? Math.Round(record.OriginalBytes / (double)record.ConvertedBytes, 2)
                : 0;
            if (record.SavingPercent == 0 && record.OriginalBytes > 0)
            {
                record.SavingPercent = 100.0 - (record.ConvertedBytes / (double)record.OriginalBytes * 100.0);
            }
        }

        private async Task PersistFilesAsync(
            ConversionRecord record,
            byte[] sourceBytes,
            string originalExtension,
            byte[] convertedBytes,
            string convertedExtension,
            string stagingDir,
            string itemDir,
            CancellationToken cancellationToken)
        {
            string stagingOriginal = Path.Combine(stagingDir, "original" + originalExtension);
            string stagingConverted = Path.Combine(stagingDir, "converted" + convertedExtension);
            await File.WriteAllBytesAsync(stagingOriginal, sourceBytes, cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(stagingConverted, convertedBytes, cancellationToken).ConfigureAwait(false);

            record.OriginalFile = "original" + originalExtension;
            record.ConvertedFile = "converted" + convertedExtension;

            await _fileStore.CommitFileAsync(stagingOriginal, Path.Combine(itemDir, record.OriginalFile), cancellationToken).ConfigureAwait(false);
            await _fileStore.CommitFileAsync(stagingConverted, Path.Combine(itemDir, record.ConvertedFile), cancellationToken).ConfigureAwait(false);
        }

        private string ItemDirectory(Guid id) =>
            Path.Combine(Path.GetFullPath(_options.RootPath), ConversionStorageOptions.ImagesFolderName, id.ToString("N"));

        public async Task<Result<ConversionFilePath>> GetFilePathAsync(Guid id, string variant, CancellationToken cancellationToken = default)
        {
            ConversionRecord? record = await GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (record is null)
                return Result<ConversionFilePath>.Fail("No existe la imagen indicada.", ConversionErrorCodes.RecordNotFound);

            return Result<ConversionFilePath>.Ok(BuildFilePath(record, variant));
        }

        public async Task<IReadOnlyList<ConversionFilePath>> GetFilePathsAsync(IEnumerable<Guid> ids, string variant, CancellationToken cancellationToken = default)
        {
            var wanted = new HashSet<Guid>(ids ?? Enumerable.Empty<Guid>());
            var records = (await _gallery.ListAsync(cancellationToken).ConfigureAwait(false))
                .Where(x => wanted.Contains(x.Id))
                .ToList();

            return records.Select(x => BuildFilePath(x, variant)).ToList();
        }

        public async Task<IReadOnlyList<ConversionFilePath>> GetCollectionFilePathsAsync(string collection, string variant, CancellationToken cancellationToken = default)
        {
            string normalized = NormalizeCollection(collection);
            var records = (await _gallery.ListAsync(cancellationToken).ConfigureAwait(false))
                .Where(x => string.Equals(NormalizeCollection(x.Collection), normalized, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return records.Select(x => BuildFilePath(x, variant)).ToList();
        }

        public async Task<Result> RevealInExplorerAsync(Guid id, string variant, CancellationToken cancellationToken = default)
        {
            ConversionRecord? record = await GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (record is null)
                return Result.Fail("No existe la imagen indicada.", ConversionErrorCodes.RecordNotFound);

            ConversionFilePath info = BuildFilePath(record, variant);
            if (!info.Exists)
                return Result.Fail("El archivo no existe en disco.", ConversionErrorCodes.IoError);

            return OpenExplorerSelected(info.Path);
        }

        public Result RevealStorageRoot()
        {
            string root = Path.Combine(Path.GetFullPath(_options.RootPath), ConversionStorageOptions.ImagesFolderName);
            Directory.CreateDirectory(root);
            return OpenExplorerSelected(root);
        }

        /// <summary>
        /// Abre el Explorador de Windows. Intentos, en orden:
        /// 1) explorer.exe /select,"ruta" con shell execute (selecciona el archivo);
        /// 2) explorer.exe "carpeta" (abre la carpeta aunque no pueda seleccionar);
        /// 3) fallo con la ruta en el mensaje, para que la UI pueda copiarla.
        /// En sesiones sin escritorio interactivo (servicio, contenedor, sesion 0) no
        /// hay nada que abrir: se informa en vez de fingir exito.
        /// </summary>
        private Result OpenExplorerSelected(string path, string? variant = null)
        {
            if (string.IsNullOrWhiteSpace(path))
                return Result.Fail("No hay ruta para abrir.", ConversionErrorCodes.IoError);

            string fullPath = Path.GetFullPath(path);

            // 1) API nativa de Windows (la misma que "Mostrar en carpeta"): devuelve un
            // HRESULT fiable y no depende de la linea de comandos de explorer.exe.
            if (ShellReveal.TryReveal(fullPath))
            {
                _observability.RecordSuccess("reveal", $"shell: {variant ?? "archivo"}: {fullPath}", fullPath);
                return Result.Ok();
            }

            string? explorer = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

            if (!File.Exists(explorer))
                explorer = "explorer.exe";

            // 2) Respaldo: explorer.exe /select (util en equipos donde la API falla).
            Result? selected = TryStartExplorer(explorer, $"/select,\"{fullPath}\"");
            if (selected is not null)
            {
                _observability.RecordSuccess("reveal", $"{variant ?? "archivo"}: {fullPath}", fullPath);
                return Result.Ok();
            }

            // 3) Abrir la carpeta contenedora (suele funcionar aunque /select no).
            string? directory = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                Result? openedFolder = TryStartExplorer(explorer, $"\"{directory}\"");
                if (openedFolder is not null)
                {
                    _observability.RecordSuccess("reveal", $"carpeta (sin seleccionar): {directory}", directory);
                    return Result.Ok();
                }
            }

            _observability.RecordFailure("reveal", new InvalidOperationException("Sin escritorio interactivo"), fullPath);
            return Result.Fail(
                $"No se pudo abrir el Explorador en {fullPath}. " +
                "Comprueba que SUCA se ejecute en una sesion de escritorio con Explorer disponible " +
                "(no como servicio ni elevado); mientras tanto puedes copiar la ruta. " +
                "Diagnostico: GET /api/web/diagnostics/reveal",
                ConversionErrorCodes.IoError);
        }

        /// <summary>
        /// Arranca explorer.exe. Devuelve null si no se pudo lanzar.
        /// explorer.exe devuelve siempre codigo 1 aunque abra la ventana, asi que su
        /// codigo de salida no sirve como senal: solo cuenta que el proceso arranque.
        /// </summary>
        private Result? TryStartExplorer(string explorer, string arguments)
        {
            try
            {
                var info = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = explorer,
                    Arguments = arguments,
                    // El Explorador necesita shell execute: sin esto el proceso se
                    // lanza en la sesion del servidor y no aparece ninguna ventana.
                    UseShellExecute = true
                };

                return System.Diagnostics.Process.Start(info) is not null ? Result.Ok() : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Explorer launch failed ({Explorer} {Args}): {Message}", explorer, arguments, ex.Message);
                return null;
            }
        }

        private ConversionFilePath BuildFilePath(ConversionRecord record, string? variant)
        {
            bool wantConverted = !string.Equals(variant, "original", StringComparison.OrdinalIgnoreCase);
            string fileName = wantConverted ? record.ConvertedFile : record.OriginalFile;
            string fullPath = string.IsNullOrWhiteSpace(fileName)
                ? ""
                : Path.Combine(ItemDirectory(record.Id), fileName);

            return new ConversionFilePath
            {
                Id = record.Id,
                Variant = wantConverted ? "converted" : "original",
                Path = fullPath,
                Directory = Path.GetDirectoryName(fullPath) ?? "",
                FileName = Path.GetFileName(fullPath),
                Exists = !string.IsNullOrWhiteSpace(fullPath) && File.Exists(fullPath)
            };
        }

        private DateTimeOffset? TryGetLastWrite(Guid id, string fileName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fileName))
                    return null;

                string path = Path.Combine(ItemDirectory(id), fileName);
                return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
            }
            catch
            {
                return null;
            }
        }

        private static string FromContentType(string contentType) => contentType switch
        {
            "image/png" => "PNG",
            "image/jpeg" => "JPEG",
            "image/webp" => "WEBP",
            "image/gif" => "GIF",
            "image/bmp" => "BMP",
            "image/tiff" => "TIFF",
            _ => ""
        };

        public static string NormalizeCollection(string? collection)
        {
            string value = (collection ?? "").Trim();
            if (value.Length == 0)
                return ConversionStorageOptions.DefaultCollection;

            foreach (char c in Path.GetInvalidFileNameChars())
                value = value.Replace(c, '_');

            return value.ToLowerInvariant();
        }

        private static bool IsSucaEngine(string? engine) =>
            string.Equals((engine ?? "direct").Trim(), "suca", StringComparison.OrdinalIgnoreCase);

        private static async Task<byte[]> ReadAllBytes(Stream content, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
        }

        private static string SanitizeFileName(string fileName)
        {
            string name = Path.GetFileName(fileName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(name))
                name = "imagen";

            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            return name;
        }

        private static string BuildDownloadName(string originalName, string format, bool converted)
        {
            string baseName = Path.GetFileNameWithoutExtension(originalName);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "imagen";

            return converted ? $"{baseName}-suca.{format}" : originalName;
        }

        private static int? ClampNullable(int? value, int min, int max) =>
            value.HasValue ? Math.Clamp(value.Value, min, max) : null;

        private static int? NormalizeDimension(int? value) =>
            value.HasValue && value.Value > 0 ? Math.Min(value.Value, 20000) : null;

        private static long ElapsedMs(long startTimestamp) =>
            (long)((Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency);

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
