using System.Threading;

namespace SUCA_Compressor.Kernel
{
    // The kernel owns only deterministic image compression and its direct metrics.
    public interface ISucaCompressionKernel
    {
        CompressionResult Process(
            string inputPath,
            string outputDirectory,
            SucaProfile profile,
            SucaOptions options,
            CancellationToken cancellationToken = default);
    }
}
