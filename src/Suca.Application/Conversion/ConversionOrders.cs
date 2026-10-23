using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SUCA_Compressor.Application.Common;

namespace SUCA_Compressor.Application.Conversion
{
    public sealed class ConversionOrderItemRequest
    {
        public string Path { get; set; } = "";
        public string? Format { get; set; }
        public int? Quality { get; set; }
        public int? MaxWidth { get; set; }
        public int? MaxHeight { get; set; }
        public string? Engine { get; set; }
        public string? Collection { get; set; }
    }

    public sealed class ConversionOrderDefaults
    {
        public string? Engine { get; set; }
        public string? Format { get; set; }
        public int? Quality { get; set; }
        public int? MaxWidth { get; set; }
        public int? MaxHeight { get; set; }
        public bool? RemoveMetadata { get; set; }
        public string? Collection { get; set; }
    }

    public sealed class ConversionOrderOutput
    {
        /// <summary>"gallery" keeps results in the runtime gallery; "directory" also copies converted files.</summary>
        public string Mode { get; set; } = "gallery";
        public string? Directory { get; set; }
    }

    public sealed class ConversionOrderRequest
    {
        public Guid? OrderId { get; set; }
        public string? CorrelationId { get; set; }
        public List<ConversionOrderItemRequest>? Items { get; set; }
        public string? SourceDirectory { get; set; }
        public bool Recursive { get; set; }
        public List<string>? Extensions { get; set; }
        public ConversionOrderDefaults? Defaults { get; set; }
        public ConversionOrderOutput? Output { get; set; }
        public int? Parallelism { get; set; }
        public string? Collection { get; set; }
    }

    public sealed class ConversionOrderItem
    {
        public Guid ItemId { get; set; } = Guid.NewGuid();
        public string Path { get; set; } = "";
        public string FileName { get; set; } = "";
        public ConversionJobState State { get; set; } = ConversionJobState.Pending;
        public Guid? RecordId { get; set; }
        public string? ErrorCode { get; set; }
        public string? Error { get; set; }
        public List<StateHistoryEntry> StateHistory { get; set; } = new();
        public long DurationMs { get; set; }
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
    }

    public sealed class ConversionOrder
    {
        public Guid OrderId { get; set; } = Guid.NewGuid();
        public string CorrelationId { get; set; } = "";
        public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
        public bool Paused { get; set; }
        public int Parallelism { get; set; } = 3;
        public string OutputMode { get; set; } = "gallery";
        public string? OutputDirectory { get; set; }
        public string? Collection { get; set; }
        public ConversionOrderDefaults Defaults { get; set; } = new();
        public List<ConversionOrderItem> Items { get; set; } = new();
    }

    public sealed class ConversionOrderSummary
    {
        public Guid OrderId { get; set; }
        public string CorrelationId { get; set; } = "";
        public string Status { get; set; } = "";
        public bool Paused { get; set; }
        public int TotalItems { get; set; }
        public int Completed { get; set; }
        public int Failed { get; set; }
        public int Cancelled { get; set; }
        public int Interrupted { get; set; }
        public int PendingCount { get; set; }
        public double ProgressPercent { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }

    public interface IConversionOrderOrchestrator
    {
        Task<Result<ConversionOrder>> SubmitAsync(ConversionOrderRequest request, CancellationToken cancellationToken = default);
        Task<Result<OrderPreview>> PreviewAsync(ConversionOrderRequest request);
        Task<ConversionOrder?> GetAsync(Guid orderId);
        Task<IReadOnlyList<ConversionOrderSummary>> ListAsync();
        Task<Result> PauseAsync(Guid orderId);
        Task<Result> ResumeAsync(Guid orderId);
        Task<Result> CancelAsync(Guid orderId);
        Task RecoverInterruptedAsync();
    }

    public sealed class OrderPreview
    {
        public string Source { get; set; } = "";
        public int ItemCount { get; set; }
        public long TotalBytes { get; set; }
        public List<string> FirstItems { get; set; } = new();
        public int Parallelism { get; set; }
    }

