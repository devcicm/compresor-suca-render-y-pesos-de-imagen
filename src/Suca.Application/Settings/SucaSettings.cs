using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SUCA_Compressor.Shared.Imaging;

namespace SUCA_Compressor.Application.Settings
{
    public enum SucaSettingsScope { System, User }

    // Nullable members mean "not configured": the effective value falls back
    // to the system scope, then to SucaSettings.DefaultValue.
    // Preset reutilizable de opciones de conversion. Vive en los ajustes para que
    // la UI (web) y la consola puedan aplicar el mismo perfil con un solo paso.
    public sealed class ConversionPreset
    {
        public string? Name { get; set; }
        public string? Engine { get; set; }
        public string? Format { get; set; }
        public int? Quality { get; set; }
        public int? MaxWidth { get; set; }
        public int? MaxHeight { get; set; }
        public bool? RemoveMetadata { get; set; }
        public string? Collection { get; set; }
    }

    public sealed class ConversionSettings
    {
        public string? Engine { get; set; }
        public string? Format { get; set; }
        public int? Quality { get; set; }
        public int? MaxWidth { get; set; }
        public int? MaxHeight { get; set; }
        public bool? RemoveMetadata { get; set; }
        public string? OutputDirectory { get; set; }

        /// <summary>Perfiles guardados de conversion (Web optimizado, Thumbnail, ...).</summary>
        public List<ConversionPreset> Presets { get; set; } = new();
    }

    public sealed class WebSettings
    {
        public int? MaxUploadMegabytes { get; set; }
        public int? GalleryPageSize { get; set; }
        public int? ConvertTimeoutSeconds { get; set; }
        public int? ConvertParallelism { get; set; }
    }

    public sealed class ConsoleSettings
    {
        public int? TimeoutSeconds { get; set; }
        public bool? Verbose { get; set; }
        public string? OutputDirectory { get; set; }
        public int? Parallelism { get; set; }
        public bool? Recursive { get; set; }
    }

    public sealed class RagSettings
    {
        public string? SourceDirectory { get; set; }
        public string? SemanticIndexPath { get; set; }
        public int? TopK { get; set; }
        public int? ChunkSize { get; set; }
        public int? ChunkOverlap { get; set; }
        public bool? Recursive { get; set; }
        public bool? RefreshIndexBeforeQuery { get; set; }
    }

    public sealed class LmStudioSettings
    {
        public string? BaseUrl { get; set; }
        public string? ChatModel { get; set; }
        public string? EmbeddingModel { get; set; }
    }

    public sealed class SucaSettings
    {
        public int SchemaVersion { get; set; } = 1;
        public ConversionSettings Conversion { get; set; } = new();
        public WebSettings Web { get; set; } = new();
        public ConsoleSettings Console { get; set; } = new();
        public LmStudioSettings LmStudio { get; set; } = new();
        public RagSettings Rag { get; set; } = new();

