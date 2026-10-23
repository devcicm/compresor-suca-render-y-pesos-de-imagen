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
using SUCA_Compressor.Application.Artifacts;
using SUCA_Compressor.Kernel;

namespace SUCA_Compressor
{
    public static class SucaRestHost
    {
        public static async Task RunAsync(string[] args, CancellationToken cancellationToken = default)
        {
            var builder = WebApplication.CreateBuilder(args);
            string? explicitUrls = TryReadOption(args, "--urls");
            string minichatUiPath = Path.Combine(builder.Environment.ContentRootPath, "src", "Suca.Web", "wwwroot");
            string defaultRagSourceDirectory = Path.Combine(builder.Environment.ContentRootPath, "knowledge", "seed", "documents");
            string defaultRagIndexPath = Path.Combine(builder.Environment.ContentRootPath, "runtime", "indexes", "minichat-rag-index.json");

            if (!string.IsNullOrWhiteSpace(explicitUrls))
                builder.WebHost.UseUrls(explicitUrls);

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
            builder.Services.AddHostedService<SucaLifecycleService>();

            var app = builder.Build();

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
                    RequestPath = "/minichat",
                    DefaultFileNames = { "index.html" }
                });
                app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(minichatUiPath),
                    RequestPath = "/minichat"
                });
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
                return Results.Ok(await admin.ListDetailedModelsAsync(ct).ConfigureAwait(false));
            });

            app.MapGet("/api/minichat/defaults", () => Results.Ok(new
            {
                lmStudio = new
                {
                    baseUrl = "http://127.0.0.1:1234/v1",
                    chatModel = "deepseek/deepseek-r1-0528-qwen3-8b",
                    embeddingModel = "text-embedding-nomic-embed-text-v1.5",
                    temperature = 0,
                    maxTokens = 700
                },
                rag = new
                {
                    sourceDirectory = defaultRagSourceDirectory,
                    semanticIndexPath = defaultRagIndexPath,
                    refreshIndexBeforeQuery = true,
                    recursive = true,
                    topK = 4,
                    chunkSize = 1200,
                    chunkOverlap = 180
                }
            }));

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

            app.MapGet("/api/minichat/conversations", async (IReliableChatService chat, CancellationToken ct) =>
                Results.Ok(await chat.ListConversationsAsync(ct).ConfigureAwait(false)));

            app.MapGet("/api/minichat/conversations/{id}", async (string id, IReliableChatService chat, CancellationToken ct) =>
            {
                var conversation = await chat.GetConversationAsync(id, ct).ConfigureAwait(false);
                return conversation is null ? Results.NotFound() : Results.Ok(conversation);
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

            app.MapGet("/mcp/resources", (IRuntimeObservability obs, IManagedJobStore jobs) => Results.Ok(new[]
            {
                new { name = "runtime.snapshot", uri = "/api/observability/snapshot", kind = "resource", description = "Estado del servicio y conteos de operaciones." },
                new { name = "runtime.events", uri = "/api/observability/events", kind = "resource", description = "Eventos recientes para depurar comportamiento." },
                new { name = "runtime.jobs", uri = "/api/jobs", kind = "resource", description = "Jobs activos y completados." },
                new { name = "runtime.health", uri = "/health", kind = "resource", description = "Estado básico del host." },
                new { name = "minichat.conversations", uri = "/api/minichat/conversations", kind = "resource", description = "Lista de conversaciones persistidas." }
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
                new { name = "minichat-chat-completions", uri = "/api/minichat/chat/completions", kind = "tool" }
            }));

            app.MapGet("/mcp/resources/{name}", async (string name, IRuntimeObservability obs, IManagedJobStore jobs, IReliableChatService chat) =>
            {
                return name switch
                {
                    "runtime.snapshot" => Results.Ok(obs.GetSnapshot(CountActiveJobs(jobs))),
                    "runtime.events" => Results.Ok(obs.GetRecentEvents()),
                    "runtime.jobs" => Results.Ok(jobs.List()),
                    "runtime.health" => Results.Ok(new { status = "ok", utcNow = DateTimeOffset.UtcNow }),
                    "minichat.conversations" => Results.Ok(await chat.ListConversationsAsync().ConfigureAwait(false)),
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

                    _ => Results.NotFound()
                };
            });

            await app.RunAsync(cancellationToken).ConfigureAwait(false);
        }

        private static int CountActiveJobs(IManagedJobStore jobs) =>
            jobs.List().Count(x => string.Equals(x.Status, "pending", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(x.Status, "running", StringComparison.OrdinalIgnoreCase));

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