    public sealed class JsonConversionOrderStore
    {
        private readonly string _ordersRoot;
        private readonly IAtomicFileStore _fileStore;
        private readonly IAsyncKeyedLock _keyedLock;

        public JsonConversionOrderStore(ConversionStorageOptions options, IAtomicFileStore fileStore, IAsyncKeyedLock keyedLock)
        {
            if (options is null || string.IsNullOrWhiteSpace(options.RootPath))
                throw new ArgumentException("ConversionStorageOptions.RootPath es obligatorio.", nameof(options));

            _ordersRoot = Path.Combine(Path.GetFullPath(options.RootPath), "orders");
            _fileStore = fileStore;
            _keyedLock = keyedLock;
        }

        public string OrdersRoot => _ordersRoot;

        public async Task SaveAsync(ConversionOrder order, CancellationToken cancellationToken = default)
        {
            order.UpdatedAtUtc = DateTimeOffset.UtcNow;
            string path = OrderPath(order.OrderId);
            string json = JsonSerializer.Serialize(order, JsonDefaults.Indented);
            await _fileStore.WriteAllTextAtomicAsync(path, json, cancellationToken).ConfigureAwait(false);
        }

        public ConversionOrder? Load(Guid orderId)
        {
            string path = OrderPath(orderId);
            if (!File.Exists(path))
                return null;

            try
            {
                return JsonSerializer.Deserialize<ConversionOrder>(File.ReadAllText(path), JsonDefaults.Loose);
            }
            catch
            {
                return null;
            }
        }

        public IReadOnlyList<ConversionOrder> LoadAll()
        {
            if (!Directory.Exists(_ordersRoot))
                return Array.Empty<ConversionOrder>();

            var orders = new List<ConversionOrder>();
            foreach (string file in Directory.EnumerateFiles(_ordersRoot, "*.json"))
            {
                try
                {
                    var order = JsonSerializer.Deserialize<ConversionOrder>(File.ReadAllText(file), JsonDefaults.Loose);
                    if (order is not null)
                        orders.Add(order);
                }
                catch
                {
                }
            }

            return orders.OrderByDescending(x => x.CreatedAtUtc).ToList();
        }

        private string OrderPath(Guid orderId) => Path.Combine(_ordersRoot, orderId.ToString("N") + ".json");
    }

    public sealed class ConversionOrderOrchestrator : IConversionOrderOrchestrator
    {
        public const int MaxItemsPerOrder = 500;
        private static readonly string[] DefaultExtensions = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tiff" };

        private sealed class OrderRuntime
        {
            public ConversionOrder Order { get; init; } = new();
            public CancellationTokenSource Cancellation { get; } = new();
            public object Sync { get; } = new();
        }

        private readonly ConversionStorageOptions _options;
        private readonly JsonConversionOrderStore _store;
        private readonly IImageConversionService _conversion;
        private readonly IRuntimeObservability _observability;
        private readonly ILogger<ConversionOrderOrchestrator> _logger;
        private readonly ConcurrentDictionary<Guid, OrderRuntime> _runtimes = new();

