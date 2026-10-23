using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http.Features;
using SUCA_Compressor.Application.Artifacts;
using SUCA_Compressor.Application.Common;
using SUCA_Compressor.Application.Conversion;
using SUCA_Compressor.Application.Settings;
using SUCA_Compressor.Kernel;

namespace SUCA_Compressor
{
    public static class SucaRestHost
    {
        public const string DefaultWebUrl = "http://127.0.0.1:5077";

        public static async Task RunAsync(string[] args, CancellationToken cancellationToken = default)
        {
            var builder = WebApplication.CreateBuilder(args);
            string? explicitUrls = TryReadOption(args, "--urls")
                ?? Environment.GetEnvironmentVariable("SUCA_URLS");
            string minichatUiPath = SucaRuntimePaths.ResolveWebUiRoot();
            string defaultRagSourceDirectory = Path.Combine(builder.Environment.ContentRootPath, "knowledge", "seed", "documents");
            string defaultRagIndexPath = Path.Combine(builder.Environment.ContentRootPath, "runtime", "indexes", "minichat-rag-index.json");
            string webStorageRoot = SucaRuntimePaths.DefaultWebStorageRoot();
            string settingsRoot = SucaRuntimePaths.DefaultSettingsRoot();

            if (!string.IsNullOrWhiteSpace(explicitUrls))
                builder.WebHost.UseUrls(explicitUrls);
            else if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])
                     && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
            {
                // Documented default so the startup banner always matches reality.
                builder.WebHost.UseUrls(DefaultWebUrl);
            }

            builder.Services.Configure<JsonOptions>(options =>
            {
                options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.SerializerOptions.WriteIndented = true;
            });

            builder.Services.AddHttpLogging(options =>
            {
                options.LoggingFields = HttpLoggingFields.RequestMethod |
                                        HttpLoggingFields.RequestPath |
                                        HttpLoggingFields.ResponseStatusCode |
                                        HttpLoggingFields.Duration;
            });

            builder.Logging.ClearProviders();
            builder.Logging.AddSimpleConsole(options =>
            {
                options.TimestampFormat = "HH:mm:ss ";
                options.SingleLine = true;
            });

            builder.Services.AddSingleton<IAsyncKeyedLock, AsyncKeyedLock>();
            builder.Services.AddSingleton<IRuntimeObservability, RuntimeObservability>();
            builder.Services.AddSingleton<IAtomicFileStore, AtomicFileStore>();
            builder.Services.AddSingleton<ISucaCompressionKernel, SucaCompressor>();
            builder.Services.AddSingleton<ICompressionArtifactsPublisher, CompressionArtifactsPublisher>();
            builder.Services.AddSingleton<ISucaCompressionCoordinator, SucaCompressionCoordinator>();
            builder.Services.AddSingleton<ISucaBehaviorService, SucaBehaviorService>();
            builder.Services.AddSingleton<ILmStudioOrchestrator, LmStudioOrchestrator>();
            builder.Services.AddSingleton<IReliableChatService, ReliableChatService>();
            builder.Services.AddSingleton<IManagedJobStore, ManagedJobStore>();
            builder.Services.AddSingleton<ISucaSettingsService>(sp => new JsonSucaSettingsService(
                settingsRoot,
                sp.GetRequiredService<IAtomicFileStore>(),
                sp.GetRequiredService<IAsyncKeyedLock>(),
                sp.GetRequiredService<IRuntimeObservability>(),
                sp.GetRequiredService<ILogger<JsonSucaSettingsService>>()));
            builder.Services.AddSingleton(new ConversionStorageOptions { RootPath = webStorageRoot });
            builder.Services.AddSingleton<IConversionGalleryStore, JsonConversionGalleryStore>();
            builder.Services.AddSingleton<CollectionsRegistryStore>();
            builder.Services.AddSingleton<IImageConversionService, ImageConversionService>();
            builder.Services.AddSingleton<JsonConversionOrderStore>();
            builder.Services.AddSingleton<IConversionOrderOrchestrator, ConversionOrderOrchestrator>();
            builder.Services.AddHostedService<SucaLifecycleService>();

            var app = builder.Build();

            app.Lifetime.ApplicationStarted.Register(() =>
            {
                var settings = app.Services.GetRequiredService<ISucaSettingsService>();
                PrintWebStartupBanner(app.Urls, settings.GetEffective());
            });

            await app.Services.GetRequiredService<IConversionOrderOrchestrator>()
                .RecoverInterruptedAsync()
                .ConfigureAwait(false);