        public static SucaSettings DefaultValue() => new()
        {
            Conversion = new ConversionSettings
            {
                Engine = "suca",
                Format = "webp",
                Quality = 80,
                RemoveMetadata = true
            },
            Web = new WebSettings
            {
                MaxUploadMegabytes = 50,
                GalleryPageSize = 24,
                ConvertTimeoutSeconds = 180,
                ConvertParallelism = 3
            },
            Console = new ConsoleSettings
            {
                TimeoutSeconds = 180,
                Verbose = false,
                Parallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 4)),
                Recursive = false
            },
            LmStudio = new LmStudioSettings
            {
                BaseUrl = "http://127.0.0.1:1234/v1"
            },
            Rag = new RagSettings
            {
                TopK = 4,
                ChunkSize = 1200,
                ChunkOverlap = 180,
                Recursive = true,
                RefreshIndexBeforeQuery = true
            }
        };
    }

    public sealed class EffectiveSettings
    {
        public SucaSettings Values { get; init; } = new();
        public IReadOnlyDictionary<string, string> Sources { get; init; } = new Dictionary<string, string>();
    }

    public interface ISucaSettingsService
    {
        SucaSettings GetEffective();
        EffectiveSettings GetEffectiveDetailed();
        string GetScopeJson(SucaSettingsScope scope);
        Task UpdateAsync(SucaSettingsScope scope, string mergedJson, CancellationToken cancellationToken = default);
        Task ResetAsync(SucaSettingsScope scope, CancellationToken cancellationToken = default);
    }

    public sealed class JsonSucaSettingsService : ISucaSettingsService
    {
        private readonly string _settingsRoot;
        private readonly string _userFileName;
        private readonly IAtomicFileStore _fileStore;
        private readonly IAsyncKeyedLock _keyedLock;
        private readonly IRuntimeObservability _observability;
        private readonly ILogger<JsonSucaSettingsService> _logger;

        public JsonSucaSettingsService(
            string settingsRoot,
            IAtomicFileStore fileStore,
            IAsyncKeyedLock keyedLock,
            IRuntimeObservability observability,
            ILogger<JsonSucaSettingsService> logger)
        {
            if (string.IsNullOrWhiteSpace(settingsRoot))
                throw new ArgumentException("settingsRoot vacío.", nameof(settingsRoot));

            _settingsRoot = Path.GetFullPath(settingsRoot);
            _fileStore = fileStore;
            _keyedLock = keyedLock;
            _observability = observability;
            _logger = logger;
            _userFileName = $"{SanitizeUserName(Environment.UserName)}.json";
        }

        public SucaSettings GetEffective() => GetEffectiveDetailed().Values;

        public EffectiveSettings GetEffectiveDetailed()
        {
            JsonObject system = ReadScopeObject(SucaSettingsScope.System);
            JsonObject user = ReadScopeObject(SucaSettingsScope.User);

            JsonObject merged = CloneDefaultObject();
            Merge(merged, system);
            Merge(merged, user);

            var values = Deserialize(merged);
            var sources = BuildSources(system, user);
            return new EffectiveSettings { Values = values, Sources = sources };
        }

        public string GetScopeJson(SucaSettingsScope scope)
        {
            string path = ScopePath(scope);
            if (!File.Exists(path))
                return "{}";

            return File.ReadAllText(path, Encoding.UTF8);
        }

        public async Task UpdateAsync(SucaSettingsScope scope, string mergedJson, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(mergedJson))
                throw new ArgumentException("El documento de ajustes está vacío.", nameof(mergedJson));

            // Validate before persisting: the patch must deserialize onto the model.
            JsonNode? patch = JsonNode.Parse(mergedJson) ?? throw new ArgumentException("JSON de ajustes inválido.");
            JsonObject candidate = CloneDefaultObject();
            Merge(candidate, patch.AsObject());
            Deserialize(candidate);

            string path = ScopePath(scope);
            // Compound key: AtomicFileStore takes the file key itself inside
            // WriteAllTextAtomicAsync, and AsyncKeyedLock is not reentrant.
            await using var lease = await _keyedLock.AcquireAsync(path + "#compound", cancellationToken).ConfigureAwait(false);

            JsonObject current = ReadScopeObjectUnsafe(scope);
            Merge(current, patch.AsObject());

            // Round-trip through the model so unknown keys and bad types are dropped.
            JsonObject normalized = JsonSerializer.SerializeToNode(Deserialize(current), JsonDefaults.Indented)?.AsObject() ?? new JsonObject();
            string json = normalized.ToJsonString(JsonDefaults.Indented);

            await _fileStore.WriteAllTextAtomicAsync(path, json, cancellationToken).ConfigureAwait(false);
            _observability.RecordSuccess("settings-update", $"{scope}:{path}");
            _logger.LogInformation("Settings scope {Scope} updated at {Path}", scope, path);
        }

        public async Task ResetAsync(SucaSettingsScope scope, CancellationToken cancellationToken = default)
        {
            string path = ScopePath(scope);
            await using var lease = await _keyedLock.AcquireAsync(path, cancellationToken).ConfigureAwait(false);

            if (File.Exists(path))
                File.Delete(path);

            _observability.RecordSuccess("settings-reset", scope.ToString());
        }

        private string ScopePath(SucaSettingsScope scope) => scope switch
        {
            SucaSettingsScope.System => Path.Combine(_settingsRoot, "system.json"),
            _ => Path.Combine(_settingsRoot, "users", _userFileName)
        };

        private JsonObject ReadScopeObject(SucaSettingsScope scope)
        {
            try
            {
                return ReadScopeObjectUnsafe(scope);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Settings scope {Scope} is unreadable; falling back to defaults.", scope);
                _observability.RecordFailure("settings-load", ex);
                return new JsonObject();
            }
        }

        private JsonObject ReadScopeObjectUnsafe(SucaSettingsScope scope)
        {
            string path = ScopePath(scope);
            if (!File.Exists(path))
                return new JsonObject();

            JsonNode? node = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
            return node as JsonObject ?? new JsonObject();
        }

        private static JsonObject CloneDefaultObject() =>
            (JsonSerializer.SerializeToNode(SucaSettings.DefaultValue(), JsonDefaults.Indented) as JsonObject)!;

        private static SucaSettings Deserialize(JsonObject node)
        {
            var settings = node.Deserialize<SucaSettings>(JsonDefaults.Loose) ?? SucaSettings.DefaultValue();
            Validate(settings);
            return settings;
        }

        private static void Validate(SucaSettings settings)
        {
            ValidateRange(settings.Conversion.Quality, 1, 100, "conversion.quality");
            ValidateRange(settings.Web.MaxUploadMegabytes, 1, 2048, "web.maxUploadMegabytes");
            ValidateRange(settings.Web.GalleryPageSize, 1, 200, "web.galleryPageSize");
            ValidateRange(settings.Web.ConvertTimeoutSeconds, 5, 3600, "web.convertTimeoutSeconds");
            ValidateRange(settings.Web.ConvertParallelism, 1, 8, "web.convertParallelism");
            ValidateRange(settings.Console.TimeoutSeconds, 5, 3600, "console.timeoutSeconds");
            ValidateRange(settings.Console.Parallelism, 1, 16, "console.parallelism");
            ValidateRange(settings.Rag.TopK, 1, 12, "rag.topK");
            ValidateRange(settings.Rag.ChunkSize, 200, 4000, "rag.chunkSize");
            ValidateRange(settings.Rag.ChunkOverlap, 0, 800, "rag.chunkOverlap");

            if (!string.IsNullOrWhiteSpace(settings.Conversion.Engine))
            {
                string engine = settings.Conversion.Engine.Trim().ToLowerInvariant();
                if (engine is not ("direct" or "suca"))
                    throw new ArgumentException("conversion.engine solo admite 'direct' o 'suca'.");
            }

            if (!string.IsNullOrWhiteSpace(settings.Conversion.Format)
                && !SucaFormatConverter.TryParseFormat(settings.Conversion.Format, out _))
            {
                throw new ArgumentException("conversion.format solo admite 'webp', 'jpeg' o 'png'.");
            }

            ValidatePresets(settings.Conversion.Presets);
        }

        private static void ValidatePresets(List<ConversionPreset>? presets)
        {
            if (presets is null)
                return;

            if (presets.Count > 24)
                throw new ArgumentException("conversion.presets admite un maximo de 24 presets.");

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < presets.Count; index++)
            {
                ConversionPreset preset = presets[index];
                string name = preset.Name?.Trim() ?? "";
                if (name.Length is 0 or > 40)
                    throw new ArgumentException($"conversion.presets[{index}].Name debe tener entre 1 y 40 caracteres.");

                if (!names.Add(name))
                    throw new ArgumentException($"El preset '{name}' esta duplicado.");

                if (preset.Quality is < 1 or > 100)
                    throw new ArgumentException($"El preset '{name}' tiene una calidad fuera de rango (1-100).");

                if (!string.IsNullOrWhiteSpace(preset.Format)
                    && !SucaFormatConverter.TryParseFormat(preset.Format, out _))
                {
                    throw new ArgumentException($"El preset '{name}' usa un formato no soportado ({preset.Format}).");
                }

                if (!string.IsNullOrWhiteSpace(preset.Engine)
                    && preset.Engine.Trim().ToLowerInvariant() is not ("direct" or "suca"))
                {
                    throw new ArgumentException($"El preset '{name}' solo admite motor 'direct' o 'suca'.");
                }

                if (preset.MaxWidth is <= 0 or > 20000 || preset.MaxHeight is <= 0 or > 20000)
                    throw new ArgumentException($"El preset '{name}' tiene dimensiones fuera de rango (1-20000).");
            }
        }

        private static void ValidateRange(int? value, int min, int max, string name)
        {
            if (value.HasValue && (value.Value < min || value.Value > max))
                throw new ArgumentException($"{name} debe estar entre {min} y {max}.");
        }

        private Dictionary<string, string> BuildSources(JsonObject system, JsonObject user)
        {
            var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            JsonObject effective = CloneDefaultObject();
            Merge(effective, system);
            Merge(effective, user);

            CollectLeaves(effective, string.Empty, (path, _) =>
            {
                if (ContainsLeaf(user, path))
                    sources[path] = "user";
                else if (ContainsLeaf(system, path))
                    sources[path] = "system";
                else
                    sources[path] = "default";
            });

            return sources;
        }

        private static void CollectLeaves(JsonNode? node, string prefix, Action<string, JsonNode?> visit)
        {
            if (node is JsonObject obj)
            {
                foreach (var property in obj)
                {
                    string path = string.IsNullOrEmpty(prefix) ? property.Key : $"{prefix}.{property.Key}";
                    CollectLeaves(property.Value, path, visit);
                }

                return;
            }

            if (!string.IsNullOrEmpty(prefix))
                visit(prefix, node);
        }

        private static bool ContainsLeaf(JsonObject? container, string path)
        {
            JsonNode? current = container;
            foreach (string segment in path.Split('.'))
            {
                if (current is not JsonObject obj || !obj.ContainsKey(segment))
                    return false;

                current = obj[segment];
            }

            return current is not null and not JsonObject;
        }

        private static void Merge(JsonObject target, JsonObject patch)
        {
            foreach (var property in patch)
            {
                string key = property.Key;
                JsonNode? patchValue = property.Value;

                if (patchValue is JsonObject patchObject)
                {
                    if (target[key] is JsonObject targetObject)
                    {
                        Merge(targetObject, patchObject);
                    }
                    else
                    {
                        var clone = new JsonObject();
                        Merge(clone, patchObject);
                        target[key] = clone;
                    }
                }
                else if (patchValue is null)
                {
                    target.Remove(key);
                }
                else
                {
                    target[key] = JsonNode.Parse(patchValue.ToJsonString());
                }
            }
        }

        private static string SanitizeUserName(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName))
                return "default";

            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string(userName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return clean.Trim();
        }
    }

    // Central resolution of local runtime locations. Works from the repository
    // root, from Visual Studio F5, from a published layout, or from explicit
    // environment variables (SUCA_WEB_ROOT, SUCA_STORAGE_ROOT, SUCA_SETTINGS_ROOT)
    // so the repo behaves the same on any machine it is cloned to.
    public static class SucaRuntimePaths
    {
        private const string ProjectFileName = "ConsoleApp suca compress.csproj";

        public static string ResolveRepositoryRoot()
        {
            foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            {
                string? dir = start;
                for (int depth = 0; depth < 8 && !string.IsNullOrEmpty(dir); depth++)
                {
                    if (File.Exists(Path.Combine(dir, ProjectFileName)))
                        return dir;

                    dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }
            }

            // Portable/published layout: keep everything relative to the binary.
            return AppContext.BaseDirectory;
        }

        public static string DefaultSettingsRoot()
        {
            string? configured = Environment.GetEnvironmentVariable("SUCA_SETTINGS_ROOT");
            if (!string.IsNullOrWhiteSpace(configured))
                return Path.GetFullPath(configured);

            return Path.Combine(ResolveRepositoryRoot(), "runtime", "settings");
        }

        public static string DefaultWebStorageRoot()
        {
            string? configured = Environment.GetEnvironmentVariable("SUCA_STORAGE_ROOT");
            if (!string.IsNullOrWhiteSpace(configured))
                return Path.GetFullPath(configured);

            return Path.Combine(ResolveRepositoryRoot(), "runtime", "web");
        }

        public static string ResolveWebUiRoot()
        {
            string? configured = Environment.GetEnvironmentVariable("SUCA_WEB_ROOT");
            if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
                return Path.GetFullPath(configured);

            foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            {
                string? dir = start;
                for (int depth = 0; depth < 8 && !string.IsNullOrEmpty(dir); depth++)
                {
                    string candidate = Path.Combine(dir, "src", "Suca.Web", "wwwroot");
                    if (Directory.Exists(candidate))
                        return candidate;

                    dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }
            }

            return string.Empty;
        }
    }
}