        public ConversionOrderOrchestrator(
            ConversionStorageOptions options,
            JsonConversionOrderStore store,
            IImageConversionService conversion,
            IRuntimeObservability observability,
            ILogger<ConversionOrderOrchestrator> logger)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _conversion = conversion ?? throw new ArgumentNullException(nameof(conversion));
            _observability = observability ?? throw new ArgumentNullException(nameof(observability));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<Result<ConversionOrder>> SubmitAsync(ConversionOrderRequest request, CancellationToken cancellationToken = default)
        {
            if (request is null)
                return Result<ConversionOrder>.Fail("Falta la orden.", ConversionErrorCodes.Internal);

            int parallelism = request.Parallelism ?? 3;
            if (parallelism < 1 || parallelism > 16)
                return Result<ConversionOrder>.Fail("parallelism debe estar entre 1 y 16.", ConversionErrorCodes.InvalidParallelism);

            Result<List<ConversionOrderItem>> itemsOutcome = ResolveItems(request);
            if (!itemsOutcome.IsSuccess)
                return Result<ConversionOrder>.Fail(itemsOutcome.Error ?? "Orden invalida.", itemsOutcome.ErrorCode);

            List<ConversionOrderItem> items = itemsOutcome.Value;
            var defaults = request.Defaults ?? new ConversionOrderDefaults();
            var order = new ConversionOrder
            {
                OrderId = request.OrderId ?? Guid.NewGuid(),
                CorrelationId = string.IsNullOrWhiteSpace(request.CorrelationId)
                    ? Guid.NewGuid().ToString("N")
                    : request.CorrelationId!.Trim(),
                Parallelism = parallelism,
                OutputMode = string.Equals(request.Output?.Mode, "directory", StringComparison.OrdinalIgnoreCase)
                    ? "directory"
                    : "gallery",
                OutputDirectory = string.IsNullOrWhiteSpace(request.Output?.Directory)
                    ? null
                    : Path.GetFullPath(request.Output.Directory!.Trim()),
                Collection = ImageConversionService.NormalizeCollection(
                    !string.IsNullOrWhiteSpace(request.Collection)
                        ? request.Collection
                        : request.Defaults?.Collection),
                Defaults = defaults
            };

            if (order.OutputMode == "directory")
                Directory.CreateDirectory(order.OutputDirectory!);

            var runtime = new OrderRuntime { Order = order };
            order.Items = items;
            foreach (ConversionOrderItem item in order.Items)
            {
                item.State = ConversionJobState.Pending;
                item.StateHistory.Add(NewEntry(ConversionJobState.Pending, order.CreatedAtUtc));
            }

            _runtimes[order.OrderId] = runtime;
            await _store.SaveAsync(order, cancellationToken).ConfigureAwait(false);
            _observability.RecordSuccess("order:started", $"items={order.Items.Count}", order.CorrelationId);

            _ = Task.Run(() => DispatchAsync(runtime), CancellationToken.None);
            return Result<ConversionOrder>.Ok(order);
        }

        public Task<Result<OrderPreview>> PreviewAsync(ConversionOrderRequest request)
        {
            if (request is null)
                return Task.FromResult(Result<OrderPreview>.Fail("Falta la orden.", ConversionErrorCodes.Internal));

            int parallelism = request.Parallelism ?? 3;
            if (parallelism < 1 || parallelism > 16)
                return Task.FromResult(Result<OrderPreview>.Fail("parallelism debe estar entre 1 y 16.", ConversionErrorCodes.InvalidParallelism));

            Result<List<ConversionOrderItem>> itemsOutcome = ResolveItems(request);
            if (!itemsOutcome.IsSuccess)
                return Task.FromResult(Result<OrderPreview>.Fail(itemsOutcome.Error ?? "Orden invalida.", itemsOutcome.ErrorCode));

            List<ConversionOrderItem> items = itemsOutcome.Value;
            var preview = new OrderPreview
            {
                Source = string.IsNullOrWhiteSpace(request.SourceDirectory)
                    ? $"{items.Count} ruta(s) explicita(s)"
                    : Path.GetFullPath(request.SourceDirectory.Trim()),
                ItemCount = items.Count,
                TotalBytes = items.Sum(x => SafeFileLength(x.Path)),
                Parallelism = parallelism,
                FirstItems = items.Take(10).Select(x => x.FileName).ToList()
            };

            return Task.FromResult(Result<OrderPreview>.Ok(preview));
        }

