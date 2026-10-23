using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SUCA_Compressor.Shared.Math;

namespace SUCA_Compressor
{
    public sealed class LmStudioOptions
    {
        public string BaseUrl { get; set; } = "http://127.0.0.1:1234/v1";
        public string? ApiKey { get; set; } = "lm-studio";
        public string ChatModel { get; set; } = "local-model";
        public string EmbeddingModel { get; set; } = "text-embedding-model";
        public string? VisionModel { get; set; } = null;
        public double Temperature { get; set; } = 0.15;
        public int MaxTokens { get; set; } = 900;
        public int TimeoutSeconds { get; set; } = 180;
        public string? SemanticRagIndexPath { get; set; } = null;
    }

    public sealed class LmStudioChatMessage
    {
        public string Role { get; set; } = "user";
        public string? Content { get; set; }
    }

    public sealed class LmStudioVisionInsight
    {
        public string Model { get; set; } = "";
        public string Prompt { get; set; } = "";
        public string ImagePath { get; set; } = "";
        public ParsedLlmJsonResponse Response { get; set; } = new();

        [JsonIgnore]
        public string RawResponseText { get; set; } = "";
    }

    public sealed class LmStudioRagHit
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string SourcePath { get; set; } = "";
        public string Text { get; set; } = "";
        public float[] Embedding { get; set; } = Array.Empty<float>();
        public string EmbeddingModel { get; set; } = "";
        public Dictionary<string, string> Metadata { get; set; } = new();
    }

    public sealed class LmStudioFolderIndexResult
    {
        public string SourceDirectory { get; set; } = "";
        public string IndexPath { get; set; } = "";
        public int FileCount { get; set; }
        public int ChunkCount { get; set; }
        public List<string> Files { get; set; } = new();
    }

    public sealed class LmStudioSemanticRagIndex
    {
        public List<LmStudioRagHit> Items { get; } = new();

        public void Add(LmStudioRagHit item)
        {
            if (item is null) throw new ArgumentNullException(nameof(item));
            Items.Add(item);
        }

        public List<(LmStudioRagHit Item, double Score)> Search(float[] queryEmbedding, int topK = 5)
        {
            if (queryEmbedding is null) throw new ArgumentNullException(nameof(queryEmbedding));
            if (queryEmbedding.Length == 0) return new();

            return Items
                .Where(i => i.Embedding is { Length: > 0 } && i.Embedding.Length == queryEmbedding.Length)
                .Select(i => (Item: i, Score: VectorMath.CosineSimilarity(queryEmbedding, i.Embedding)))
                .OrderByDescending(x => x.Score)
                .Take(topK)
                .ToList();
        }

        public void Save(string path)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(Items, JsonDefaults.Indented);
            File.WriteAllText(path, json, Encoding.UTF8);
        }

        public static LmStudioSemanticRagIndex Load(string path)
        {
            var idx = new LmStudioSemanticRagIndex();
            if (!File.Exists(path))
                return idx;

            var items = JsonSerializer.Deserialize<List<LmStudioRagHit>>(File.ReadAllText(path, Encoding.UTF8), JsonDefaults.Loose)
                        ?? new List<LmStudioRagHit>();
            idx.Items.AddRange(items);
            return idx;
        }
    }

    public sealed class LmStudioRagAnswer
    {
        public string Query { get; set; } = "";
        public ParsedLlmJsonResponse Response { get; set; } = new();
        public List<(LmStudioRagHit Item, double Score)> Contexts { get; set; } = new();

        [JsonIgnore]
        public string RawAnswerText { get; set; } = "";
    }

    public sealed class LmStudioSucaInsight
    {
        public CompressionResult? Compression { get; set; }
        public VisualOcrFeatures? Ocr { get; set; }
        public SucaNeuralWeightMap? WeightMap { get; set; }
        public LmStudioVisionInsight? Vision { get; set; }
        public ParsedLlmJsonResponse Advisory { get; set; } = new();

        [JsonIgnore]
        public string RawAdvisoryText { get; set; } = "";
    }

    public static class LmStudioSucaBridge
    {
        public static string BuildSemanticKnowledgeText(
            CompressionResult result,
            VisualOcrFeatures? ocr = null,
            SucaNeuralWeightMap? weightMap = null,
            string? extraNotes = null)
        {
            if (result is null) throw new ArgumentNullException(nameof(result));

            var sb = new StringBuilder();
            sb.AppendLine(RagTextProjector.BuildImageKnowledgeText(result));

            if (ocr is not null)
            {
                sb.AppendLine("OCR_Visual:");
                sb.AppendLine($"TextLines: {ocr.EstimatedTextLines}");
                sb.AppendLine($"Columns: {ocr.EstimatedColumns}");
                sb.AppendLine($"InkDensity: {ocr.InkDensity:F4}");
                sb.AppendLine($"TransitionScore: {ocr.CharacterTransitionScore:F4}");
                sb.AppendLine($"ReadabilityScore: {ocr.ReadabilityScore:F4}");
            }

            if (weightMap is not null)
            {
                var summary = SummarizeWeightMap(weightMap);
                sb.AppendLine("NeuralWeightMap:");
                sb.AppendLine($"MinWeight: {summary.Min:F4}");
                sb.AppendLine($"MeanWeight: {summary.Mean:F4}");
                sb.AppendLine($"MaxWeight: {summary.Max:F4}");
                sb.AppendLine($"HotCells(>=0.75): {summary.HotCells}");
            }

            if (!string.IsNullOrWhiteSpace(extraNotes))
            {
                sb.AppendLine("Notas:");
                sb.AppendLine(extraNotes.Trim());
            }

            return sb.ToString().Trim();
        }

        public static async Task<LmStudioRagHit> BuildSemanticRagItemAsync(
            LmStudioClient client,
            CompressionResult result,
            VisualOcrFeatures? ocr = null,
            SucaNeuralWeightMap? weightMap = null,
            string? extraNotes = null,
            CancellationToken cancellationToken = default)
        {
            if (client is null) throw new ArgumentNullException(nameof(client));
            if (result is null) throw new ArgumentNullException(nameof(result));

            string text = BuildSemanticKnowledgeText(result, ocr, weightMap, extraNotes);
            float[] embedding = await client.CreateEmbeddingAsync(text, cancellationToken).ConfigureAwait(false);

            return new LmStudioRagHit
            {
                SourcePath = result.OutputPath,
                Text = text,
                Embedding = embedding,
                EmbeddingModel = client.Options.EmbeddingModel,
                Metadata = new Dictionary<string, string>
                {
                    ["kind"] = result.Analysis.Kind.ToString(),
                    ["format"] = result.Decision.OutputFormat.ToString(),
                    ["inputPath"] = result.InputPath,
                    ["outputPath"] = result.OutputPath
                }
            };
        }

        public static async Task<LmStudioRagHit> IndexCompressionResultAsync(
            LmStudioClient client,
            string indexPath,
            CompressionResult result,
            VisualOcrFeatures? ocr = null,
            SucaNeuralWeightMap? weightMap = null,
            string? extraNotes = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(indexPath)) throw new ArgumentException("Ruta de índice inválida.", nameof(indexPath));

            var index = LmStudioSemanticRagIndex.Load(indexPath);
            var item = await BuildSemanticRagItemAsync(client, result, ocr, weightMap, extraNotes, cancellationToken).ConfigureAwait(false);
            index.Add(item);
            index.Save(indexPath);
            return item;
        }

        public static async Task<LmStudioFolderIndexResult> IndexDirectoryAsync(
            LmStudioClient client,
            string sourceDirectory,
            string indexPath,
            bool recursive = true,
            int chunkSize = 1200,
            int chunkOverlap = 180,
            string[]? extensions = null,
            CancellationToken cancellationToken = default)
        {
            if (client is null) throw new ArgumentNullException(nameof(client));
            if (string.IsNullOrWhiteSpace(sourceDirectory)) throw new ArgumentException("Directorio fuente inválido.", nameof(sourceDirectory));
            if (!Directory.Exists(sourceDirectory)) throw new DirectoryNotFoundException(sourceDirectory);
            if (string.IsNullOrWhiteSpace(indexPath)) throw new ArgumentException("Ruta de índice inválida.", nameof(indexPath));
            if (chunkSize < 200) throw new ArgumentOutOfRangeException(nameof(chunkSize), "ChunkSize demasiado pequeño.");
            if (chunkOverlap < 0 || chunkOverlap >= chunkSize) throw new ArgumentOutOfRangeException(nameof(chunkOverlap));

            string[] effectiveExtensions = NormalizeExtensions(extensions);
            var files = EnumerateKnowledgeFiles(sourceDirectory, recursive, effectiveExtensions).ToList();

            var index = new LmStudioSemanticRagIndex();
            foreach (string file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string content = await DocumentTextExtractor.ExtractTextAsync(file, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(content))
                    continue;

                int chunkIndex = 0;
                foreach (string chunk in ChunkText(content, chunkSize, chunkOverlap))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(chunk))
                        continue;

                    float[] embedding = await client.CreateEmbeddingAsync(chunk, cancellationToken).ConfigureAwait(false);
                    index.Add(new LmStudioRagHit
                    {
                        SourcePath = file,
                        Text = chunk,
                        Embedding = embedding,
                        EmbeddingModel = client.Options.EmbeddingModel,
                        Metadata = new Dictionary<string, string>
                        {
                            ["sourceDirectory"] = sourceDirectory,
                            ["relativePath"] = Path.GetRelativePath(sourceDirectory, file),
                            ["chunkIndex"] = chunkIndex.ToString(),
                            ["kind"] = "document"
                        }
                    });
                    chunkIndex++;
                }
            }

            index.Save(indexPath);
            return new LmStudioFolderIndexResult
            {
                SourceDirectory = sourceDirectory,
                IndexPath = indexPath,
                FileCount = files.Count,
                ChunkCount = index.Items.Count,
                Files = files
            };
        }

        public static async Task<LmStudioRagAnswer> AskWithSemanticRagAsync(
            LmStudioClient client,
            string indexPath,
            string userQuestion,
            int topK = 4,
            CancellationToken cancellationToken = default)
        {
            if (client is null) throw new ArgumentNullException(nameof(client));
            if (string.IsNullOrWhiteSpace(indexPath)) throw new ArgumentException("Ruta de índice inválida.", nameof(indexPath));
            if (string.IsNullOrWhiteSpace(userQuestion)) throw new ArgumentException("Consulta vacía.", nameof(userQuestion));

            var index = LmStudioSemanticRagIndex.Load(indexPath);
            float[] q = await client.CreateEmbeddingAsync(userQuestion, cancellationToken).ConfigureAwait(false);
            var hits = index.Search(q, topK);

            string prompt = BuildRagAnswerPrompt(userQuestion, hits);
            ParsedLlmJsonResponse answer = await client.ChatAsJsonAsync(new[]
            {
                new LmStudioChatMessage
                {
                    Role = "system",
                    Content = "Eres un asistente técnico para SUCA. Responde solo con base en el contexto recuperado. Si falta evidencia, dilo explícitamente."
                },
                new LmStudioChatMessage
                {
                    Role = "user",
                    Content = prompt
                }
            }, "suca_rag_answer", cancellationToken).ConfigureAwait(false);

            answer.Question ??= userQuestion;
            answer.Format ??= "rag";

            return new LmStudioRagAnswer
            {
                Query = userQuestion,
                Response = answer,
                RawAnswerText = answer.RawContent,
                Contexts = hits
            };
        }

        public static async Task<LmStudioVisionInsight> AnalyzeImageAsync(
            LmStudioClient client,
            string imagePath,
            CompressionResult? result = null,
            VisualOcrFeatures? ocr = null,
            SucaNeuralWeightMap? weightMap = null,
            string? userPrompt = null,
            CancellationToken cancellationToken = default)
        {
            if (client is null) throw new ArgumentNullException(nameof(client));
            if (string.IsNullOrWhiteSpace(imagePath)) throw new ArgumentException("Ruta de imagen inválida.", nameof(imagePath));

            string prompt = BuildVisionPrompt(result, ocr, weightMap, userPrompt);
            ParsedLlmJsonResponse response = await client.VisionAsJsonAsync(imagePath, prompt, "suca_vision_analysis", cancellationToken).ConfigureAwait(false);
            response.Format ??= "vision";

            return new LmStudioVisionInsight
            {
                Model = client.Options.VisionModel ?? client.Options.ChatModel,
                Prompt = prompt,
                ImagePath = imagePath,
                Response = response,
                RawResponseText = response.RawContent
            };
        }

        public static async Task<LmStudioSucaInsight> BuildFullInsightAsync(
            LmStudioClient client,
            CompressionResult result,
            string imagePathForVision,
            SucaProfile? profile = null,
            bool runVision = true,
            CancellationToken cancellationToken = default)
        {
            if (client is null) throw new ArgumentNullException(nameof(client));
            if (result is null) throw new ArgumentNullException(nameof(result));

            var ocr = VisualOcrEngine.AnalyzeReadability(result.OutputPath, cancellationToken);
            int profileSeed = profile?.ExtendedHash() ?? result.Decision.ExtendedHash;
            var weightMap = SucaNeuralMapEngine.BuildWeightMap(imagePathForVision, profileSeed, cancellationToken);

            LmStudioVisionInsight? vision = null;
            if (runVision)
                vision = await AnalyzeImageAsync(client, imagePathForVision, result, ocr, weightMap, cancellationToken: cancellationToken).ConfigureAwait(false);

            string advisoryPrompt = BuildAdvisorPrompt(result, ocr, weightMap, vision?.Response.Answer);
            ParsedLlmJsonResponse advisory = await client.ChatAsJsonAsync(new[]
            {
                new LmStudioChatMessage
                {
                    Role = "system",
                    Content = "Eres un analista técnico de compresión visual para SUCA. Entrega recomendaciones operativas, no marketing."
                },
                new LmStudioChatMessage
                {
                    Role = "user",
                    Content = advisoryPrompt
                }
            }, "suca_advisory", cancellationToken).ConfigureAwait(false);

            advisory.Format ??= "advisory";

            return new LmStudioSucaInsight
            {
                Compression = result,
                Ocr = ocr,
                WeightMap = weightMap,
                Vision = vision,
                Advisory = advisory,
                RawAdvisoryText = advisory.RawContent
            };
        }

        private static string BuildRagAnswerPrompt(string userQuestion, List<(LmStudioRagHit Item, double Score)> hits)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Pregunta del usuario:");
            sb.AppendLine(userQuestion.Trim());
            sb.AppendLine();
            sb.AppendLine("Contexto recuperado:");

            if (hits.Count == 0)
            {
                sb.AppendLine("[Sin contexto recuperado]");
            }
            else
            {
                int i = 1;
                foreach (var hit in hits)
                {
                    sb.AppendLine($"[Doc {i}] score={hit.Score:F4} path={hit.Item.SourcePath}");
                    sb.AppendLine(hit.Item.Text);
                    sb.AppendLine();
                    i++;
                }
            }

            sb.AppendLine("Instrucciones:");
            sb.AppendLine("- Responde en espanol.");
            sb.AppendLine("- Cita los archivos relevantes por path cuando existan.");
            sb.AppendLine("- Si el contexto no basta, dilo claramente.");
            return sb.ToString();
        }

        private static IEnumerable<string> EnumerateKnowledgeFiles(string sourceDirectory, bool recursive, string[] extensions)
        {
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            return Directory.EnumerateFiles(sourceDirectory, "*.*", option)
                .Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> ChunkText(string content, int chunkSize, int overlap)
        {
            string normalized = content.Replace("\r\n", "\n").Trim();
            if (normalized.Length <= chunkSize)
            {
                yield return normalized;
                yield break;
            }

            int start = 0;
            while (start < normalized.Length)
            {
                int length = Math.Min(chunkSize, normalized.Length - start);
                int end = start + length;
                if (end < normalized.Length)
                {
                    int cut = normalized.LastIndexOf('\n', end - 1, length);
                    if (cut > start + 100)
                        end = cut + 1;
                }

                string chunk = normalized.Substring(start, end - start).Trim();
                if (!string.IsNullOrWhiteSpace(chunk))
                    yield return chunk;

                if (end >= normalized.Length)
                    break;

                start = Math.Max(end - overlap, start + 1);
            }
        }

        private static string[] NormalizeExtensions(string[]? extensions)
        {
            var defaults = new[]
            {
                ".txt", ".md", ".csv", ".log", ".json", ".jsonl", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf", ".sql",
                ".html", ".htm", ".xhtml", ".rtf", ".pdf",
                ".docx", ".xlsx", ".pptx",
                ".odt", ".ods", ".odp",
                ".epub"
            };
            if (extensions is null || extensions.Length == 0)
                return defaults;

            return extensions
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.StartsWith('.') ? x : "." + x)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string BuildVisionPrompt(
            CompressionResult? result,
            VisualOcrFeatures? ocr,
            SucaNeuralWeightMap? weightMap,
            string? userPrompt)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Analiza esta imagen para el proyecto SUCA.");
            sb.AppendLine("Necesito una salida tecnica con:");
            sb.AppendLine("1. Tipo de contenido visual.");
            sb.AppendLine("2. Riesgos de compresion.");
            sb.AppendLine("3. Zonas que requieren preservacion.");
            sb.AppendLine("4. Si parece documento, UI, grafico, foto o huella.");
            sb.AppendLine("5. Recomendacion breve para formato/calidad.");

            if (result is not null)
            {
                sb.AppendLine();
                sb.AppendLine("Contexto SUCA:");
                sb.AppendLine(BuildSemanticKnowledgeText(result, ocr, weightMap));
            }

            if (!string.IsNullOrWhiteSpace(userPrompt))
            {
                sb.AppendLine();
                sb.AppendLine("Pregunta adicional del usuario:");
                sb.AppendLine(userPrompt.Trim());
            }

            return sb.ToString();
        }

        private static string BuildAdvisorPrompt(
            CompressionResult result,
            VisualOcrFeatures? ocr,
            SucaNeuralWeightMap? weightMap,
            string? visionResponse)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Resume las mejores recomendaciones para operar SUCA con este activo.");
            sb.AppendLine("Quiero:");
            sb.AppendLine("- Riesgo de perdida visual.");
            sb.AppendLine("- Si el OCR visual sugiere documento sensible.");
            sb.AppendLine("- Si el mapa de pesos exige proteger zonas.");
            sb.AppendLine("- Recomendaciones concretas para formato, calidad y escala.");
            sb.AppendLine();
            sb.AppendLine(BuildSemanticKnowledgeText(result, ocr, weightMap));

            if (!string.IsNullOrWhiteSpace(visionResponse))
            {
                sb.AppendLine();
                sb.AppendLine("Analisis multimodal del modelo:");
                sb.AppendLine(visionResponse.Trim());
            }

            return sb.ToString();
        }

        private static (double Min, double Mean, double Max, int HotCells) SummarizeWeightMap(SucaNeuralWeightMap map)
        {
            var values = new List<double>(map.CellsX * map.CellsY);
            for (int y = 0; y < map.CellsY; y++)
                for (int x = 0; x < map.CellsX; x++)
                    values.Add(map.Weights[y, x]);

            return (
                Min: values.Count == 0 ? 0 : values.Min(),
                Mean: values.Count == 0 ? 0 : values.Average(),
                Max: values.Count == 0 ? 0 : values.Max(),
                HotCells: values.Count(v => v >= 0.75)
            );
        }
    }

    public sealed class LmStudioClient : IDisposable
    {
        private readonly HttpClient _http;
        private bool _disposeClient;

        public LmStudioOptions Options { get; }

        public LmStudioClient(LmStudioOptions options, HttpClient? httpClient = null)
        {
            Options = options ?? throw new ArgumentNullException(nameof(options));
            _http = httpClient ?? new HttpClient();
            _disposeClient = httpClient is null;
            _http.Timeout = TimeSpan.FromSeconds(Math.Max(5, options.TimeoutSeconds));
        }

        public async Task<float[]> CreateEmbeddingAsync(string input, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(input))
                throw new ArgumentException("El texto de embedding no puede estar vacío.", nameof(input));

            var request = new
            {
                model = Options.EmbeddingModel,
                input
            };

            using JsonDocument doc = await PostJsonAsync("embeddings", request, cancellationToken).ConfigureAwait(false);
            JsonElement root = doc.RootElement;
            JsonElement data = root.GetProperty("data");
            if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                throw new InvalidOperationException("LM Studio no devolvió embeddings.");

            JsonElement emb = data[0].GetProperty("embedding");
            float[] vector = new float[emb.GetArrayLength()];
            int i = 0;
            foreach (JsonElement x in emb.EnumerateArray())
                vector[i++] = x.GetSingle();

            return vector;
        }

        public async Task<string> ChatAsync(IEnumerable<LmStudioChatMessage> messages, CancellationToken cancellationToken = default)
        {
            if (messages is null) throw new ArgumentNullException(nameof(messages));

            var request = new
            {
                model = Options.ChatModel,
                temperature = Options.Temperature,
                max_tokens = Options.MaxTokens,
                messages = messages.Select(ToTextMessage).ToArray()
            };

            using JsonDocument doc = await PostJsonAsync("chat/completions", request, cancellationToken).ConfigureAwait(false);
            return ReadChatContent(doc.RootElement);
        }

        /// <summary>
        /// Chat con respuesta progresiva: entrega cada fragmento de texto tal como llega de
        /// LM Studio (SSE) mediante onDelta. Devuelve el texto completo acumulado.
        /// </summary>
        public async Task<string> ChatStreamAsync(
            IEnumerable<LmStudioChatMessage> messages,
            Func<string, CancellationToken, Task> onDelta,
            CancellationToken cancellationToken = default)
        {
            if (messages is null) throw new ArgumentNullException(nameof(messages));
            if (onDelta is null) throw new ArgumentNullException(nameof(onDelta));

            var payload = new
            {
                model = Options.ChatModel,
                temperature = Options.Temperature,
                max_tokens = Options.MaxTokens,
                stream = true,
                messages = messages.Select(ToTextMessage).ToArray()
            };

            string url = BuildUrl("chat/completions");
            string json = JsonSerializer.Serialize(payload, JsonDefaults.Default);
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(Options.ApiKey))
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Options.ApiKey}");

            using HttpResponseMessage resp = await _http
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                string errorBody = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException($"LM Studio devolvió {(int)resp.StatusCode}: {errorBody}");
            }

            await using Stream stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            var accumulated = new System.Text.StringBuilder();
            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) continue;

                string payloadLine = line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    ? line[5..].Trim()
                    : line.Trim();

                if (payloadLine.Length == 0) continue;
                if (payloadLine.Equals("[DONE]", StringComparison.OrdinalIgnoreCase)) break;

                string? delta = TryReadDelta(payloadLine);
                if (string.IsNullOrEmpty(delta)) continue;

                accumulated.Append(delta);
                await onDelta(delta, cancellationToken).ConfigureAwait(false);
            }

            return accumulated.ToString();
        }

        /// <summary>Extrae el fragmento de un evento SSE de chat (choices[0].delta.content).</summary>
        private static string? TryReadDelta(string json)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("choices", out JsonElement choices)
                    || choices.ValueKind != JsonValueKind.Array
                    || choices.GetArrayLength() == 0)
                {
                    return null;
                }

                JsonElement first = choices[0];
                if (first.TryGetProperty("delta", out JsonElement delta)
                    && delta.ValueKind == JsonValueKind.Object
                    && delta.TryGetProperty("content", out JsonElement content)
                    && content.ValueKind == JsonValueKind.String)
                {
                    return content.GetString();
                }

                if (first.TryGetProperty("message", out JsonElement message)
                    && message.TryGetProperty("content", out JsonElement full)
                    && full.ValueKind == JsonValueKind.String)
                {
                    return full.GetString();
                }

                return null;
            }
            catch (JsonException)
            {
                // LM Studio puede emitir metadatos que no son JSON; se ignoran.
                return null;
            }
        }

        public async Task<ParsedLlmJsonResponse> ChatAsJsonAsync(
            IEnumerable<LmStudioChatMessage> messages,
            string schemaName,
            CancellationToken cancellationToken = default)
        {
            if (messages is null) throw new ArgumentNullException(nameof(messages));
            if (string.IsNullOrWhiteSpace(schemaName)) throw new ArgumentException("schemaName vacío.", nameof(schemaName));

            var request = new
            {
                model = Options.ChatModel,
                temperature = Options.Temperature,
                max_tokens = Options.MaxTokens,
                response_format = BuildJsonSchemaResponseFormat(schemaName),
                messages = messages.Select(ToTextMessage).ToArray()
            };

            using JsonDocument doc = await PostJsonAsync("chat/completions", request, cancellationToken).ConfigureAwait(false);
            string content = ReadChatContent(doc.RootElement);
            string finishReason = ReadFinishReason(doc.RootElement);
            var parsed = LmStudioJsonResponseParser.Parse(content, finishReason);
            if (parsed.Question is null)
                parsed.Question = messages.LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content;
            return parsed;
        }

        public async Task<string> VisionAsync(string imagePath, string prompt, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) throw new ArgumentException("Ruta de imagen inválida.", nameof(imagePath));
            if (!File.Exists(imagePath)) throw new FileNotFoundException("Imagen no encontrada.", imagePath);
            if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Prompt vacío.", nameof(prompt));

            string model = Options.VisionModel ?? Options.ChatModel;
            string dataUrl = ToDataUrl(imagePath);

            var request = new
            {
                model,
                temperature = Options.Temperature,
                max_tokens = Options.MaxTokens,
                messages = new object[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "text", text = prompt },
                            new { type = "image_url", image_url = new { url = dataUrl } }
                        }
                    }
                }
            };

            using JsonDocument doc = await PostJsonAsync("chat/completions", request, cancellationToken).ConfigureAwait(false);
            return ReadChatContent(doc.RootElement);
        }

        public async Task<ParsedLlmJsonResponse> VisionAsJsonAsync(
            string imagePath,
            string prompt,
            string schemaName,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) throw new ArgumentException("Ruta de imagen inválida.", nameof(imagePath));
            if (!File.Exists(imagePath)) throw new FileNotFoundException("Imagen no encontrada.", imagePath);
            if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Prompt vacío.", nameof(prompt));
            if (string.IsNullOrWhiteSpace(schemaName)) throw new ArgumentException("schemaName vacío.", nameof(schemaName));

            string model = Options.VisionModel ?? Options.ChatModel;
            string dataUrl = ToDataUrl(imagePath);

            var request = new
            {
                model,
                temperature = Options.Temperature,
                max_tokens = Options.MaxTokens,
                response_format = BuildJsonSchemaResponseFormat(schemaName),
                messages = new object[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "text", text = prompt },
                            new { type = "image_url", image_url = new { url = dataUrl } }
                        }
                    }
                }
            };

            using JsonDocument doc = await PostJsonAsync("chat/completions", request, cancellationToken).ConfigureAwait(false);
            string content = ReadChatContent(doc.RootElement);
            string finishReason = ReadFinishReason(doc.RootElement);
            return LmStudioJsonResponseParser.Parse(content, finishReason);
        }

        private async Task<JsonDocument> PostJsonAsync(string route, object payload, CancellationToken cancellationToken)
        {
            string url = BuildUrl(route);
            string json = JsonSerializer.Serialize(payload, JsonDefaults.Default);
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(Options.ApiKey))
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Options.ApiKey}");

            using HttpResponseMessage resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"LM Studio devolvió {(int)resp.StatusCode}: {body}");

            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("La respuesta de LM Studio no es JSON válido.", ex);
            }
        }

        private string BuildUrl(string route)
        {
            string baseUrl = Options.BaseUrl.TrimEnd('/');
            route = route.TrimStart('/');
            return $"{baseUrl}/{route}";
        }

        private static object ToTextMessage(LmStudioChatMessage msg)
        {
            return new
            {
                role = string.IsNullOrWhiteSpace(msg.Role) ? "user" : msg.Role,
                content = msg.Content ?? string.Empty
            };
        }

        private static string ReadChatContent(JsonElement root)
        {
            if (!root.TryGetProperty("choices", out JsonElement choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
                throw new InvalidOperationException("LM Studio no devolvió choices.");

            JsonElement message = choices[0].GetProperty("message");
            if (!message.TryGetProperty("content", out JsonElement content))
                return string.Empty;

            if (content.ValueKind == JsonValueKind.String)
                return content.GetString() ?? string.Empty;

            if (content.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (JsonElement part in content.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Object &&
                        part.TryGetProperty("text", out JsonElement text))
                    {
                        sb.AppendLine(text.GetString());
                    }
                }
                return sb.ToString().Trim();
            }

            return content.ToString();
        }

        private static string ReadFinishReason(JsonElement root)
        {
            if (!root.TryGetProperty("choices", out JsonElement choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
                return string.Empty;

            return choices[0].TryGetProperty("finish_reason", out JsonElement finish)
                ? finish.GetString() ?? string.Empty
                : string.Empty;
        }

        private static object BuildJsonSchemaResponseFormat(string schemaName)
        {
            return new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = schemaName,
                    schema = new
                    {
                        type = "object",
                        additionalProperties = false,
                        properties = new Dictionary<string, object>
                        {
                            ["question"] = new { type = "string" },
                            ["answer"] = new { type = "string" },
                            ["confidence"] = new { type = "number" },
                            ["format"] = new { type = "string" }
                        },
                        required = new[] { "question", "answer", "confidence", "format" }
                    }
                }
            };
        }

        private static string ToDataUrl(string imagePath)
        {
            byte[] bytes = File.ReadAllBytes(imagePath);
            string ext = Path.GetExtension(imagePath).ToLowerInvariant();
            string mime = ext switch
            {
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                ".gif" => "image/gif",
                ".tif" => "image/tiff",
                ".tiff" => "image/tiff",
                _ => "application/octet-stream"
            };
            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }

        public void Dispose()
        {
            if (_disposeClient)
                _http.Dispose();
        }
    }

    internal static class JsonDefaults
    {
        // Los enums se escriben como texto ("Completed", no 6): la API la consumen
        // servicios e IAs, y un numero no es explicito. Los archivos JSON ya
        // escritos siguen leyendose porque la conversion acepta ambos formatos.
        private static JsonSerializerOptions Build(bool indented, bool loose)
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = indented,
                PropertyNameCaseInsensitive = loose,
                Converters = { new JsonStringEnumConverter() }
            };

            return options;
        }

        public static JsonSerializerOptions Default { get; } = Build(indented: false, loose: false);

        public static JsonSerializerOptions Indented { get; } = Build(indented: true, loose: false);

        public static JsonSerializerOptions Loose { get; } = Build(indented: false, loose: true);
    }
}
