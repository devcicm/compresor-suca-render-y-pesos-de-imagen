using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SUCA_Compressor.Shared.Imaging;

namespace SUCA_Compressor.Application.Artifacts
{
    // Optional post-processing. These settings never alter the kernel's compression decision.
    public sealed class CompressionArtifactsOptions
    {
        public bool GenerateVisualEmbedding { get; set; }
        public bool WriteDatasetRecord { get; set; }
        public string? DatasetPath { get; set; }
        public string? VisualRagIndexPath { get; set; }
    }

    public interface ICompressionArtifactsPublisher
    {
        Task PublishAsync(
            CompressionArtifactsOptions? options,
            CompressionResult result,
            CancellationToken cancellationToken = default);
    }

    public sealed class CompressionArtifactsPublisher : ICompressionArtifactsPublisher
    {
        private readonly IAtomicFileStore _fileStore;
        private readonly IAsyncKeyedLock _keyedLock;

        public CompressionArtifactsPublisher(IAtomicFileStore fileStore, IAsyncKeyedLock keyedLock)
        {
            _fileStore = fileStore;
            _keyedLock = keyedLock;
        }

        public async Task PublishAsync(
            CompressionArtifactsOptions? options,
            CompressionResult result,
            CancellationToken cancellationToken = default)
        {
            if (options is null || result is null) return;

            bool needsEmbedding = options.GenerateVisualEmbedding ||
                                  options.WriteDatasetRecord ||
                                  !string.IsNullOrWhiteSpace(options.VisualRagIndexPath);
            float[]? embedding = needsEmbedding
                ? VisualEmbeddingEngine.BuildImageEmbedding(result.OutputPath, cancellationToken)
                : null;

            if (options.WriteDatasetRecord && !string.IsNullOrWhiteSpace(options.DatasetPath))
            {
                DatasetRecord record = DatasetBuilder.FromResult(result, embedding, result.Analysis.Kind.ToString());
                string json = JsonSerializer.Serialize(record, JsonDefaults.Default);
                await _fileStore.AppendLineAsync(options.DatasetPath, json, cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(options.VisualRagIndexPath) && embedding is not null)
            {
                string indexPath = options.VisualRagIndexPath;
                await using var lease = await _keyedLock.AcquireAsync(indexPath, cancellationToken).ConfigureAwait(false);

                LocalRagIndex index = LocalRagIndex.Load(indexPath);
                index.Add(new RagItem
                {
                    SourcePath = result.OutputPath,
                    Text = RagTextProjector.BuildImageKnowledgeText(result),
                    Embedding = embedding,
                    Metadata = new Dictionary<string, string>
                    {
                        ["kind"] = result.Analysis.Kind.ToString(),
                        ["format"] = result.Decision.OutputFormat.ToString(),
                        ["profileHash"] = result.Decision.Hash.ToString()
                    }
                });

                string json = JsonSerializer.Serialize(index.Items, JsonDefaults.Indented);
                await _fileStore.WriteAllTextAtomicAsync(indexPath, json, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