        private Result<List<ConversionOrderItem>> ResolveItems(ConversionOrderRequest request)
        {
            var items = new List<ConversionOrderItem>();

            if (request.Items is { Count: > 0 })
            {
                foreach (var entry in request.Items)
                {
                    if (entry is null || string.IsNullOrWhiteSpace(entry.Path))
                        continue;

                    string fullPath = Path.GetFullPath(entry.Path.Trim());
                    if (!File.Exists(fullPath))
                        return Result<List<ConversionOrderItem>>.Fail(
                            $"La imagen no existe: {entry.Path}",
                            ConversionErrorCodes.PathNotFound);

                    items.Add(new ConversionOrderItem
                    {
                        Path = fullPath,
                        FileName = Path.GetFileName(fullPath)
                    });
                }
            }
            else if (!string.IsNullOrWhiteSpace(request.SourceDirectory))
            {
                if (!Directory.Exists(request.SourceDirectory))
                    return Result<List<ConversionOrderItem>>.Fail(
                        $"El directorio no existe: {request.SourceDirectory}",
                        ConversionErrorCodes.PathNotFound);

                IEnumerable<string> extensionSource = request.Extensions is { Count: > 0 }
                    ? request.Extensions
                    : DefaultExtensions;
                var extensions = extensionSource
                    .Select(x => x.StartsWith('.') ? x.ToLowerInvariant() : "." + x.ToLowerInvariant())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var searchOption = request.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                items = Directory.EnumerateFiles(request.SourceDirectory, "*.*", searchOption)
                    .Where(file => extensions.Contains(Path.GetExtension(file)))
                    .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                    .Select(file => new ConversionOrderItem { Path = Path.GetFullPath(file), FileName = Path.GetFileName(file) })
                    .ToList();
            }

            if (items.Count == 0)
                return Result<List<ConversionOrderItem>>.Fail(
                    "La orden no contiene imagenes (items vacio o directorio sin imagenes).",
                    ConversionErrorCodes.EmptyOrder);

            if (items.Count > MaxItemsPerOrder)
                return Result<List<ConversionOrderItem>>.Fail(
                    $"La orden excede el maximo de {MaxItemsPerOrder} imagenes.",
                    ConversionErrorCodes.TooManyItems);

            return Result<List<ConversionOrderItem>>.Ok(items);
        }

        private static long SafeFileLength(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }

        public Task<ConversionOrder?> GetAsync(Guid orderId)
        {
            if (_runtimes.TryGetValue(orderId, out OrderRuntime? runtime))
            {
                lock (runtime.Sync)
                    return Task.FromResult<ConversionOrder?>(CloneOrder(runtime.Order));
            }

            return Task.FromResult<ConversionOrder?>(_store.Load(orderId));
        }

        public Task<IReadOnlyList<ConversionOrderSummary>> ListAsync()
        {
            var summaries = _store.LoadAll().Select(ToSummary).ToList();
            return Task.FromResult<IReadOnlyList<ConversionOrderSummary>>(summaries);
        }

        public async Task<Result> PauseAsync(Guid orderId)
        {
            if (!_runtimes.TryGetValue(orderId, out OrderRuntime? runtime))
                return Result.Fail("La orden no esta activa en este servidor.", ConversionErrorCodes.OrderNotFound);

            bool changed = false;
            lock (runtime.Sync)
            {
                if (!runtime.Order.Paused)
                {
                    runtime.Order.Paused = true;
                    changed = true;
                }
            }

            if (changed)
            {
                await _store.SaveAsync(runtime.Order).ConfigureAwait(false);
                _observability.RecordSuccess("order:paused", runtime.Order.CorrelationId, runtime.Order.CorrelationId);
            }

            return Result.Ok();
        }

        public async Task<Result> ResumeAsync(Guid orderId)
        {
            if (!_runtimes.TryGetValue(orderId, out OrderRuntime? runtime))
                return Result.Fail("La orden no esta activa en este servidor.", ConversionErrorCodes.OrderNotFound);

            bool changed = false;
            lock (runtime.Sync)
            {
                if (runtime.Order.Paused)
                {
                    runtime.Order.Paused = false;
                    changed = true;
                }
            }

            if (changed)
            {
                await _store.SaveAsync(runtime.Order).ConfigureAwait(false);
                _observability.RecordSuccess("order:resumed", runtime.Order.CorrelationId, runtime.Order.CorrelationId);
            }

            return Result.Ok();
        }

