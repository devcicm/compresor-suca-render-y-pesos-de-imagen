using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SUCA_Compressor
{
    public sealed class LmStudioModelDescriptor
    {
        public string Id { get; set; } = "";
        public string Object { get; set; } = "";
        public string? Type { get; set; }
        public string? Publisher { get; set; }
        public string? Arch { get; set; }
        public string? Quantization { get; set; }
        public string? State { get; set; }
        public int? MaxContextLength { get; set; }
        public int? LoadedContextLength { get; set; }
    }

    public sealed class LmStudioModelAdmin
    {
        private readonly LmStudioOptions _options;

        public LmStudioModelAdmin(LmStudioOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public async Task<IReadOnlyList<LmStudioModelDescriptor>> ListDetailedModelsAsync(CancellationToken cancellationToken = default)
        {
            using var client = new LmStudioClient(_options);
            using var doc = await QueryJsonAsync(client, "http://127.0.0.1:1234/api/v0/models", cancellationToken).ConfigureAwait(false);

            var models = new List<LmStudioModelDescriptor>();
            if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
                return models;

            foreach (JsonElement item in data.EnumerateArray())
            {
                models.Add(new LmStudioModelDescriptor
                {
                    Id = GetString(item, "id") ?? "",
                    Object = GetString(item, "object") ?? "",
                    Type = GetString(item, "type"),
                    Publisher = GetString(item, "publisher"),
                    Arch = GetString(item, "arch"),
                    Quantization = GetString(item, "quantization"),
                    State = GetString(item, "state"),
                    MaxContextLength = GetInt(item, "max_context_length"),
                    LoadedContextLength = GetInt(item, "loaded_context_length")
                });
            }

            return models;
        }

        public Task<string> LoadAsync(string modelId, CancellationToken cancellationToken = default) =>
            RunCliAsync($"load -y \"{modelId}\"", cancellationToken);

        public Task<string> UnloadAsync(string modelId, CancellationToken cancellationToken = default) =>
            RunCliAsync($"unload \"{modelId}\"", cancellationToken);

        public Task<string> UnloadAllAsync(CancellationToken cancellationToken = default) =>
            RunCliAsync("unload --all", cancellationToken);

        private static async Task<JsonDocument> QueryJsonAsync(LmStudioClient client, string url, CancellationToken cancellationToken)
        {
            using var http = new System.Net.Http.HttpClient();
            string body = await http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            return JsonDocument.Parse(body);
        }

        private static async Task<string> RunCliAsync(string arguments, CancellationToken cancellationToken)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "lms",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            string stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            string stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"lms {arguments} failed: {stderr}");

            return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
        }

        private static string? GetString(JsonElement element, string name)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString();
            }

            return null;
        }

        private static int? GetInt(JsonElement element, string name)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out int value))
                    return value;
            }

            return null;
        }
    }
}