            app.UseHttpLogging();
            app.UseExceptionHandler(errorApp =>
            {
                errorApp.Run(async context =>
                {
                    var feature = context.Features.Get<IExceptionHandlerFeature>();
                    var ex = feature?.Error;
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = ex?.Message ?? "Unhandled error",
                        detail = ex?.ToString()
                    }, cancellationToken);
                });
            });

            app.Use(async (context, next) =>
            {
                string correlationId = context.Request.Headers.TryGetValue("X-Correlation-Id", out var cid) && !string.IsNullOrWhiteSpace(cid)
                    ? cid.ToString()
                    : Guid.NewGuid().ToString("N");

                context.Response.Headers["X-Correlation-Id"] = correlationId;
                context.Items["CorrelationId"] = correlationId;

                using var scope = app.Logger.BeginScope(new Dictionary<string, object>
                {
                    ["CorrelationId"] = correlationId
                });

                await next();
            });

            if (Directory.Exists(minichatUiPath))
            {
                app.UseDefaultFiles(new DefaultFilesOptions
                {
                    FileProvider = new PhysicalFileProvider(minichatUiPath),
                    RequestPath = "/studio",
                    DefaultFileNames = { "index.html" }
                });
                app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(minichatUiPath),
                    RequestPath = "/studio"
                });
            }

            // The minichat lives inside the SUCA Studio shell now. A single route
            // pattern covers both "/minichat" and "/minichat/".
            app.MapGet("/minichat", () => Results.Redirect("/studio/chat.html", permanent: false));

            // Explicit studio entry point. Requesting "/studio" without a trailing
            // slash redirects to "/studio/" so relative asset URLs resolve under
            // the /studio prefix. The single route pattern covers both forms.
            string studioHtmlPath = Path.Combine(minichatUiPath, "index.html");
            app.MapGet("/studio", (HttpContext http) =>
            {
                if (!string.IsNullOrEmpty(http.Request.Path.Value) && !http.Request.Path.Value.EndsWith('/'))
                    return Results.Redirect("/studio/", permanent: false);

                return File.Exists(studioHtmlPath)
                    ? Results.File(studioHtmlPath, "text/html; charset=utf-8")
                    : Results.NotFound();
            });

            // Pretty URLs for the multipage shell: /studio/{page} serves the page.
            foreach (var (route, pageFile) in new[] {
                ("galeria", "galeria.html"),
                ("chat", "chat.html"),
                ("ajustes", "ajustes.html"),
                ("actividad", "actividad.html")
            })
            {
                string pagePath = Path.Combine(minichatUiPath, pageFile);
                app.MapGet($"/studio/{route}", () => File.Exists(pagePath)
                    ? Results.File(pagePath, "text/html; charset=utf-8")
                    : Results.NotFound());
            }

            app.MapGet("/health", (IRuntimeObservability obs, IManagedJobStore jobs) =>
            {
                var snapshot = obs.GetSnapshot(CountActiveJobs(jobs));
                return Results.Ok(new
                {
                    status = "ok",
                    utcNow = DateTimeOffset.UtcNow,
                    snapshot
                });
            });

            app.MapGet("/api/observability/snapshot", (IRuntimeObservability obs, IManagedJobStore jobs) =>
                Results.Ok(obs.GetSnapshot(CountActiveJobs(jobs))));

            app.MapGet("/api/observability/events", (IRuntimeObservability obs) =>
                Results.Ok(obs.GetRecentEvents()));

            app.MapGet("/api/jobs", (IManagedJobStore jobs) => Results.Ok(jobs.List()));

            app.MapGet("/api/lmstudio/models", async (CancellationToken ct) =>
            {
                var admin = new LmStudioModelAdmin(new LmStudioOptions());
                try
                {
                    return Results.Ok(await admin.ListDetailedModelsAsync(ct).ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    // LM Studio es un servicio externo: 503 explica mejor que un 500.
                    return Results.Json(
                        new { error = $"LM Studio no esta disponible: {ex.Message}", serviceUnavailable = true },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            });

            app.MapGet("/api/minichat/defaults", (ISucaSettingsService settings) =>
            {
                var effective = settings.GetEffective();
                return Results.Ok(new
                {
                    lmStudio = new
                    {
                        baseUrl = effective.LmStudio.BaseUrl ?? "http://127.0.0.1:1234/v1",
                        chatModel = effective.LmStudio.ChatModel ?? "deepseek/deepseek-r1-0528-qwen3-8b",
                        embeddingModel = effective.LmStudio.EmbeddingModel ?? "text-embedding-nomic-embed-text-v1.5",
                        temperature = 0,
                        maxTokens = 700
                    },
                    rag = new
                    {
                        sourceDirectory = effective.Rag.SourceDirectory ?? defaultRagSourceDirectory,
                        semanticIndexPath = effective.Rag.SemanticIndexPath ?? defaultRagIndexPath,
                        refreshIndexBeforeQuery = effective.Rag.RefreshIndexBeforeQuery ?? true,
                        recursive = effective.Rag.Recursive ?? true,
                        topK = effective.Rag.TopK ?? 4,
                        chunkSize = effective.Rag.ChunkSize ?? 1200,
                        chunkOverlap = effective.Rag.ChunkOverlap ?? 180
                    }
                });
            });

            app.MapGet("/api/jobs/{id:guid}", (Guid id, IManagedJobStore jobs) =>
            {
                var job = jobs.Get(id);
                return job is null ? Results.NotFound() : Results.Ok(job);
            });

            app.MapPost("/api/jobs/{id:guid}/cancel", (Guid id, IManagedJobStore jobs) =>
                jobs.Cancel(id) ? Results.Accepted($"/api/jobs/{id}") : Results.NotFound());

            app.MapPost("/api/tools/compress", async (CompressionCommand command, ISucaCompressionCoordinator service, HttpContext httpContext, CancellationToken ct) =>
            {
                var result = await service.ProcessAsync(command, LinkToken(httpContext, ct)).ConfigureAwait(false);
                return Results.Ok(result);
            });

            app.MapPost("/api/tools/compress/jobs", (CompressionCommand command, ISucaCompressionCoordinator service, IManagedJobStore jobs, HttpContext httpContext) =>
            {
                var job = jobs.Enqueue("compress", async ct =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, httpContext.RequestAborted);
                    return await service.ProcessAsync(command, linked.Token).ConfigureAwait(false);
                });

                return Results.Accepted($"/api/jobs/{job.Id}", job);
            });

            app.MapPost("/api/tools/ocr", async (OcrCommand command, ISucaBehaviorService service, HttpContext httpContext, CancellationToken ct) =>
            {
                var result = await service.AnalyzeOcrAsync(command, LinkToken(httpContext, ct)).ConfigureAwait(false);
                return Results.Ok(result);
            });

            app.MapPost("/api/tools/weight-map", async (WeightMapCommand command, ISucaBehaviorService service, HttpContext httpContext, CancellationToken ct) =>
            {
                var result = await service.BuildWeightMapAsync(command, LinkToken(httpContext, ct)).ConfigureAwait(false);
                return Results.Ok(result);
            });

            app.MapPost("/api/tools/embedding", async (EmbeddingCommand command, ISucaBehaviorService service, HttpContext httpContext, CancellationToken ct) =>
            {
                var result = await service.BuildEmbeddingAsync(command, LinkToken(httpContext, ct)).ConfigureAwait(false);
                return Results.Ok(new { embedding = result, dimension = result.Length });
            });

            app.MapPost("/api/tools/local-rag/query", async (LocalVisualRagQueryCommand command, ISucaBehaviorService service, HttpContext httpContext, CancellationToken ct) =>
            {
                var hits = await service.QueryLocalVisualRagAsync(command, LinkToken(httpContext, ct)).ConfigureAwait(false);
                return Results.Ok(hits.Select(x => new
                {
                    score = x.Score,
                    item = x.Item
                }));
            });

            app.MapPost("/api/tools/lmstudio/rag/index", async (LmStudioRagIndexCommand command, ILmStudioOrchestrator service, HttpContext httpContext, CancellationToken ct) =>
            {
                var result = await service.IndexCompressionAsync(command, LinkToken(httpContext, ct)).ConfigureAwait(false);
                return Results.Ok(result);
            });

            app.MapPost("/api/tools/lmstudio/rag/ask", async (LmStudioAskCommand command, ILmStudioOrchestrator service, HttpContext httpContext, CancellationToken ct) =>
            {
                var result = await service.AskSemanticRagAsync(command, LinkToken(httpContext, ct)).ConfigureAwait(false);
                return Results.Ok(ToLmStudioRagResponse(result));
            });

            app.MapPost("/api/tools/lmstudio/rag/index-folder", async (LmStudioFolderIndexCommand command, ILmStudioOrchestrator service, HttpContext httpContext, CancellationToken ct) =>
            {
                var result = await service.IndexFolderAsync(command, LinkToken(httpContext, ct)).ConfigureAwait(false);
                return Results.Ok(result);
            });

            app.MapPost("/api/tools/lmstudio/rag/ask-folder", async (LmStudioFolderAskCommand command, ILmStudioOrchestrator service, HttpContext httpContext, CancellationToken ct) =>
            {
                var result = await service.AskFolderAsync(command, LinkToken(httpContext, ct)).ConfigureAwait(false);
                return Results.Ok(ToLmStudioRagResponse(result));
            });

            app.MapPost("/api/tools/lmstudio/vision", async (LmStudioVisionCommand command, ILmStudioOrchestrator service, HttpContext httpContext, CancellationToken ct) =>
            {
                var result = await service.AnalyzeVisionAsync(command, LinkToken(httpContext, ct)).ConfigureAwait(false);
                return Results.Ok(ToLmStudioVisionResponse(result));
            });

            app.MapPost("/api/tools/lmstudio/insight", async (LmStudioInsightCommand command, ILmStudioOrchestrator service, HttpContext httpContext, CancellationToken ct) =>
            {
                var result = await service.BuildInsightAsync(command, LinkToken(httpContext, ct)).ConfigureAwait(false);
                return Results.Ok(ToLmStudioInsightResponse(result));
            });

            app.MapGet("/api/minichat/conversations", async (string? search, IReliableChatService chat, CancellationToken ct) =>
                Results.Ok(string.IsNullOrWhiteSpace(search)
                    ? await chat.ListConversationsAsync(ct).ConfigureAwait(false)
                    : await chat.SearchConversationsAsync(search, ct).ConfigureAwait(false)));

            app.MapGet("/api/minichat/conversations/{id}", async (string id, IReliableChatService chat, CancellationToken ct) =>
            {
                var conversation = await chat.GetConversationAsync(id, ct).ConfigureAwait(false);
                return conversation is null ? Results.NotFound() : Results.Ok(conversation);
            });

            app.MapPatch("/api/minichat/conversations/{id}", async (string id, JsonElement payload, IReliableChatService chat, CancellationToken ct) =>
            {
                string title = payload.TryGetProperty("title", out JsonElement titleElement)
                    ? titleElement.GetString() ?? ""
                    : "";

                var updated = await chat.RenameConversationAsync(id, title, ct).ConfigureAwait(false);
                return updated is null
                    ? Results.Json(
                        new { error = "No se pudo renombrar la conversacion.", errorCode = ConversionErrorCodes.RecordNotFound },
                        statusCode: StatusCodes.Status404NotFound)
                    : Results.Ok(new { conversationId = updated.ConversationId, title = updated.Title, updatedAtUtc = updated.LastUpdatedAtUtc });
            });

            app.MapDelete("/api/minichat/conversations/{id}", async (string id, IReliableChatService chat, CancellationToken ct) =>
                await chat.DeleteConversationAsync(id, ct).ConfigureAwait(false)
                    ? Results.NoContent()
                    : Results.NotFound());

            // Respuesta en streaming (SSE): un evento por linea con formato OpenAI-like.
            app.MapPost("/api/minichat/chat/stream", async (
                ChatCompletionRequest request,
                IReliableChatService chat,
                HttpContext httpContext,
                CancellationToken ct) =>
            {
                httpContext.Response.Headers.CacheControl = "no-cache";
                httpContext.Response.Headers["X-Accel-Buffering"] = "no";
                httpContext.Response.ContentType = "text/event-stream; charset=utf-8";

                CancellationToken token = LinkToken(httpContext, ct);

                await foreach (ChatStreamEvent evt in chat.StreamAsync(request, token).ConfigureAwait(false))
                {
                    string json = System.Text.Json.JsonSerializer.Serialize(evt, JsonDefaults.Loose);
                    await httpContext.Response.WriteAsync($"data: {json}\n\n", token).ConfigureAwait(false);
                    await httpContext.Response.Body.FlushAsync(token).ConfigureAwait(false);
                }

                await httpContext.Response.WriteAsync("data: [DONE]\n\n", CancellationToken.None).ConfigureAwait(false);
                return Results.Empty;
            });

            // Estado de LM Studio: la UI lo consulta para mostrar el banner de servicio caido.
            app.MapGet("/api/lmstudio/health", async (ISucaSettingsService settings, CancellationToken ct) =>
            {
                string baseUrl = (settings.GetEffective().LmStudio.BaseUrl ?? "http://127.0.0.1:1234/v1").TrimEnd('/');
                string modelsUrl = $"{baseUrl}/models";

                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                    using var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
                    using HttpResponseMessage response = await client.SendAsync(request, ct).ConfigureAwait(false);

                    if (response.IsSuccessStatusCode)
                    {
                        return Results.Ok(new { status = "available", baseUrl, modelsUrl });
                    }

                    return Results.Ok(new { status = "unavailable", baseUrl, modelsUrl, detail = $"HTTP {(int)response.StatusCode}" });
                }
                catch (Exception ex)
                {
                    return Results.Ok(new { status = "unavailable", baseUrl, modelsUrl, detail = ex.Message });
                }
            });

            app.MapPost("/api/minichat/messages/send", async (ReliableChatSendRequest request, IReliableChatService chat, HttpContext httpContext, CancellationToken ct) =>
            {
                var result = await chat.SendAsync(request, LinkToken(httpContext, ct)).ConfigureAwait(false);
                return Results.Ok(new
                {
                    conversationId = result.Conversation.ConversationId,
                    user = result.UserEnvelope,
                    assistant = result.AssistantEnvelope,
                    response = result.Response,
                    contexts = result.Contexts
                });
            });

            app.MapPost("/api/minichat/chat/completions", async (ChatCompletionRequest request, IReliableChatService chat, HttpContext httpContext, CancellationToken ct) =>
            {
                var result = await chat.CompleteAsync(request, LinkToken(httpContext, ct)).ConfigureAwait(false);
                return Results.Ok(result);
            });

            // ------------------------------------------------------------
            // SUCA Studio: conversion service, gallery and base64 retrieval.
            // ------------------------------------------------------------
            app.MapPost("/api/web/convert", async (HttpContext http, IImageConversionService conversion, ISucaSettingsService settings, CancellationToken ct) =>
            {
                var effective = settings.GetEffective();
                long maxBytes = Math.Max(1, effective.Web.MaxUploadMegabytes ?? 50) * 1024L * 1024L;

                var bodyFeature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (bodyFeature is { IsReadOnly: false })
                    bodyFeature.MaxRequestBodySize = maxBytes + (1024 * 1024);

                if (!http.Request.HasFormContentType)
                    return Results.BadRequest(new { error = "Se esperaba multipart/form-data con un campo 'file'." });

                IFormCollection form = await http.Request.ReadFormAsync(ct).ConfigureAwait(false);
                IFormFile? file = form.Files.FirstOrDefault();
                if (file is null || file.Length == 0)
                    return Results.BadRequest(new { error = "Falta el archivo. Envíelo en el campo 'file'." });

                if (file.Length > maxBytes)
                    return Results.Json(new { error = $"El archivo excede el límite de {effective.Web.MaxUploadMegabytes ?? 50} MB." }, statusCode: StatusCodes.Status413PayloadTooLarge);

                var command = new ConvertImageCommand
                {
                    FileName = string.IsNullOrWhiteSpace(file.FileName) ? "imagen" : file.FileName,
                    Engine = FormValue(form, "engine", effective.Conversion.Engine ?? "suca"),
                    Format = FormValue(form, "format", effective.Conversion.Format ?? "webp"),
                    Quality = FormInt(form, "quality") ?? effective.Conversion.Quality,
                    MaxWidth = FormInt(form, "maxWidth") ?? effective.Conversion.MaxWidth,
                    MaxHeight = FormInt(form, "maxHeight") ?? effective.Conversion.MaxHeight,
                    RemoveMetadata = FormBool(form, "removeMetadata", effective.Conversion.RemoveMetadata ?? true),
                    Collection = FormValue(form, "collection", ""),
                    CorrelationId = form["correlationId"].ToString()
                };

                int timeoutSeconds = Math.Max(5, effective.Web.ConvertTimeoutSeconds ?? 180);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(LinkToken(http, ct));
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

                await using Stream stream = file.OpenReadStream();
                Result<ConversionRecord> outcome = await conversion
                    .ConvertAsync(command, stream, timeoutCts.Token)
                    .ConfigureAwait(false);
                return MapConversionOutcome(outcome);
            });

            app.MapGet("/api/web/images", async (string? collection, int? page, int? pageSize, IImageConversionService conversion, CancellationToken ct) =>
            {
                var records = await conversion.ListAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(collection))
                {
                    string normalized = ImageConversionService.NormalizeCollection(collection);
                    records = records.Where(x => string.Equals(x.Collection, normalized, StringComparison.OrdinalIgnoreCase)).ToList();
                }

                var ordered = records
                    .OrderByDescending(x => x.CreatedAtUtc)
                    .ThenBy(x => x.OriginalFileName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                int total = ordered.Count;

                // Sin paginacion explicita se devuelve todo (comportamiento previo).
                if (page.HasValue || pageSize.HasValue)
                {
                    int size = Math.Clamp(pageSize ?? 24, 1, 200);
                    int index = Math.Max(1, page ?? 1);
                    ordered = ordered.Skip((index - 1) * size).Take(size).ToList();
                }

                return Results.Ok(new { total, page = page ?? 1, pageSize = pageSize ?? total, items = ordered });
            });

            app.MapGet("/api/web/images/{id:guid}", async (Guid id, IImageConversionService conversion, CancellationToken ct) =>
            {
                var record = await conversion.GetAsync(id, ct).ConfigureAwait(false);
                return record is null ? Results.NotFound() : Results.Ok(record);
            });

            app.MapGet("/api/web/images/{id:guid}/file", async (Guid id, string? variant, IImageConversionService conversion, CancellationToken ct) =>
            {
                var content = await conversion.OpenFileAsync(id, variant ?? "converted", ct).ConfigureAwait(false);
                return content is null
                    ? Results.NotFound()
                    : Results.File(content.Bytes, string.IsNullOrWhiteSpace(content.ContentType) ? "application/octet-stream" : content.ContentType, content.DownloadName);
            });

            app.MapGet("/api/web/images/{id:guid}/base64", async (Guid id, string? variant, IImageConversionService conversion, CancellationToken ct) =>
            {
                var content = await conversion.OpenFileAsync(id, variant ?? "converted", ct).ConfigureAwait(false);
                if (content is null)
                    return Results.NotFound();

                return Results.Ok(BuildBase64Payload(id, variant ?? "converted", content));
            });

            // Cancelacion real de una conversion en vuelo (por correlacion).
            app.MapPost("/api/web/convert/cancel", (JsonElement payload, IImageConversionService conversion) =>
            {
                string correlationId = payload.TryGetProperty("correlationId", out JsonElement correlationElement)
                    ? correlationElement.GetString() ?? ""
                    : "";
                Result<bool> outcome = conversion.CancelConversion(correlationId);
                if (!outcome.IsSuccess)
                    return Results.Json(
                        new { error = outcome.Error, errorCode = outcome.ErrorCode },
                        statusCode: MapErrorCodeToStatus(outcome.ErrorCode));

                return Results.Accepted("/api/web/convert", new { status = "cancel-requested", correlationId = correlationId.Trim() });
            });

            // ZIP con la seleccion de la galeria.
            app.MapPost("/api/web/images/export", async (JsonElement payload, IImageConversionService conversion, CancellationToken ct) =>
            {
                List<Guid> ids = ReadGuidList(payload, "ids");
                string variant = payload.TryGetProperty("variant", out JsonElement variantElement)
                    ? variantElement.GetString() ?? "converted"
                    : "converted";

                Result<ConversionFileContent> outcome = await conversion
                    .ExportArchiveAsync(ids, variant, ct)
                    .ConfigureAwait(false);

                if (!outcome.IsSuccess)
                    return Results.Json(
                        new { error = outcome.Error, errorCode = outcome.ErrorCode },
                        statusCode: MapErrorCodeToStatus(outcome.ErrorCode));

                ConversionFileContent archive = outcome.Value;
                return Results.File(archive.Bytes, archive.ContentType, archive.DownloadName);
            });

            // Mover varias imagenes a una coleccion (accion en lote de la galeria).
            app.MapPost("/api/web/images/collection", async (JsonElement payload, IImageConversionService conversion, CancellationToken ct) =>
            {
                List<Guid> ids = ReadGuidList(payload, "ids");
                string name = payload.TryGetProperty("collection", out JsonElement collectionElement)
                    ? collectionElement.GetString() ?? ""
                    : "";

                Result<int> outcome = await conversion
                    .SetCollectionForManyAsync(ids, name, ct)
                    .ConfigureAwait(false);

                return outcome.IsSuccess
                    ? Results.Ok(new { status = "moved", moved = outcome.Value, collection = ImageConversionService.NormalizeCollection(name) })
                    : Results.Json(
                        new { error = outcome.Error, errorCode = outcome.ErrorCode },
                        statusCode: MapErrorCodeToStatus(outcome.ErrorCode));
            });

            app.MapDelete("/api/web/images/{id:guid}", async (Guid id, IImageConversionService conversion, CancellationToken ct) =>
                await conversion.DeleteAsync(id, ct).ConfigureAwait(false) ? Results.NoContent() : Results.NotFound());

            // ------------------------------------------------------------
            // File paths on disk + open folder in Explorer (local desktop).
            // ------------------------------------------------------------
            app.MapGet("/api/web/images/{id:guid}/path", async (Guid id, string? variant, IImageConversionService conversion, CancellationToken ct) =>
            {
                Result<ConversionFilePath> outcome = await conversion
                    .GetFilePathAsync(id, variant ?? "converted", ct)
                    .ConfigureAwait(false);

                return outcome.IsSuccess
                    ? Results.Ok(outcome.Value)
                    : Results.Json(new { error = outcome.Error, errorCode = outcome.ErrorCode }, statusCode: MapErrorCodeToStatus(outcome.ErrorCode));
            });

            app.MapPost("/api/web/images/paths", async (JsonElement payload, IImageConversionService conversion, CancellationToken ct) =>
            {
                var ids = new List<Guid>();
                if (payload.TryGetProperty("ids", out JsonElement idsElement) && idsElement.ValueKind is JsonValueKind.Array)
                {
                    foreach (JsonElement entry in idsElement.EnumerateArray())
                        if (Guid.TryParse(entry.GetString(), out Guid parsed))
                            ids.Add(parsed);
                }

                string variant = payload.TryGetProperty("variant", out JsonElement variantElement)
                    ? variantElement.GetString() ?? "converted"
                    : "converted";

                var paths = await conversion.GetFilePathsAsync(ids, variant, ct).ConfigureAwait(false);
                return Results.Ok(new { total = paths.Count, items = paths });
            });

            app.MapPost("/api/web/images/{id:guid}/reveal", async (Guid id, string? variant, IImageConversionService conversion) =>
            {
                Result outcome = await conversion.RevealInExplorerAsync(id, variant ?? "converted").ConfigureAwait(false);
                return outcome.IsSuccess
                    ? Results.Ok(new { status = "revealed", variant = (variant ?? "converted").ToLowerInvariant() })
                    : Results.Json(new { error = outcome.Error, errorCode = outcome.ErrorCode }, statusCode: MapErrorCodeToStatus(outcome.ErrorCode));
            });

            app.MapPost("/api/web/storage/reveal", (IImageConversionService conversion) =>
            {
                Result outcome = conversion.RevealStorageRoot();
                return outcome.IsSuccess
                    ? Results.Ok(new { status = "revealed" })
                    : Results.Json(new { error = outcome.Error, errorCode = outcome.ErrorCode }, statusCode: MapErrorCodeToStatus(outcome.ErrorCode));
            });

            app.MapGet("/api/web/collections/{name}/paths", async (string name, string? variant, IImageConversionService conversion, CancellationToken ct) =>
            {
                var paths = await conversion
                    .GetCollectionFilePathsAsync(name, variant ?? "converted", ct)
                    .ConfigureAwait(false);
                return Results.Ok(new { collection = ImageConversionService.NormalizeCollection(name), total = paths.Count, items = paths });
            });

            // ------------------------------------------------------------
            // Gallery collections: categories with enriched JSON retrieval.
            // ------------------------------------------------------------
            app.MapGet("/api/web/collections", async (IImageConversionService conversion, CancellationToken ct) =>
                Results.Ok(await conversion.ListCollectionsAsync(ct).ConfigureAwait(false)));

            app.MapPost("/api/web/collections", async (JsonElement payload, IImageConversionService conversion, CancellationToken ct) =>
            {
                string name = payload.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() ?? "" : "";
                Result<string> outcome = await conversion.CreateCollectionAsync(name, ct).ConfigureAwait(false);
                return outcome.IsSuccess
                    ? Results.Created($"/api/web/collections/{outcome.Value}/images", new { collection = outcome.Value, total = 0 })
                    : Results.Json(new { error = outcome.Error, errorCode = outcome.ErrorCode }, statusCode: MapErrorCodeToStatus(outcome.ErrorCode));
            });

            app.MapPut("/api/web/collections/{name}", async (string name, JsonElement payload, IImageConversionService conversion, CancellationToken ct) =>
            {
                string newName = payload.TryGetProperty("newName", out JsonElement newNameElement) ? newNameElement.GetString() ?? "" : "";
                Result<string> outcome = await conversion.RenameCollectionAsync(name, newName, ct).ConfigureAwait(false);
                return outcome.IsSuccess
                    ? Results.Ok(new { status = "renamed", from = ImageConversionService.NormalizeCollection(name), to = outcome.Value })
                    : Results.Json(new { error = outcome.Error, errorCode = outcome.ErrorCode }, statusCode: MapErrorCodeToStatus(outcome.ErrorCode));
            });

            // Explorador de carpetas del servidor (herramienta local de escritorio).
            app.MapGet("/api/web/folders", (string? path) =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        var drives = DriveInfo.GetDrives()
                            .Where(d => d.IsReady)
                            .Select(d => new { name = d.Name, path = d.Name, imageCount = (int?)null })
                            .ToList();

                        return Results.Ok(new { mode = "drives", path = "", parent = (string?)null, items = drives });
                    }

                    string full = Path.GetFullPath(path);
                    if (!Directory.Exists(full))
                        return Results.Json(
                            new { error = $"La carpeta no existe: {full}", errorCode = ConversionErrorCodes.PathNotFound },
                            statusCode: MapErrorCodeToStatus(ConversionErrorCodes.PathNotFound));

                    var knownExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tiff" };

                    int CountImages(string directory)
                    {
                        try
                        {
                            return Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
                                .Count(file => knownExtensions.Contains(Path.GetExtension(file)));
                        }
                        catch
                        {
                            return 0;
                        }
                    }

                    var children = Directory.EnumerateDirectories(full)
                        .Select(dir => new { name = Path.GetFileName(dir), path = dir, imageCount = (int?)CountImages(dir) })
                        .OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var parent = Directory.GetParent(full)?.FullName;

                    return Results.Ok(new { mode = "browse", path = full, parent, items = children });
                }
                catch (Exception ex)
                {
                    return Results.Json(
                        new { error = ex.Message, errorCode = ConversionErrorCodes.IoError },
                        statusCode: MapErrorCodeToStatus(ConversionErrorCodes.IoError));
                }
            });

            app.MapPost("/api/web/orders/preview", async (ConversionOrderRequest request, IConversionOrderOrchestrator orchestrator) =>
            {
                Result<OrderPreview> outcome = await orchestrator.PreviewAsync(request).ConfigureAwait(false);
                return outcome.IsSuccess
                    ? Results.Ok(outcome.Value)
                    : Results.Json(new { error = outcome.Error, errorCode = outcome.ErrorCode }, statusCode: MapErrorCodeToStatus(outcome.ErrorCode));
            });

            app.MapGet("/api/web/collections/{name}/images", async (
                string name,
                bool? includeBase64,
                bool? originalBase64,
                int? page,
                int? pageSize,
                IImageConversionService conversion,
                ISucaSettingsService settings,
                CancellationToken ct) =>
            {
                var effective = settings.GetEffective();
                var request = new CollectionImagesRequest
                {
                    Page = page ?? 1,
                    PageSize = pageSize ?? effective.Web.GalleryPageSize ?? 24,
                    IncludeBase64 = includeBase64 ?? true,
                    IncludeOriginalBase64 = originalBase64 ?? false
                };

                Result<CollectionImagesResult> outcome = await conversion
                    .GetCollectionAsync(name, request, ct)
                    .ConfigureAwait(false);

                return outcome.IsSuccess
                    ? Results.Ok(outcome.Value)
                    : Results.Json(
                        new { error = outcome.Error, errorCode = outcome.ErrorCode },
                        statusCode: MapErrorCodeToStatus(outcome.ErrorCode));
            });

            app.MapPut("/api/web/images/{id:guid}/collection", async (
                Guid id,
                JsonElement payload,
                IImageConversionService conversion,
                CancellationToken ct) =>
            {
                string collection = payload.TryGetProperty("collection", out JsonElement element)
                    ? element.GetString() ?? ""
                    : "";

                Result outcome = await conversion.SetCollectionAsync(id, collection, ct).ConfigureAwait(false);
                return outcome.IsSuccess
                    ? Results.Ok(new { status = "moved", collection = ImageConversionService.NormalizeCollection(collection) })
                    : Results.Json(
                        new { error = outcome.Error, errorCode = outcome.ErrorCode },
                        statusCode: MapErrorCodeToStatus(outcome.ErrorCode));
            });

            app.MapDelete("/api/web/collections/{name}", async (
                string name,
                bool confirm,
                IImageConversionService conversion,
                CancellationToken ct) =>
            {
                if (!confirm)
                    return Results.BadRequest(new { error = "Confirme con ?confirm=true. Los archivos no se borran; las imagenes pasan a la coleccion general." });

                Result<int> outcome = await conversion.DissolveCollectionAsync(name, ct).ConfigureAwait(false);
                return outcome.IsSuccess
                    ? Results.Ok(new { status = "dissolved", moved = outcome.Value, collection = "general" })
                    : Results.Json(
                        new { error = outcome.Error, errorCode = outcome.ErrorCode },
                        statusCode: MapErrorCodeToStatus(outcome.ErrorCode));
            });

            // ------------------------------------------------------------
            // Orchestrated conversion orders (JSON batch for services / AI).
            // ------------------------------------------------------------
            app.MapPost("/api/web/orders", async (ConversionOrderRequest request, IConversionOrderOrchestrator orchestrator, CancellationToken ct) =>
            {
                Result<ConversionOrder> outcome = await orchestrator.SubmitAsync(request, ct).ConfigureAwait(false);
                if (!outcome.IsSuccess)
                    return Results.Json(
                        new { error = outcome.Error, errorCode = outcome.ErrorCode },
                        statusCode: MapErrorCodeToStatus(outcome.ErrorCode));

                ConversionOrder order = outcome.Value;
                return Results.Accepted(
                    $"/api/web/orders/{order.OrderId}",
                    new { orderId = order.OrderId, correlationId = order.CorrelationId, statusUrl = $"/api/web/orders/{order.OrderId}", totalItems = order.Items.Count });
            });

            app.MapGet("/api/web/orders", async (IConversionOrderOrchestrator orchestrator) =>
                Results.Ok(await orchestrator.ListAsync().ConfigureAwait(false)));

            app.MapGet("/api/web/orders/{id:guid}", async (Guid id, IConversionOrderOrchestrator orchestrator) =>
            {
                var order = await orchestrator.GetAsync(id).ConfigureAwait(false);
                return order is null ? Results.NotFound() : Results.Ok(order);
            });

            app.MapGet("/api/web/orders/{id:guid}/events", async (Guid id, IConversionOrderOrchestrator orchestrator, IRuntimeObservability obs) =>
            {
                var order = await orchestrator.GetAsync(id).ConfigureAwait(false);
                if (order is null)
                    return Results.NotFound();

                var events = obs.GetRecentEvents()
                    .Where(x => string.Equals(x.CorrelationId, order.CorrelationId, StringComparison.OrdinalIgnoreCase)
                                || (x.Message ?? "").Contains(order.CorrelationId, StringComparison.OrdinalIgnoreCase)
                                || order.Items.Any(i => (x.CorrelationId ?? "").StartsWith(order.CorrelationId + ".", StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                return Results.Ok(new { orderId = order.OrderId, correlationId = order.CorrelationId, events });
            });

            app.MapPost("/api/web/orders/{id:guid}/pause", async (Guid id, IConversionOrderOrchestrator orchestrator) =>
            {
                Result outcome = await orchestrator.PauseAsync(id).ConfigureAwait(false);
                return outcome.IsSuccess ? Results.Accepted() : Results.Json(new { error = outcome.Error, errorCode = outcome.ErrorCode }, statusCode: MapErrorCodeToStatus(outcome.ErrorCode));
            });

            app.MapPost("/api/web/orders/{id:guid}/resume", async (Guid id, IConversionOrderOrchestrator orchestrator) =>
            {
                Result outcome = await orchestrator.ResumeAsync(id).ConfigureAwait(false);
                return outcome.IsSuccess ? Results.Accepted() : Results.Json(new { error = outcome.Error, errorCode = outcome.ErrorCode }, statusCode: MapErrorCodeToStatus(outcome.ErrorCode));
            });

            app.MapPost("/api/web/orders/{id:guid}/cancel", async (Guid id, IConversionOrderOrchestrator orchestrator) =>
            {
                Result outcome = await orchestrator.CancelAsync(id).ConfigureAwait(false);
                return outcome.IsSuccess ? Results.Accepted() : Results.Json(new { error = outcome.Error, errorCode = outcome.ErrorCode }, statusCode: MapErrorCodeToStatus(outcome.ErrorCode));
            });

            // ------------------------------------------------------------
            // Settings: system and user scopes with effective resolution.
            // ------------------------------------------------------------
            app.MapGet("/api/settings/effective", (ISucaSettingsService settings) =>
            {
                var detailed = settings.GetEffectiveDetailed();
                return Results.Ok(new { values = detailed.Values, sources = detailed.Sources });
            });

            app.MapGet("/api/settings/{scope}", (string scope, ISucaSettingsService settings) =>
            {
                if (!TryParseScope(scope, out SucaSettingsScope parsed))
                    return Results.BadRequest(new { error = "scope debe ser 'system' o 'user'." });

                return Results.Text(settings.GetScopeJson(parsed), "application/json");
            });

            app.MapPut("/api/settings/{scope}", async (HttpContext http, string scope, ISucaSettingsService settings, CancellationToken ct) =>
            {
                if (!TryParseScope(scope, out SucaSettingsScope parsed))
                    return Results.BadRequest(new { error = "scope debe ser 'system' o 'user'." });

                using var reader = new StreamReader(http.Request.Body);
                string body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

                try
                {
                    await settings.UpdateAsync(parsed, body, ct).ConfigureAwait(false);
                    return Results.Ok(new { status = "updated", scope = parsed.ToString().ToLowerInvariant() });
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            app.MapPost("/api/settings/{scope}/reset", async (string scope, ISucaSettingsService settings, CancellationToken ct) =>
            {
                if (!TryParseScope(scope, out SucaSettingsScope parsed))
                    return Results.BadRequest(new { error = "scope debe ser 'system' o 'user'." });

                await settings.ResetAsync(parsed, ct).ConfigureAwait(false);
                return Results.Ok(new { status = "reset", scope = parsed.ToString().ToLowerInvariant() });
            });

            app.MapGet("/mcp/resources", (IRuntimeObservability obs, IManagedJobStore jobs, IImageConversionService conversion) => Results.Ok(new[]
            {
                new { name = "runtime.snapshot", uri = "/api/observability/snapshot", kind = "resource", description = "Estado del servicio y conteos de operaciones." },
                new { name = "runtime.events", uri = "/api/observability/events", kind = "resource", description = "Eventos recientes para depurar comportamiento." },
                new { name = "runtime.jobs", uri = "/api/jobs", kind = "resource", description = "Jobs activos y completados." },
                new { name = "runtime.health", uri = "/health", kind = "resource", description = "Estado básico del host." },
                new { name = "minichat.conversations", uri = "/api/minichat/conversations", kind = "resource", description = "Lista de conversaciones persistidas." },
                new { name = "gallery.images", uri = "/api/web/images", kind = "resource", description = "Galería de imágenes convertidas." },
                new { name = "gallery.collections", uri = "/api/web/collections", kind = "resource", description = "Colecciones (categorías) de la galería con métricas agregadas." },
                new { name = "orders", uri = "/api/web/orders", kind = "resource", description = "Órdenes de conversión orquestada." },
                new { name = "settings.effective", uri = "/api/settings/effective", kind = "resource", description = "Ajustes efectivos (sistema + usuario) y su origen." }
            }));

            app.MapGet("/mcp/tools", () => Results.Ok(new[]
            {
                new { name = "compress", uri = "/api/tools/compress", kind = "tool" },
                new { name = "compress-job", uri = "/api/tools/compress/jobs", kind = "tool" },
                new { name = "ocr", uri = "/api/tools/ocr", kind = "tool" },
                new { name = "weight-map", uri = "/api/tools/weight-map", kind = "tool" },
                new { name = "embedding", uri = "/api/tools/embedding", kind = "tool" },
                new { name = "local-rag-query", uri = "/api/tools/local-rag/query", kind = "tool" },
                new { name = "lmstudio-rag-index", uri = "/api/tools/lmstudio/rag/index", kind = "tool" },
                new { name = "lmstudio-rag-ask", uri = "/api/tools/lmstudio/rag/ask", kind = "tool" },
                new { name = "lmstudio-rag-index-folder", uri = "/api/tools/lmstudio/rag/index-folder", kind = "tool" },
                new { name = "lmstudio-rag-ask-folder", uri = "/api/tools/lmstudio/rag/ask-folder", kind = "tool" },
                new { name = "lmstudio-vision", uri = "/api/tools/lmstudio/vision", kind = "tool" },
                new { name = "lmstudio-insight", uri = "/api/tools/lmstudio/insight", kind = "tool" },
                new { name = "minichat-send", uri = "/api/minichat/messages/send", kind = "tool" },
                new { name = "minichat-chat-completions", uri = "/api/minichat/chat/completions", kind = "tool" },
                new { name = "convert", uri = "/api/web/convert", kind = "tool" },
                new { name = "gallery-list", uri = "/api/web/images", kind = "tool" },
                new { name = "image-base64", uri = "/api/web/images/base64", kind = "tool" },
                new { name = "convert-order", uri = "/api/web/orders", kind = "tool" },
                new { name = "order-status", uri = "/api/web/orders/{id}", kind = "tool" },
                new { name = "order-pause", uri = "/api/web/orders/{id}/pause", kind = "tool" },
                new { name = "order-resume", uri = "/api/web/orders/{id}/resume", kind = "tool" },
                new { name = "order-cancel", uri = "/api/web/orders/{id}/cancel", kind = "tool" },
                new { name = "gallery-collections", uri = "/api/web/collections", kind = "tool" },
                new { name = "gallery-by-collection", uri = "/api/web/collections/{name}/images", kind = "tool" },
                new { name = "image-path", uri = "/api/web/images/{id}/path", kind = "tool" },
                new { name = "image-reveal", uri = "/api/web/images/{id}/reveal", kind = "tool" },
                new { name = "collection-paths", uri = "/api/web/collections/{name}/paths", kind = "tool" },
                new { name = "settings-get", uri = "/api/settings/effective", kind = "tool" },
                new { name = "settings-update", uri = "/api/settings/{scope}", kind = "tool" }
            }));

            app.MapGet("/mcp/resources/{name}", async (string name, IRuntimeObservability obs, IManagedJobStore jobs, IReliableChatService chat, IImageConversionService conversion, ISucaSettingsService settings) =>
            {
                return name switch
                {
                    "runtime.snapshot" => Results.Ok(obs.GetSnapshot(CountActiveJobs(jobs))),
                    "runtime.events" => Results.Ok(obs.GetRecentEvents()),
                    "runtime.jobs" => Results.Ok(jobs.List()),
                    "runtime.health" => Results.Ok(new { status = "ok", utcNow = DateTimeOffset.UtcNow }),
                    "minichat.conversations" => Results.Ok(await chat.ListConversationsAsync().ConfigureAwait(false)),
                    "gallery.images" => Results.Ok(await conversion.ListAsync().ConfigureAwait(false)),
                    "settings.effective" => Results.Ok(new
                    {
                        values = settings.GetEffective(),
                        sources = settings.GetEffectiveDetailed().Sources
                    }),
                    _ => Results.NotFound()
                };
            });

            app.MapPost("/mcp/tools/{name}", async (string name, JsonElement payload, IServiceProvider services, HttpContext httpContext, CancellationToken ct) =>
            {
                CancellationToken token = LinkToken(httpContext, ct);

                return name switch
                {
                    "compress" => Results.Ok(await services.GetRequiredService<ISucaCompressionCoordinator>()
                        .ProcessAsync(Deserialize<CompressionCommand>(payload), token).ConfigureAwait(false)),

                    "ocr" => Results.Ok(await services.GetRequiredService<ISucaBehaviorService>()
                        .AnalyzeOcrAsync(Deserialize<OcrCommand>(payload), token).ConfigureAwait(false)),

                    "weight-map" => Results.Ok(await services.GetRequiredService<ISucaBehaviorService>()
                        .BuildWeightMapAsync(Deserialize<WeightMapCommand>(payload), token).ConfigureAwait(false)),

                    "embedding" => Results.Ok(await services.GetRequiredService<ISucaBehaviorService>()
                        .BuildEmbeddingAsync(Deserialize<EmbeddingCommand>(payload), token).ConfigureAwait(false)),

                    "local-rag-query" => Results.Ok((await services.GetRequiredService<ISucaBehaviorService>()
                        .QueryLocalVisualRagAsync(Deserialize<LocalVisualRagQueryCommand>(payload), token).ConfigureAwait(false))
                        .Select(x => new { score = x.Score, item = x.Item })),

                    "lmstudio-rag-index" => Results.Ok(await services.GetRequiredService<ILmStudioOrchestrator>()
                        .IndexCompressionAsync(Deserialize<LmStudioRagIndexCommand>(payload), token).ConfigureAwait(false)),

                    "lmstudio-rag-ask" => Results.Ok(ToLmStudioRagResponse(await services.GetRequiredService<ILmStudioOrchestrator>()
                        .AskSemanticRagAsync(Deserialize<LmStudioAskCommand>(payload), token).ConfigureAwait(false))),

                    "lmstudio-rag-index-folder" => Results.Ok(await services.GetRequiredService<ILmStudioOrchestrator>()
                        .IndexFolderAsync(Deserialize<LmStudioFolderIndexCommand>(payload), token).ConfigureAwait(false)),

                    "lmstudio-rag-ask-folder" => Results.Ok(ToLmStudioRagResponse(await services.GetRequiredService<ILmStudioOrchestrator>()
                        .AskFolderAsync(Deserialize<LmStudioFolderAskCommand>(payload), token).ConfigureAwait(false))),

                    "lmstudio-vision" => Results.Ok(ToLmStudioVisionResponse(await services.GetRequiredService<ILmStudioOrchestrator>()
                        .AnalyzeVisionAsync(Deserialize<LmStudioVisionCommand>(payload), token).ConfigureAwait(false))),

                    "lmstudio-insight" => Results.Ok(ToLmStudioInsightResponse(await services.GetRequiredService<ILmStudioOrchestrator>()
                        .BuildInsightAsync(Deserialize<LmStudioInsightCommand>(payload), token).ConfigureAwait(false))),

                    "minichat-send" => Results.Ok(await services.GetRequiredService<IReliableChatService>()
                        .SendAsync(Deserialize<ReliableChatSendRequest>(payload), token).ConfigureAwait(false)),

                    "minichat-chat-completions" => Results.Ok(await services.GetRequiredService<IReliableChatService>()
                        .CompleteAsync(Deserialize<ChatCompletionRequest>(payload), token).ConfigureAwait(false)),

                    "convert" => await ConvertFromMcpPayloadAsync(services, payload, token).ConfigureAwait(false),

                    "gallery-list" => Results.Ok(await services.GetRequiredService<IImageConversionService>()
                        .ListAsync(token).ConfigureAwait(false)),

                    "image-base64" => await ImageBase64FromMcpPayloadAsync(services, payload, token).ConfigureAwait(false),

                    "convert-order" => await ConvertOrderFromMcpPayloadAsync(services, payload, token).ConfigureAwait(false),

                    "order-status" => await OrderActionFromMcpPayloadAsync(services, payload, "status").ConfigureAwait(false),

                    "order-pause" => await OrderActionFromMcpPayloadAsync(services, payload, "pause").ConfigureAwait(false),

                    "order-resume" => await OrderActionFromMcpPayloadAsync(services, payload, "resume").ConfigureAwait(false),

                    "order-cancel" => await OrderActionFromMcpPayloadAsync(services, payload, "cancel").ConfigureAwait(false),

                    "gallery-collections" => Results.Ok(await services.GetRequiredService<IImageConversionService>()
                        .ListCollectionsAsync(token).ConfigureAwait(false)),

                    "gallery-by-collection" => await GalleryByCollectionFromMcpPayloadAsync(services, payload, token).ConfigureAwait(false),

                    "image-path" => await ImagePathFromMcpPayloadAsync(services, payload, token).ConfigureAwait(false),

                    "image-reveal" => await ImagePathFromMcpPayloadAsync(services, payload, token, reveal: true).ConfigureAwait(false),

                    "collection-paths" => await CollectionPathsFromMcpPayloadAsync(services, payload, token).ConfigureAwait(false),

                    "settings-get" => Results.Ok(new
                    {
                        values = services.GetRequiredService<ISucaSettingsService>().GetEffective(),
                        sources = services.GetRequiredService<ISucaSettingsService>().GetEffectiveDetailed().Sources
                    }),

                    "settings-update" => await SettingsUpdateFromMcpPayloadAsync(services, payload, token).ConfigureAwait(false),

                    _ => Results.NotFound()
                };
            });

            await app.RunAsync(cancellationToken).ConfigureAwait(false);
        }

        private static void PrintWebStartupBanner(IEnumerable<string> addresses, SucaSettings effectiveSettings)
        {
            var bases = addresses
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => a.TrimEnd('/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (bases.Count == 0)
                bases.Add(DefaultWebUrl);

            Console.WriteLine();
            Console.WriteLine("==========================================================================");
            Console.WriteLine(" SUCA servicios disponibles");
            foreach (string baseUrl in bases)
            {
                Console.WriteLine($"  Backend (REST + MCP)   : {baseUrl}");
                Console.WriteLine($"    Salud                : {baseUrl}/health");
                Console.WriteLine($"    Observabilidad       : {baseUrl}/api/observability/snapshot");
                Console.WriteLine($"    Jobs                 : {baseUrl}/api/jobs");
                Console.WriteLine($"  Frontend SUCA Studio   : {baseUrl}/studio   (conversion, galeria, ajustes)");
                Console.WriteLine($"  Frontend Minichat      : {baseUrl}/minichat/   (chat LLM + RAG)");
                Console.WriteLine($"  Catalogo MCP           : {baseUrl}/mcp/tools y {baseUrl}/mcp/resources");
            }

            string lmStudio = effectiveSettings.LmStudio.BaseUrl ?? "http://127.0.0.1:1234/v1";
            Console.WriteLine($"  Servicio externo LM Studio: {lmStudio} (requerido para chat/RAG/vision)");
            Console.WriteLine("==========================================================================");
            Console.WriteLine();
        }

        private static int CountActiveJobs(IManagedJobStore jobs) =>
            jobs.List().Count(x => string.Equals(x.Status, "pending", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(x.Status, "running", StringComparison.OrdinalIgnoreCase));

        private static string FormValue(IFormCollection form, string key, string fallback)
        {
            string value = form[key].ToString();
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static IResult MapConversionOutcome(Result<ConversionRecord> outcome)
        {
            if (outcome.IsSuccess)
                return Results.Ok(outcome.Value);

            return Results.Json(
                new { error = outcome.Error, errorCode = outcome.ErrorCode },
                statusCode: MapErrorCodeToStatus(outcome.ErrorCode));
        }

        private static int MapErrorCodeToStatus(string errorCode) => errorCode switch
        {
            ConversionErrorCodes.EmptyFile or
            ConversionErrorCodes.InvalidFormat or
            ConversionErrorCodes.InvalidCollection or
            ConversionErrorCodes.InvalidParallelism or
            ConversionErrorCodes.EmptyOrder or
            ConversionErrorCodes.PathNotFound or
            ConversionErrorCodes.OrderNotFound or
            ConversionErrorCodes.InvalidStateTransition or
            ConversionErrorCodes.TooManyItems => StatusCodes.Status400BadRequest,

            ConversionErrorCodes.RecordNotFound or
            ConversionErrorCodes.CollectionNotFound => StatusCodes.Status404NotFound,

            ConversionErrorCodes.CollectionExists => StatusCodes.Status409Conflict,

            ConversionErrorCodes.TooLarge => StatusCodes.Status413PayloadTooLarge,
            ConversionErrorCodes.UnsupportedImage => StatusCodes.Status422UnprocessableEntity,
            ConversionErrorCodes.Cancelled => 499,
            _ => StatusCodes.Status500InternalServerError
        };

        private static int? FormInt(IFormCollection form, string key)
        {
            string value = form[key].ToString();
            return int.TryParse(value, out int parsed) ? parsed : null;
        }

        private static bool FormBool(IFormCollection form, string key, bool fallback)
        {
            string value = form[key].ToString();
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            return value is "true" or "1" or "yes" or "on";
        }

        private static bool TryParseScope(string value, out SucaSettingsScope scope)
        {
            if (string.Equals(value, "system", StringComparison.OrdinalIgnoreCase))
            {
                scope = SucaSettingsScope.System;
                return true;
            }

            if (string.Equals(value, "user", StringComparison.OrdinalIgnoreCase))
            {
                scope = SucaSettingsScope.User;
                return true;
            }

            scope = SucaSettingsScope.System;
            return false;
        }

        private static object BuildBase64Payload(Guid id, string variant, ConversionFileContent content)
        {
            string base64 = Convert.ToBase64String(content.Bytes);
            string contentType = string.IsNullOrWhiteSpace(content.ContentType) ? "application/octet-stream" : content.ContentType;

            return new
            {
                id,
                variant,
                fileName = content.DownloadName,
                contentType,
                byteCount = content.Bytes.LongLength,
                base64,
                dataUrl = $"data:{contentType};base64,{base64}"
            };
        }

        private static async Task<IResult> ConvertFromMcpPayloadAsync(IServiceProvider services, JsonElement payload, CancellationToken token)
        {
            string? contentBase64 = payload.TryGetProperty("contentBase64", out JsonElement base64Element)
                ? base64Element.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(contentBase64))
                return Results.BadRequest(new { error = "contentBase64 es obligatorio." });

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(contentBase64);
            }
            catch (FormatException)
            {
                return Results.BadRequest(new { error = "contentBase64 no es un base64 válido." });
            }

            var command = new ConvertImageCommand
            {
                FileName = payload.TryGetProperty("fileName", out JsonElement nameElement) ? nameElement.GetString() ?? "imagen" : "imagen",
                Engine = payload.TryGetProperty("engine", out JsonElement engineElement) ? engineElement.GetString() ?? "direct" : "direct",
                Format = payload.TryGetProperty("format", out JsonElement formatElement) ? formatElement.GetString() ?? "webp" : "webp"
            };

            if (payload.TryGetProperty("quality", out JsonElement qualityElement) && qualityElement.TryGetInt32(out int quality))
                command.Quality = quality;

            if (payload.TryGetProperty("maxWidth", out JsonElement widthElement) && widthElement.TryGetInt32(out int width))
                command.MaxWidth = width;

            if (payload.TryGetProperty("maxHeight", out JsonElement heightElement) && heightElement.TryGetInt32(out int height))
                command.MaxHeight = height;

            if (payload.TryGetProperty("removeMetadata", out JsonElement metadataElement) && metadataElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                command.RemoveMetadata = metadataElement.GetBoolean();

            var settings = services.GetRequiredService<ISucaSettingsService>();
            var effective = settings.GetEffective();
            // Apply user defaults for any value the payload omitted.
            if (!payload.TryGetProperty("engine", out _))
                command.Engine = effective.Conversion.Engine ?? command.Engine;

            if (!payload.TryGetProperty("format", out _))
                command.Format = effective.Conversion.Format ?? command.Format;

            command.Quality ??= effective.Conversion.Quality;
            command.MaxWidth ??= effective.Conversion.MaxWidth;
            command.MaxHeight ??= effective.Conversion.MaxHeight;

            using var stream = new MemoryStream(bytes, writable: false);
            Result<ConversionRecord> outcome = await services.GetRequiredService<IImageConversionService>()
                .ConvertAsync(command, stream, token)
                .ConfigureAwait(false);

            return MapConversionOutcome(outcome);
        }

        private static List<Guid> ReadGuidList(JsonElement payload, string property)
        {
            var ids = new List<Guid>();
            if (!payload.TryGetProperty(property, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
                return ids;

            foreach (JsonElement entry in array.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String && Guid.TryParse(entry.GetString(), out Guid parsed))
                {
                    ids.Add(parsed);
                }
                else if (entry.ValueKind == JsonValueKind.Object
                         && entry.TryGetProperty("id", out JsonElement idElement)
                         && Guid.TryParse(idElement.GetString(), out Guid fromObject))
                {
                    ids.Add(fromObject);
                }
            }

            return ids;
        }

        private static async Task<IResult> ConvertOrderFromMcpPayloadAsync(IServiceProvider services, JsonElement payload, CancellationToken token)
        {
            var request = payload.Deserialize<ConversionOrderRequest>(JsonDefaults.Loose);
            if (request is null)
                return Results.BadRequest(new { error = "No se pudo leer la orden.", errorCode = ConversionErrorCodes.EmptyOrder });

            Result<ConversionOrder> outcome = await services.GetRequiredService<IConversionOrderOrchestrator>()
                .SubmitAsync(request, token)
                .ConfigureAwait(false);

            return outcome.IsSuccess
                ? Results.Accepted(
                    $"/api/web/orders/{outcome.Value.OrderId}",
                    new
                    {
                        orderId = outcome.Value.OrderId,
                        correlationId = outcome.Value.CorrelationId,
                        statusUrl = $"/api/web/orders/{outcome.Value.OrderId}",
                        totalItems = outcome.Value.Items.Count
                    })
                : Results.Json(
                    new { error = outcome.Error, errorCode = outcome.ErrorCode },
                    statusCode: MapErrorCodeToStatus(outcome.ErrorCode));
        }

        private static async Task<IResult> OrderActionFromMcpPayloadAsync(IServiceProvider services, JsonElement payload, string action)
        {
            if (!payload.TryGetProperty("orderId", out JsonElement idElement) || !Guid.TryParse(idElement.GetString(), out Guid orderId))
                return Results.BadRequest(new { error = "orderId (GUID) es obligatorio.", errorCode = ConversionErrorCodes.OrderNotFound });

            IConversionOrderOrchestrator orchestrator = services.GetRequiredService<IConversionOrderOrchestrator>();

            switch (action)
            {
                case "status":
                {
                    ConversionOrder? order = await orchestrator.GetAsync(orderId).ConfigureAwait(false);
                    return order is null
                        ? Results.NotFound()
                        : Results.Ok(order);
                }

                case "pause":
                {
                    Result outcome = await orchestrator.PauseAsync(orderId).ConfigureAwait(false);
                    return outcome.IsSuccess ? Results.Accepted() : Results.Json(new { error = outcome.Error, errorCode = outcome.ErrorCode }, statusCode: MapErrorCodeToStatus(outcome.ErrorCode));
                }

                case "resume":
                {
                    Result outcome = await orchestrator.ResumeAsync(orderId).ConfigureAwait(false);
                    return outcome.IsSuccess ? Results.Accepted() : Results.Json(new { error = outcome.Error, errorCode = outcome.ErrorCode }, statusCode: MapErrorCodeToStatus(outcome.ErrorCode));
                }

                default:
                {
                    Result outcome = await orchestrator.CancelAsync(orderId).ConfigureAwait(false);
                    return outcome.IsSuccess ? Results.Accepted() : Results.Json(new { error = outcome.Error, errorCode = outcome.ErrorCode }, statusCode: MapErrorCodeToStatus(outcome.ErrorCode));
                }
            }
        }

        private static async Task<IResult> GalleryByCollectionFromMcpPayloadAsync(IServiceProvider services, JsonElement payload, CancellationToken token)
        {
            string name = payload.TryGetProperty("collection", out JsonElement nameElement)
                ? nameElement.GetString() ?? ""
                : "";
            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { error = "collection es obligatorio.", errorCode = ConversionErrorCodes.InvalidCollection });

            var request = new CollectionImagesRequest
            {
                Page = payload.TryGetProperty("page", out JsonElement pageElement) && pageElement.TryGetInt32(out int page) ? page : 1,
                PageSize = payload.TryGetProperty("pageSize", out JsonElement sizeElement) && sizeElement.TryGetInt32(out int size) ? size : 24,
                IncludeBase64 = !payload.TryGetProperty("includeBase64", out JsonElement includeElement) || includeElement.GetBoolean(),
                IncludeOriginalBase64 = payload.TryGetProperty("originalBase64", out JsonElement originalElement) && originalElement.GetBoolean()
            };

            Result<CollectionImagesResult> outcome = await services.GetRequiredService<IImageConversionService>()
                .GetCollectionAsync(name, request, token)
                .ConfigureAwait(false);

            return outcome.IsSuccess
                ? Results.Ok(outcome.Value)
                : Results.Json(
                    new { error = outcome.Error, errorCode = outcome.ErrorCode },
                    statusCode: MapErrorCodeToStatus(outcome.ErrorCode));
        }

        private static async Task<IResult> ImageBase64FromMcpPayloadAsync(IServiceProvider services, JsonElement payload, CancellationToken token)
        {
            if (!payload.TryGetProperty("id", out JsonElement idElement) || !Guid.TryParse(idElement.GetString(), out Guid id))
                return Results.BadRequest(new { error = "id (GUID) es obligatorio." });

            string variant = payload.TryGetProperty("variant", out JsonElement variantElement)
                ? variantElement.GetString() ?? "converted"
                : "converted";

            var content = await services.GetRequiredService<IImageConversionService>()
                .OpenFileAsync(id, variant, token).ConfigureAwait(false);

            return content is null ? Results.NotFound() : Results.Ok(BuildBase64Payload(id, variant, content));
        }

        private static async Task<IResult> ImagePathFromMcpPayloadAsync(IServiceProvider services, JsonElement payload, CancellationToken token, bool reveal = false)
        {
            if (!payload.TryGetProperty("id", out JsonElement idElement) || !Guid.TryParse(idElement.GetString(), out Guid id))
                return Results.BadRequest(new { error = "id (GUID) es obligatorio.", errorCode = ConversionErrorCodes.RecordNotFound });

            string variant = payload.TryGetProperty("variant", out JsonElement variantElement)
                ? variantElement.GetString() ?? "converted"
                : "converted";

            IImageConversionService conversion = services.GetRequiredService<IImageConversionService>();

            if (reveal)
            {
                Result outcome = await conversion.RevealInExplorerAsync(id, variant, token).ConfigureAwait(false);
                return outcome.IsSuccess
                    ? Results.Ok(new { status = "revealed", variant = variant.ToLowerInvariant() })
                    : Results.Json(new { error = outcome.Error, errorCode = outcome.ErrorCode }, statusCode: MapErrorCodeToStatus(outcome.ErrorCode));
            }

            Result<ConversionFilePath> outcome2 = await conversion
                .GetFilePathAsync(id, variant, token)
                .ConfigureAwait(false);

            return outcome2.IsSuccess
                ? Results.Ok(outcome2.Value)
                : Results.Json(new { error = outcome2.Error, errorCode = outcome2.ErrorCode }, statusCode: MapErrorCodeToStatus(outcome2.ErrorCode));
        }

        private static async Task<IResult> CollectionPathsFromMcpPayloadAsync(IServiceProvider services, JsonElement payload, CancellationToken token)
        {
            string name = payload.TryGetProperty("collection", out JsonElement nameElement)
                ? nameElement.GetString() ?? ""
                : "";
            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { error = "collection es obligatorio.", errorCode = ConversionErrorCodes.InvalidCollection });

            string variant = payload.TryGetProperty("variant", out JsonElement variantElement)
                ? variantElement.GetString() ?? "converted"
                : "converted";

            var paths = await services.GetRequiredService<IImageConversionService>()
                .GetCollectionFilePathsAsync(name, variant, token)
                .ConfigureAwait(false);

            return Results.Ok(new
            {
                collection = ImageConversionService.NormalizeCollection(name),
                total = paths.Count,
                items = paths,
                joined = string.Join(Environment.NewLine, paths.Select(x => x.Path))
            });
        }

        private static async Task<IResult> SettingsUpdateFromMcpPayloadAsync(IServiceProvider services, JsonElement payload, CancellationToken token)
        {
            string scopeValue = payload.TryGetProperty("scope", out JsonElement scopeElement)
                ? scopeElement.GetString() ?? "user"
                : "user";

            if (!TryParseScope(scopeValue, out SucaSettingsScope scope))
                return Results.BadRequest(new { error = "scope debe ser 'system' o 'user'." });

            if (!payload.TryGetProperty("values", out JsonElement valuesElement) || valuesElement.ValueKind is not JsonValueKind.Object)
                return Results.BadRequest(new { error = "values debe ser un objeto con los ajustes a combinar." });

            var settingsService = services.GetRequiredService<ISucaSettingsService>();
            await settingsService.UpdateAsync(scope, valuesElement.GetRawText(), token).ConfigureAwait(false);
            return Results.Ok(new { status = "updated", scope = scope.ToString().ToLowerInvariant() });
        }

        private static CancellationToken LinkToken(HttpContext context, CancellationToken token)
        {
            if (!context.RequestAborted.CanBeCanceled)
                return token;

            if (!token.CanBeCanceled)
                return context.RequestAborted;

            var linked = CancellationTokenSource.CreateLinkedTokenSource(token, context.RequestAborted);
            context.Response.RegisterForDispose(linked);
            return linked.Token;
        }

        private static T Deserialize<T>(JsonElement element)
        {
            var value = element.Deserialize<T>(JsonDefaults.Loose);
            if (value is null)
                throw new InvalidOperationException($"No se pudo deserializar {typeof(T).Name}.");

            return value;
        }

        private static object ToLmStudioRagResponse(LmStudioRagAnswer answer)
        {
            return new
            {
                query = answer.Query,
                response = answer.Response,
                contexts = answer.Contexts.Select(x => new
                {
                    score = x.Score,
                    sourcePath = x.Item.SourcePath,
                    text = x.Item.Text,
                    metadata = x.Item.Metadata
                })
            };
        }

        private static object ToLmStudioVisionResponse(LmStudioVisionInsight insight)
        {
            return new
            {
                model = insight.Model,
                prompt = insight.Prompt,
                imagePath = insight.ImagePath,
                response = insight.Response
            };
        }

        private static object ToLmStudioInsightResponse(LmStudioSucaInsight insight)
        {
            return new
            {
                compression = insight.Compression,
                ocr = insight.Ocr,
                weightMap = insight.WeightMap is null ? null : new
                {
                    cellsX = insight.WeightMap.CellsX,
                    cellsY = insight.WeightMap.CellsY,
                    weights = ToJagged(insight.WeightMap.Weights)
                },
                vision = insight.Vision is null ? null : ToLmStudioVisionResponse(insight.Vision),
                advisory = insight.Advisory
            };
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

        private static string? TryReadOption(string[] args, string optionName)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.Equals(arg, optionName, StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                        return args[i + 1];
                }

                if (arg.StartsWith(optionName + "=", StringComparison.OrdinalIgnoreCase))
                    return arg.Substring(optionName.Length + 1);
            }

            return null;
        }
    }
}