        public async Task<Result> CancelAsync(Guid orderId)
        {
            if (!_runtimes.TryGetValue(orderId, out OrderRuntime? runtime))
                return Result.Fail("La orden no esta activa en este servidor.", ConversionErrorCodes.OrderNotFound);

            runtime.Cancellation.Cancel();
            _observability.RecordSuccess("order:cancel-requested", runtime.Order.CorrelationId, runtime.Order.CorrelationId);
            await Task.CompletedTask;
            return Result.Ok();
        }

        public async Task RecoverInterruptedAsync()
        {
            foreach (ConversionOrder order in _store.LoadAll())
            {
                bool changed = false;
                foreach (ConversionOrderItem item in order.Items.Where(x => !ConversionJobStateMachine.IsTerminal(x.State)))
                {
                    Transition(item, ConversionJobState.Interrupted, ConversionErrorCodes.ServerRestart,
                        "El servidor se reinicio con la conversion en curso.");
                    changed = true;
                }

                if (changed)
                {
                    await _store.SaveAsync(order).ConfigureAwait(false);
                    _logger.LogWarning("Order {OrderId} recovered with interrupted items after restart.", order.OrderId);
                }
            }
        }

        private async Task DispatchAsync(OrderRuntime runtime)
        {
            ConversionOrder order = runtime.Order;
            var gate = new SemaphoreSlim(Math.Max(1, order.Parallelism));

            try
            {
                foreach (ConversionOrderItem item in order.Items.ToList())
                {
                    bool stop;
                    lock (runtime.Sync)
                        stop = runtime.Cancellation.IsCancellationRequested;

                    if (stop)
                    {
                        await CancelRemainingAsync(runtime).ConfigureAwait(false);
                        return;
                    }

                    await WaitWhilePausedAsync(runtime).ConfigureAwait(false);

                    lock (runtime.Sync)
                    {
                        if (runtime.Cancellation.IsCancellationRequested)
                        {
                            stop = true;
                        }
                    }

                    if (stop)
                    {
                        await CancelRemainingAsync(runtime).ConfigureAwait(false);
                        return;
                    }

                    await gate.WaitAsync(runtime.Cancellation.Token).ConfigureAwait(false);

                    bool shouldRun;
                    lock (runtime.Sync)
                        shouldRun = item.State is ConversionJobState.Pending or ConversionJobState.Paused
                                    && !runtime.Cancellation.IsCancellationRequested;

                    if (!shouldRun)
                    {
                        gate.Release();
                        continue;
                    }

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await RunItemAsync(runtime, item, runtime.Cancellation.Token).ConfigureAwait(false);
                        }
                        finally
                        {
                            gate.Release();
                        }
                    }, CancellationToken.None);
                }

