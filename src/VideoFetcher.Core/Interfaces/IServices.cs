using VideoFetcher.Core.Models;

namespace VideoFetcher.Core.Interfaces;

public interface IHlsManifestParser
{
    HlsPlaylistResult Parse(string manifestContent, Uri baseUri, string pageTitle, IReadOnlyDictionary<string, string> headers);
}

public interface IConversionPlanner
{
    ConversionPlan BuildPlan(MediaStreamCandidate candidate, TargetDeviceProfile target, string availableHwEncoder);
}

public interface IDownloadManager
{
    Task<string> DownloadHlsSegmentsAsync(
        IReadOnlyList<HlsSegment> segments,
        IReadOnlyDictionary<string, string> headers,
        string workingDirectory,
        IProgress<DownloadProgress>? progress,
        string jobId,
        CancellationToken cancellationToken);
}

public interface IConversionEngine
{
    Task<string> ExecuteAsync(
        string inputPath,
        string outputPath,
        ConversionPlan plan,
        CancellationToken cancellationToken);

    Task<string> DetectBestHardwareEncoderAsync(CancellationToken cancellationToken);
}
