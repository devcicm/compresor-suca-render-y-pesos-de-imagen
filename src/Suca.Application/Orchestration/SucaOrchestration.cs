using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SUCA_Compressor.Application.Artifacts;
using SUCA_Compressor.Kernel;

namespace SUCA_Compressor
{
    public sealed class CompressionCommand
    {
        public string InputPath { get; set; } = "";
        public string OutputDirectory { get; set; } = "";
        public SucaProfile Profile { get; set; } = new();
        public SucaOptions Options { get; set; } = new();
        public CompressionArtifactsOptions? Artifacts { get; set; }
    }

    public sealed class OcrCommand
    {
        public string ImagePath { get; set; } = "";
    }

    public sealed class WeightMapCommand
    {
        public string ImagePath { get; set; } = "";
        public SucaProfile Profile { get; set; } = new();
    }

    public sealed class EmbeddingCommand
    {
        public string ImagePath { get; set; } = "";
    }

    public sealed class LocalVisualRagQueryCommand
    {
        public string IndexPath { get; set; } = "";
        public string ImagePath { get; set; } = "";
        public int TopK { get; set; } = 5;
    }

    public sealed class LmStudioRagIndexCommand
    {
        public CompressionCommand Compression { get; set; } = new();
        public LmStudioOptions LmStudio { get; set; } = new();
        public string SemanticIndexPath { get; set; } = "";
        public string? ExtraNotes { get; set; }
    }

    public sealed class LmStudioAskCommand
    {
        public LmStudioOptions LmStudio { get; set; } = new();
        public string SemanticIndexPath { get; set; } = "";
        public string Query { get; set; } = "";
        public int TopK { get; set; } = 4;
    }

    public sealed class LmStudioFolderIndexCommand
    {
        public LmStudioOptions LmStudio { get; set; } = new();
        public string SourceDirectory { get; set; } = "";
        public string SemanticIndexPath { get; set; } = "";
        public bool Recursive { get; set; } = true;
        public int ChunkSize { get; set; } = 1200;
        public int ChunkOverlap { get; set; } = 180;
        public string[]? Extensions { get; set; }
    }

    public sealed class LmStudioFolderAskCommand
    {
        public LmStudioOptions LmStudio { get; set; } = new();
        public string SourceDirectory { get; set; } = "";
        public string SemanticIndexPath { get; set; } = "";
        public string Query { get; set; } = "";
        public int TopK { get; set; } = 4;
        public bool RefreshIndexBeforeQuery { get; set; } = true;
        public bool Recursive { get; set; } = true;
        public int ChunkSize { get; set; } = 1200;
        public int ChunkOverlap { get; set; } = 180;
        public string[]? Extensions { get; set; }
    }

    public sealed class LmStudioVisionCommand
    {
        public LmStudioOptions LmStudio { get; set; } = new();
        public string ImagePath { get; set; } = "";
        public string? Prompt { get; set; }
    }

    public sealed class LmStudioInsightCommand
    {
        public CompressionCommand Compression { get; set; } = new();
        public LmStudioOptions LmStudio { get; set; } = new();
        public bool RunVision { get; set; } = true;
    }

    public sealed class WeightMapResponse
    {
        public int CellsX { get; set; }
        public int CellsY { get; set; }
        public double[][] Weights { get; set; } = Array.Empty<double[]>();
    }

    public sealed class OperationEvent
    {
        public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
        public string Name { get; set; } = "";
        public string Level { get; set; } = "info";
        public string Message { get; set; } = "";
        public string? CorrelationId { get; set; }
        public string? JobId { get; set; }
    }

    public sealed class RuntimeSnapshot
    {
        public DateTimeOffset StartedAtUtc { get; set; }
        public long TotalOperations { get; set; }
        public long SucceededOperations { get; set; }
        public long FailedOperations { get; set; }
        public long CancelledOperations { get; set; }
        public int ActiveJobs { get; set; }
        public int StoredEvents { get; set; }
    }

    public sealed class ManagedJobSnapshot
    {
        public Guid Id { get; set; }
        public string JobType { get; set; } = "";
        public string Status { get; set; } = "pending";
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public string? Error { get; set; }
        public object? Result { get; set; }
    }