                await WaitForCompletionAsync(runtime).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await CancelRemainingAsync(runtime).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Order dispatcher failed for {OrderId}", order.OrderId);
            }
        }

        private async Task WaitWhilePausedAsync(OrderRuntime runtime)
        {
            while (true)
            {
                bool paused, cancelled;
                lock (runtime.Sync)
                {
                    paused = runtime.Order.Paused;
                    cancelled = runtime.Cancellation.IsCancellationRequested;
                }

                if (cancelled || !paused)
                    return;

                await Task.Delay(200, runtime.Cancellation.Token).ConfigureAwait(false);
            }
        }

        private async Task WaitForCompletionAsync(OrderRuntime runtime)
        {
            while (true)
            {
                bool busy, cancelled;
                lock (runtime.Sync)
                {
                    busy = runtime.Order.Items.Any(x => !ConversionJobStateMachine.IsTerminal(x.State));
                    cancelled = runtime.Cancellation.IsCancellationRequested;
                }

                if (!busy || cancelled)
                    return;

                await Task.Delay(150).ConfigureAwait(false);
            }
        }

        private async Task CancelRemainingAsync(OrderRuntime runtime)
        {
            bool changed = false;
            lock (runtime.Sync)
            {
                foreach (ConversionOrderItem item in runtime.Order.Items)
                {
                    if (ConversionJobStateMachine.IsTerminal(item.State))
                        continue;

                    if (item.State != ConversionJobState.Running)
                    {
                        Transition(item, ConversionJobState.Cancelled, ConversionErrorCodes.Cancelled, "Orden cancelada.");
                        changed = true;
                    }
                }
            }

            if (changed)
                await _store.SaveAsync(runtime.Order).ConfigureAwait(false);
        }

        private async Task RunItemAsync(OrderRuntime runtime, ConversionOrderItem item, CancellationToken orderToken)
        {
            ConversionOrder order = runtime.Order;
            using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(orderToken);

            lock (runtime.Sync)
            {
                Transition(item, ConversionJobState.Running, null, null);
                item.StartedAtUtc = DateTimeOffset.UtcNow;
            }

            await _store.SaveAsync(order).ConfigureAwait(false);

            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            string orderKey = order.OrderId.ToString("N")[..8];
            string correlation = string.IsNullOrEmpty(order.CorrelationId)
                ? item.ItemId.ToString("N")
                : $"{order.CorrelationId}.{item.ItemId.ToString("N")[..8]}";
            string collection = string.IsNullOrWhiteSpace(order.Collection)
                ? $"order-{orderKey}"
                : order.Collection;

            _observability.RecordStart("order-item", correlation);

            try
            {
                await using var stream = File.OpenRead(item.Path);
                var command = new ConvertImageCommand
                {
                    FileName = item.FileName,
                    Engine = order.Defaults.Engine ?? "suca",
                    Format = order.Defaults.Format ?? "webp",
                    Quality = order.Defaults.Quality,
                    MaxWidth = order.Defaults.MaxWidth,
                    MaxHeight = order.Defaults.MaxHeight,
                    RemoveMetadata = order.Defaults.RemoveMetadata ?? true,
                    Collection = collection,
                    CorrelationId = correlation
                };

                Result<ConversionRecord> outcome = await _conversion
                    .ConvertAsync(command, stream, itemCts.Token)
                    .ConfigureAwait(false);

                long elapsed = (long)((System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

                lock (runtime.Sync)
                {
                    item.DurationMs = elapsed;
                    if (outcome.IsSuccess)
                    {
                        item.RecordId = outcome.Value.Id;
                        Transition(item, ConversionJobState.Completed, null, null);
                        item.CompletedAtUtc = DateTimeOffset.UtcNow;
                    }
                    else
                    {
                        item.ErrorCode = outcome.ErrorCode;
                        item.Error = outcome.Error;
                        if (outcome.ErrorCode == ConversionErrorCodes.Cancelled)
                        {
                            Transition(item, ConversionJobState.Cancelling, outcome.ErrorCode, outcome.Error);
                            Transition(item, ConversionJobState.Cancelled, outcome.ErrorCode, outcome.Error);
                        }
                        else
                        {
                            Transition(item, ConversionJobState.Failed, outcome.ErrorCode, outcome.Error);
                        }

                        item.CompletedAtUtc = DateTimeOffset.UtcNow;
                    }
                }

                if (outcome.IsSuccess && order.OutputMode == "directory" && !string.IsNullOrWhiteSpace(order.OutputDirectory))
                    await CopyToOutputDirectoryAsync(order, outcome.Value).ConfigureAwait(false);

                await _store.SaveAsync(order).ConfigureAwait(false);
                if (outcome.IsSuccess)
                    _observability.RecordSuccess("order-item",
                        $"{item.FileName} -> {item.State} (gallery-record={item.RecordId})", correlation);
                else
                    _observability.RecordSuccess("order-item", $"{item.FileName} -> {item.State}", correlation);
            }
            catch (OperationCanceledException)
            {
                lock (runtime.Sync)
                {
                    item.ErrorCode = ConversionErrorCodes.Cancelled;
                    item.Error = "Conversion cancelada.";
                    Transition(item, ConversionJobState.Cancelling, ConversionErrorCodes.Cancelled, item.Error);
                    Transition(item, ConversionJobState.Cancelled, ConversionErrorCodes.Cancelled, item.Error);
                    item.CompletedAtUtc = DateTimeOffset.UtcNow;
                }

                await _store.SaveAsync(order).ConfigureAwait(false);
                _observability.RecordCancellation("order-item", correlation);
            }
            catch (Exception ex)
            {
                lock (runtime.Sync)
                {
                    item.ErrorCode = ConversionErrorCodes.IoError;
                    item.Error = ex.Message;
                    Transition(item, ConversionJobState.Failed, ConversionErrorCodes.IoError, ex.Message);
                    item.CompletedAtUtc = DateTimeOffset.UtcNow;
                }

                await _store.SaveAsync(order).ConfigureAwait(false);
                _observability.RecordFailure("order-item", ex, correlation);
                _logger.LogError(ex, "Order item failed: {Path}", item.Path);
            }
        }

        private async Task CopyToOutputDirectoryAsync(ConversionOrder order, ConversionRecord record)
        {
            ConversionFileContent? content = await _conversion
                .OpenFileAsync(record.Id, "converted")
                .ConfigureAwait(false);

            if (content is null)
                return;

            string target = Path.Combine(order.OutputDirectory!, content.DownloadName);
            await File.WriteAllBytesAsync(target, content.Bytes).ConfigureAwait(false);
        }

        private static void Transition(
            ConversionOrderItem item,
            ConversionJobState next,
            string? errorCode,
            string? error)
        {
            if (!ConversionJobStateMachine.CanTransition(item.State, next))
                return;

            if (errorCode is not null)
                item.ErrorCode = errorCode;

            if (error is not null)
                item.Error = error;

            item.State = next;
            item.StateHistory.Add(new StateHistoryEntry
            {
                State = next.ToString(),
                AtUtc = DateTimeOffset.UtcNow,
                ElapsedMs = item.StartedAtUtc is null
                    ? 0
                    : (long)(DateTimeOffset.UtcNow - item.StartedAtUtc.Value).TotalMilliseconds
            });
        }

        private static StateHistoryEntry NewEntry(ConversionJobState state, DateTimeOffset atUtc) => new()
        {
            State = state.ToString(),
            AtUtc = atUtc
        };

        private static ConversionOrder CloneOrder(ConversionOrder order) =>
            JsonSerializer.Deserialize<ConversionOrder>(JsonSerializer.Serialize(order, JsonDefaults.Default), JsonDefaults.Loose) ?? order;

        private static ConversionOrderSummary ToSummary(ConversionOrder order)
        {            int completed = order.Items.Count(x => x.State == ConversionJobState.Completed);
            int failed = order.Items.Count(x => x.State == ConversionJobState.Failed);
            int cancelled = order.Items.Count(x => x.State == ConversionJobState.Cancelled);
            int interrupted = order.Items.Count(x => x.State == ConversionJobState.Interrupted);
            int total = order.Items.Count;
            int terminal = completed + failed + cancelled + interrupted;

            string status;
            if (order.Items.All(x => x.State == ConversionJobState.Cancelled))
                status = "cancelled";
            else if (terminal == total)
                status = failed > 0 || interrupted > 0 ? "completedWithErrors" : "completed";
            else if (order.Paused)
                status = "paused";
            else
                status = "running";

            return new ConversionOrderSummary
            {
                OrderId = order.OrderId,
                CorrelationId = order.CorrelationId,
                Status = status,
                Paused = order.Paused,
                TotalItems = total,
                Completed = completed,
                Failed = failed,
                Cancelled = cancelled,
                Interrupted = interrupted,
                PendingCount = total - terminal,
                ProgressPercent = total == 0 ? 0 : Math.Round(terminal * 100.0 / total, 1),
                CreatedAtUtc = order.CreatedAtUtc,
                UpdatedAtUtc = order.UpdatedAtUtc
            };
        }
    }
}