    public interface IAsyncKeyedLock
    {
        ValueTask<IAsyncDisposable> AcquireAsync(string key, CancellationToken cancellationToken = default);
    }

    public interface IAtomicFileStore
    {
        Task CommitFileAsync(string stagingPath, string finalPath, CancellationToken cancellationToken = default);
        Task WriteAllTextAtomicAsync(string path, string contents, CancellationToken cancellationToken = default);
        Task AppendLineAsync(string path, string line, CancellationToken cancellationToken = default);
    }

    public interface ISucaCompressionCoordinator
    {
        Task<CompressionResult> ProcessAsync(CompressionCommand command, CancellationToken cancellationToken = default);
    }

    public interface ISucaBehaviorService
    {
        Task<VisualOcrFeatures> AnalyzeOcrAsync(OcrCommand command, CancellationToken cancellationToken = default);
        Task<WeightMapResponse> BuildWeightMapAsync(WeightMapCommand command, CancellationToken cancellationToken = default);
        Task<float[]> BuildEmbeddingAsync(EmbeddingCommand command, CancellationToken cancellationToken = default);
        Task<List<(RagItem Item, double Score)>> QueryLocalVisualRagAsync(LocalVisualRagQueryCommand command, CancellationToken cancellationToken = default);
    }

    public interface ILmStudioOrchestrator
    {
        Task<LmStudioRagHit> IndexCompressionAsync(LmStudioRagIndexCommand command, CancellationToken cancellationToken = default);
        Task<LmStudioRagAnswer> AskSemanticRagAsync(LmStudioAskCommand command, CancellationToken cancellationToken = default);
        Task<LmStudioFolderIndexResult> IndexFolderAsync(LmStudioFolderIndexCommand command, CancellationToken cancellationToken = default);
        Task<LmStudioRagAnswer> AskFolderAsync(LmStudioFolderAskCommand command, CancellationToken cancellationToken = default);
        Task<LmStudioVisionInsight> AnalyzeVisionAsync(LmStudioVisionCommand command, CancellationToken cancellationToken = default);
        Task<LmStudioSucaInsight> BuildInsightAsync(LmStudioInsightCommand command, CancellationToken cancellationToken = default);
    }

    public interface IRuntimeObservability
    {
        void RecordStart(string operation, string? correlationId = null);
        void RecordSuccess(string operation, string message, string? correlationId = null);
        void RecordFailure(string operation, Exception ex, string? correlationId = null);
        void RecordCancellation(string operation, string? correlationId = null);
        RuntimeSnapshot GetSnapshot(int activeJobs);
        IReadOnlyList<OperationEvent> GetRecentEvents();
    }

    public interface IManagedJobStore : IAsyncDisposable
    {
        ManagedJobSnapshot Enqueue(string jobType, Func<CancellationToken, Task<object?>> work);
        ManagedJobSnapshot? Get(Guid id);
        IReadOnlyList<ManagedJobSnapshot> List();
        bool Cancel(Guid id);
        Task CancelAllAsync();
    }

    public sealed class AsyncKeyedLock : IAsyncKeyedLock
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

        public async ValueTask<IAsyncDisposable> AcquireAsync(string key, CancellationToken cancellationToken = default)
        {
            var semaphore = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Releaser(semaphore);
        }

        private sealed class Releaser : IAsyncDisposable
        {
            private readonly SemaphoreSlim _semaphore;
            private int _released;

            public Releaser(SemaphoreSlim semaphore) => _semaphore = semaphore;

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                    _semaphore.Release();

                return ValueTask.CompletedTask;
            }
        }
    }

    public sealed class RuntimeObservability : IRuntimeObservability
    {
        private readonly object _sync = new();
        private readonly Queue<OperationEvent> _events = new();
        private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
        private long _total;
        private long _success;
        private long _failed;
        private long _cancelled;
        private const int MaxEvents = 200;

        public void RecordStart(string operation, string? correlationId = null)
        {
            Interlocked.Increment(ref _total);
            Enqueue("info", operation, "started", correlationId);
        }

        public void RecordSuccess(string operation, string message, string? correlationId = null)
        {
            Interlocked.Increment(ref _success);
            Enqueue("info", operation, message, correlationId);
        }

        public void RecordFailure(string operation, Exception ex, string? correlationId = null)
        {
            Interlocked.Increment(ref _failed);
            Enqueue("error", operation, ex.Message, correlationId);
        }

        public void RecordCancellation(string operation, string? correlationId = null)
        {
            Interlocked.Increment(ref _cancelled);
            Enqueue("warning", operation, "cancelled", correlationId);
        }

        public RuntimeSnapshot GetSnapshot(int activeJobs)
        {
            lock (_sync)
            {
                return new RuntimeSnapshot
                {
                    StartedAtUtc = _startedAtUtc,
                    TotalOperations = _total,
                    SucceededOperations = _success,
                    FailedOperations = _failed,
                    CancelledOperations = _cancelled,
                    ActiveJobs = activeJobs,
                    StoredEvents = _events.Count
                };
            }
        }

        public IReadOnlyList<OperationEvent> GetRecentEvents()
        {
            lock (_sync)
            {
                return _events.ToArray();
            }
        }

        private void Enqueue(string level, string operation, string message, string? correlationId)
        {
            lock (_sync)
            {
                _events.Enqueue(new OperationEvent
                {
                    Level = level,
                    Name = operation,
                    Message = message,
                    CorrelationId = correlationId
                });

                while (_events.Count > MaxEvents)
                    _events.Dequeue();
            }
        }
    }

    public sealed class AtomicFileStore : IAtomicFileStore
    {
        private readonly IAsyncKeyedLock _keyedLock;
        private readonly ILogger<AtomicFileStore> _logger;

        public AtomicFileStore(IAsyncKeyedLock keyedLock, ILogger<AtomicFileStore> logger)
        {
            _keyedLock = keyedLock;
            _logger = logger;
        }

        public async Task CommitFileAsync(string stagingPath, string finalPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(stagingPath)) throw new ArgumentException("stagingPath vacío.", nameof(stagingPath));
            if (string.IsNullOrWhiteSpace(finalPath)) throw new ArgumentException("finalPath vacío.", nameof(finalPath));

            string? dir = Path.GetDirectoryName(finalPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            await using var lease = await _keyedLock.AcquireAsync(NormalizeKey(finalPath), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(finalPath))
                throw new IOException($"El archivo final ya existe: {finalPath}");

            File.Move(stagingPath, finalPath);
            _logger.LogInformation("Atomic commit completed: {FinalPath}", finalPath);
        }

        public async Task WriteAllTextAtomicAsync(string path, string contents, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path vacío.", nameof(path));

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";

            await using var lease = await _keyedLock.AcquireAsync(NormalizeKey(path), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(tempPath, contents, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

            // File.Replace can transiently fail on Windows when another handle
            // (reader, indexer, antivirus) holds the destination open. Retry.
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                        File.Replace(tempPath, path, null);
                    else
                        File.Move(tempPath, path);
                    break;
                }
                catch (IOException) when (attempt < 5)
                {
                    await Task.Delay(40 * attempt, cancellationToken).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException) when (attempt < 5)
                {
                    await Task.Delay(40 * attempt, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        public async Task AppendLineAsync(string path, string line, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path vacío.", nameof(path));

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            await using var lease = await _keyedLock.AcquireAsync(NormalizeKey(path), cancellationToken).ConfigureAwait(false);
            await File.AppendAllTextAsync(path, line + Environment.NewLine, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }

        private static string NormalizeKey(string path) => Path.GetFullPath(path).ToUpperInvariant();
    }

    public sealed class ManagedJobStore : IManagedJobStore
    {
        private sealed class ManagedJobState
        {
            private readonly object _sync = new();
            public Guid Id { get; init; }
            public string JobType { get; init; } = "";
            public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
            public DateTimeOffset? StartedAtUtc { get; private set; }
            public DateTimeOffset? CompletedAtUtc { get; private set; }
            public string Status { get; private set; } = "pending";
            public string? Error { get; private set; }
            public object? Result { get; private set; }
            public CancellationTokenSource Cancellation { get; } = new();

            public void MarkStarted()
            {
                lock (_sync)
                {
                    Status = "running";
                    StartedAtUtc = DateTimeOffset.UtcNow;
                }
            }

            public void MarkCompleted(object? result)
            {
                lock (_sync)
                {
                    Status = "completed";
                    Result = result;
                    CompletedAtUtc = DateTimeOffset.UtcNow;
                }
            }

            public void MarkCancelled()
            {
                lock (_sync)
                {
                    Status = "cancelled";
                    CompletedAtUtc = DateTimeOffset.UtcNow;
                }
            }

            public void MarkFailed(Exception ex)
            {
                lock (_sync)
                {
                    Status = "failed";
                    Error = ex.ToString();
                    CompletedAtUtc = DateTimeOffset.UtcNow;
                }
            }

            public ManagedJobSnapshot Snapshot()
            {
                lock (_sync)
                {
                    return new ManagedJobSnapshot
                    {
                        Id = Id,
                        JobType = JobType,
                        Status = Status,
                        CreatedAtUtc = CreatedAtUtc,
                        StartedAtUtc = StartedAtUtc,
                        CompletedAtUtc = CompletedAtUtc,
                        Error = Error,
                        Result = Result
                    };
                }
            }
        }

        private readonly ConcurrentDictionary<Guid, ManagedJobState> _jobs = new();
        private readonly ILogger<ManagedJobStore> _logger;
        private readonly IRuntimeObservability _observability;

        public ManagedJobStore(ILogger<ManagedJobStore> logger, IRuntimeObservability observability)
        {
            _logger = logger;
            _observability = observability;
        }

        public ManagedJobSnapshot Enqueue(string jobType, Func<CancellationToken, Task<object?>> work)
        {
            if (string.IsNullOrWhiteSpace(jobType)) throw new ArgumentException("jobType vacío.", nameof(jobType));
            if (work is null) throw new ArgumentNullException(nameof(work));

            var state = new ManagedJobState
            {
                Id = Guid.NewGuid(),
                JobType = jobType
            };

            _jobs[state.Id] = state;

            _ = Task.Run(async () =>
            {
                state.MarkStarted();
                _observability.RecordStart($"job:{jobType}", state.Id.ToString("N"));
                try
                {
                    object? result = await work(state.Cancellation.Token).ConfigureAwait(false);
                    state.MarkCompleted(result);
                    _observability.RecordSuccess($"job:{jobType}", "completed", state.Id.ToString("N"));
                }
                catch (OperationCanceledException)
                {
                    state.MarkCancelled();
                    _observability.RecordCancellation($"job:{jobType}", state.Id.ToString("N"));
                }
                catch (Exception ex)
                {
                    state.MarkFailed(ex);
                    _observability.RecordFailure($"job:{jobType}", ex, state.Id.ToString("N"));
                    _logger.LogError(ex, "Background job {JobId} failed", state.Id);
                }
            });

            return state.Snapshot();
        }

        public ManagedJobSnapshot? Get(Guid id) => _jobs.TryGetValue(id, out var state) ? state.Snapshot() : null;

        public IReadOnlyList<ManagedJobSnapshot> List() =>
            _jobs.Values.OrderByDescending(x => x.CreatedAtUtc).Select(x => x.Snapshot()).ToList();

        public bool Cancel(Guid id)
        {
            if (!_jobs.TryGetValue(id, out var state))
                return false;

            state.Cancellation.Cancel();
            return true;
        }

        public async Task CancelAllAsync()
        {
            foreach (var job in _jobs.Values)
                job.Cancellation.Cancel();

            await Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            await CancelAllAsync().ConfigureAwait(false);

            foreach (var job in _jobs.Values)
                job.Cancellation.Dispose();
        }
    }

    public sealed class SucaCompressionCoordinator : ISucaCompressionCoordinator
    {
        private static readonly ActivitySource ActivitySource = new("SUCA.Orchestration");
        private readonly ISucaCompressionKernel _kernel;
        private readonly ICompressionArtifactsPublisher _artifactsPublisher;
        private readonly IAtomicFileStore _fileStore;
        private readonly IAsyncKeyedLock _keyedLock;
        private readonly ILogger<SucaCompressionCoordinator> _logger;
        private readonly IRuntimeObservability _observability;

        public SucaCompressionCoordinator(
            ISucaCompressionKernel kernel,
            ICompressionArtifactsPublisher artifactsPublisher,
            IAtomicFileStore fileStore,
            IAsyncKeyedLock keyedLock,
            ILogger<SucaCompressionCoordinator> logger,
            IRuntimeObservability observability)
        {
            _kernel = kernel;
            _artifactsPublisher = artifactsPublisher;
            _fileStore = fileStore;
            _keyedLock = keyedLock;
            _logger = logger;
            _observability = observability;
        }

        public async Task<CompressionResult> ProcessAsync(CompressionCommand command, CancellationToken cancellationToken = default)
        {
            ValidateCompressionCommand(command);

            using var activity = ActivitySource.StartActivity("suca.compress");
            string correlationId = activity?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
            _observability.RecordStart("compress", correlationId);

            string outputDir = Path.GetFullPath(command.OutputDirectory);
            string stagingDir = Path.Combine(outputDir, ".suca-staging", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDir);

            try
            {
                await using var outputLease = await _keyedLock.AcquireAsync(outputDir, cancellationToken).ConfigureAwait(false);

                var result = await Task.Run(
                    () => _kernel.Process(command.InputPath, stagingDir, command.Profile, command.Options, cancellationToken),
                    cancellationToken).ConfigureAwait(false);

                string extension = Path.GetExtension(result.OutputPath);
                string finalPath = FileNameUtil.BuildUniqueOutputPath(command.InputPath, outputDir, "suca", extension, result.Decision.Hash);
                await _fileStore.CommitFileAsync(result.OutputPath, finalPath, cancellationToken).ConfigureAwait(false);

                result.OutputPath = finalPath;
                var finalInfo = new FileInfo(finalPath);
                result.OutputBytes = finalInfo.Length;
                result.OutputKb = finalInfo.Length / 1024.0;

                await _artifactsPublisher.PublishAsync(command.Artifacts, result, cancellationToken).ConfigureAwait(false);

                _observability.RecordSuccess("compress", Path.GetFileName(finalPath), correlationId);
                return result;
            }
            catch (OperationCanceledException)
            {
                _observability.RecordCancellation("compress", correlationId);
                throw;
            }
            catch (Exception ex)
            {
                _observability.RecordFailure("compress", ex, correlationId);
                _logger.LogError(ex, "Compression orchestration failed for {InputPath}", command.InputPath);
                throw;
            }
            finally
            {
                TryDeleteDirectory(stagingDir);
            }
        }

        private static void ValidateCompressionCommand(CompressionCommand command)
        {
            if (command is null) throw new ArgumentNullException(nameof(command));
            if (string.IsNullOrWhiteSpace(command.InputPath)) throw new ArgumentException("InputPath vacío.");
            if (string.IsNullOrWhiteSpace(command.OutputDirectory)) throw new ArgumentException("OutputDirectory vacío.");
            if (!File.Exists(command.InputPath)) throw new FileNotFoundException("La imagen de entrada no existe.", command.InputPath);
        }

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

    public sealed class SucaBehaviorService : ISucaBehaviorService
    {
        private readonly ILogger<SucaBehaviorService> _logger;
        private readonly IRuntimeObservability _observability;

        public SucaBehaviorService(ILogger<SucaBehaviorService> logger, IRuntimeObservability observability)
        {
            _logger = logger;
            _observability = observability;
        }

        public async Task<VisualOcrFeatures> AnalyzeOcrAsync(OcrCommand command, CancellationToken cancellationToken = default)
        {
            if (command is null) throw new ArgumentNullException(nameof(command));
            if (string.IsNullOrWhiteSpace(command.ImagePath)) throw new ArgumentException("ImagePath vacío.");

            _observability.RecordStart("ocr");
            try
            {
                var result = await Task.Run(() => VisualOcrEngine.AnalyzeReadability(command.ImagePath, cancellationToken), cancellationToken).ConfigureAwait(false);
                _observability.RecordSuccess("ocr", result.ToString());
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _observability.RecordFailure("ocr", ex);
                _logger.LogError(ex, "OCR analysis failed for {ImagePath}", command.ImagePath);
                throw;
            }
        }

        public async Task<WeightMapResponse> BuildWeightMapAsync(WeightMapCommand command, CancellationToken cancellationToken = default)
        {
            if (command is null) throw new ArgumentNullException(nameof(command));
            if (string.IsNullOrWhiteSpace(command.ImagePath)) throw new ArgumentException("ImagePath vacío.");

            _observability.RecordStart("weight-map");
            try
            {
                var map = await Task.Run(() => SucaNeuralMapEngine.BuildWeightMap(command.ImagePath, command.Profile.ExtendedHash(), cancellationToken), cancellationToken).ConfigureAwait(false);
                var response = new WeightMapResponse
                {
                    CellsX = map.CellsX,
                    CellsY = map.CellsY,
                    Weights = ToJagged(map.Weights)
                };
                _observability.RecordSuccess("weight-map", $"{map.CellsX}x{map.CellsY}");
                return response;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _observability.RecordFailure("weight-map", ex);
                _logger.LogError(ex, "Weight map failed for {ImagePath}", command.ImagePath);
                throw;
            }
        }

        public async Task<float[]> BuildEmbeddingAsync(EmbeddingCommand command, CancellationToken cancellationToken = default)
        {
            if (command is null) throw new ArgumentNullException(nameof(command));
            if (string.IsNullOrWhiteSpace(command.ImagePath)) throw new ArgumentException("ImagePath vacío.");

            _observability.RecordStart("embedding");
            try
            {
                var result = await Task.Run(() => VisualEmbeddingEngine.BuildImageEmbedding(command.ImagePath, cancellationToken), cancellationToken).ConfigureAwait(false);
                _observability.RecordSuccess("embedding", $"dim={result.Length}");
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _observability.RecordFailure("embedding", ex);
                _logger.LogError(ex, "Embedding failed for {ImagePath}", command.ImagePath);
                throw;
            }
        }

        public async Task<List<(RagItem Item, double Score)>> QueryLocalVisualRagAsync(LocalVisualRagQueryCommand command, CancellationToken cancellationToken = default)
        {
            if (command is null) throw new ArgumentNullException(nameof(command));
            if (string.IsNullOrWhiteSpace(command.IndexPath)) throw new ArgumentException("IndexPath vacío.");
            if (string.IsNullOrWhiteSpace(command.ImagePath)) throw new ArgumentException("ImagePath vacío.");

            _observability.RecordStart("local-rag");
            try
            {
                return await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    float[] embedding = VisualEmbeddingEngine.BuildImageEmbedding(command.ImagePath, cancellationToken);
                    var idx = LocalRagIndex.Load(command.IndexPath);
                    var hits = idx.Search(embedding, command.TopK);
                    _observability.RecordSuccess("local-rag", $"hits={hits.Count}");
                    return hits;
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _observability.RecordFailure("local-rag", ex);
                _logger.LogError(ex, "Local RAG query failed for {ImagePath}", command.ImagePath);
                throw;
            }
        }

        private static double[][] ToJagged(double[,] matrix)
        {
            int rows = matrix.GetLength(0);
            int cols = matrix.GetLength(1);
            var jagged = new double[rows][];
            for (int y = 0; y < rows; y++)
            {
                jagged[y] = new double[cols];
                for (int x = 0; x < cols; x++)
                    jagged[y][x] = matrix[y, x];
            }
            return jagged;
        }
    }

    public sealed class LmStudioOrchestrator : ILmStudioOrchestrator
    {
        private readonly ISucaCompressionCoordinator _compressionCoordinator;
        private readonly ILogger<LmStudioOrchestrator> _logger;
        private readonly IRuntimeObservability _observability;

        public LmStudioOrchestrator(
            ISucaCompressionCoordinator compressionCoordinator,
            ILogger<LmStudioOrchestrator> logger,
            IRuntimeObservability observability)
        {
            _compressionCoordinator = compressionCoordinator;
            _logger = logger;
            _observability = observability;
        }

        public async Task<LmStudioRagHit> IndexCompressionAsync(LmStudioRagIndexCommand command, CancellationToken cancellationToken = default)
        {
            if (command is null) throw new ArgumentNullException(nameof(command));
            if (string.IsNullOrWhiteSpace(command.SemanticIndexPath)) throw new ArgumentException("SemanticIndexPath vacío.");

            _observability.RecordStart("lmstudio-index");
            try
            {
                var result = await _compressionCoordinator.ProcessAsync(command.Compression, cancellationToken).ConfigureAwait(false);
                var ocr = await Task.Run(() => VisualOcrEngine.AnalyzeReadability(result.OutputPath, cancellationToken), cancellationToken).ConfigureAwait(false);
                var map = await Task.Run(() => SucaNeuralMapEngine.BuildWeightMap(command.Compression.InputPath, command.Compression.Profile.ExtendedHash(), cancellationToken), cancellationToken).ConfigureAwait(false);

                using var client = new LmStudioClient(command.LmStudio);
                var item = await LmStudioSucaBridge.IndexCompressionResultAsync(
                    client,
                    command.SemanticIndexPath,
                    result,
                    ocr,
                    map,
                    command.ExtraNotes,
                    cancellationToken).ConfigureAwait(false);

                _observability.RecordSuccess("lmstudio-index", item.SourcePath);
                return item;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _observability.RecordFailure("lmstudio-index", ex);
                _logger.LogError(ex, "LM Studio semantic indexing failed");
                throw;
            }
        }

        public async Task<LmStudioRagAnswer> AskSemanticRagAsync(LmStudioAskCommand command, CancellationToken cancellationToken = default)
        {
            if (command is null) throw new ArgumentNullException(nameof(command));
            if (string.IsNullOrWhiteSpace(command.SemanticIndexPath)) throw new ArgumentException("SemanticIndexPath vacío.");
            if (string.IsNullOrWhiteSpace(command.Query)) throw new ArgumentException("Query vacía.");

            _observability.RecordStart("lmstudio-rag-ask");
            try
            {
                using var client = new LmStudioClient(command.LmStudio);
                var answer = await LmStudioSucaBridge.AskWithSemanticRagAsync(
                    client,
                    command.SemanticIndexPath,
                    command.Query,
                    command.TopK,
                    cancellationToken).ConfigureAwait(false);
                _observability.RecordSuccess("lmstudio-rag-ask", $"hits={answer.Contexts.Count}");
                return answer;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _observability.RecordFailure("lmstudio-rag-ask", ex);
                _logger.LogError(ex, "LM Studio semantic RAG query failed");
                throw;
            }
        }

        public async Task<LmStudioFolderIndexResult> IndexFolderAsync(LmStudioFolderIndexCommand command, CancellationToken cancellationToken = default)
        {
            if (command is null) throw new ArgumentNullException(nameof(command));
            if (string.IsNullOrWhiteSpace(command.SourceDirectory)) throw new ArgumentException("SourceDirectory vacío.");
            if (string.IsNullOrWhiteSpace(command.SemanticIndexPath)) throw new ArgumentException("SemanticIndexPath vacío.");

            _observability.RecordStart("lmstudio-folder-index");
            try
            {
                using var client = new LmStudioClient(command.LmStudio);
                var result = await LmStudioSucaBridge.IndexDirectoryAsync(
                    client,
                    command.SourceDirectory,
                    command.SemanticIndexPath,
                    command.Recursive,
                    command.ChunkSize,
                    command.ChunkOverlap,
                    command.Extensions,
                    cancellationToken).ConfigureAwait(false);
                _observability.RecordSuccess("lmstudio-folder-index", $"files={result.FileCount},chunks={result.ChunkCount}");
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _observability.RecordFailure("lmstudio-folder-index", ex);
                _logger.LogError(ex, "LM Studio folder indexing failed");
                throw;
            }
        }

        public async Task<LmStudioRagAnswer> AskFolderAsync(LmStudioFolderAskCommand command, CancellationToken cancellationToken = default)
        {
            if (command is null) throw new ArgumentNullException(nameof(command));
            if (string.IsNullOrWhiteSpace(command.SourceDirectory)) throw new ArgumentException("SourceDirectory vacío.");
            if (string.IsNullOrWhiteSpace(command.SemanticIndexPath)) throw new ArgumentException("SemanticIndexPath vacío.");
            if (string.IsNullOrWhiteSpace(command.Query)) throw new ArgumentException("Query vacía.");

            _observability.RecordStart("lmstudio-folder-ask");
            try
            {
                using var client = new LmStudioClient(command.LmStudio);

                if (command.RefreshIndexBeforeQuery || !File.Exists(command.SemanticIndexPath))
                {
                    await LmStudioSucaBridge.IndexDirectoryAsync(
                        client,
                        command.SourceDirectory,
                        command.SemanticIndexPath,
                        command.Recursive,
                        command.ChunkSize,
                        command.ChunkOverlap,
                        command.Extensions,
                        cancellationToken).ConfigureAwait(false);
                }

                var answer = await LmStudioSucaBridge.AskWithSemanticRagAsync(
                    client,
                    command.SemanticIndexPath,
                    command.Query,
                    command.TopK,
                    cancellationToken).ConfigureAwait(false);
                _observability.RecordSuccess("lmstudio-folder-ask", $"hits={answer.Contexts.Count}");
                return answer;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _observability.RecordFailure("lmstudio-folder-ask", ex);
                _logger.LogError(ex, "LM Studio folder ask failed");
                throw;
            }
        }

        public async Task<LmStudioVisionInsight> AnalyzeVisionAsync(LmStudioVisionCommand command, CancellationToken cancellationToken = default)
        {
            if (command is null) throw new ArgumentNullException(nameof(command));
            if (string.IsNullOrWhiteSpace(command.ImagePath)) throw new ArgumentException("ImagePath vacío.");

            _observability.RecordStart("lmstudio-vision");
            try
            {
                using var client = new LmStudioClient(command.LmStudio);
                var result = await LmStudioSucaBridge.AnalyzeImageAsync(client, command.ImagePath, userPrompt: command.Prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
                _observability.RecordSuccess("lmstudio-vision", command.ImagePath);
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _observability.RecordFailure("lmstudio-vision", ex);
                _logger.LogError(ex, "LM Studio vision analysis failed");
                throw;
            }
        }

        public async Task<LmStudioSucaInsight> BuildInsightAsync(LmStudioInsightCommand command, CancellationToken cancellationToken = default)
        {
            if (command is null) throw new ArgumentNullException(nameof(command));

            _observability.RecordStart("lmstudio-insight");
            try
            {
                var result = await _compressionCoordinator.ProcessAsync(command.Compression, cancellationToken).ConfigureAwait(false);
                using var client = new LmStudioClient(command.LmStudio);
                var insight = await LmStudioSucaBridge.BuildFullInsightAsync(
                    client,
                    result,
                    command.Compression.InputPath,
                    command.Compression.Profile,
                    command.RunVision,
                    cancellationToken).ConfigureAwait(false);
                _observability.RecordSuccess("lmstudio-insight", result.OutputPath);
                return insight;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _observability.RecordFailure("lmstudio-insight", ex);
                _logger.LogError(ex, "LM Studio full insight failed");
                throw;
            }
        }
    }

    public sealed class SucaLifecycleService : IHostedService
    {
        private readonly ILogger<SucaLifecycleService> _logger;
        private readonly IManagedJobStore _jobs;

        public SucaLifecycleService(ILogger<SucaLifecycleService> logger, IManagedJobStore jobs)
        {
            _logger = logger;
            _jobs = jobs;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("SUCA service lifecycle started");
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("SUCA service lifecycle stopping");
            await _jobs.CancelAllAsync().ConfigureAwait(false);
        }
    }
}
