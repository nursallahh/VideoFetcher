using VideoFetcher.Core.Models;

namespace VideoFetcher.Core.Interfaces;

public sealed record HighSpeedDownloadOptions(
    int MaxConcurrency = 16,
    int ChunkSizeBytes = 4 * 1024 * 1024,
    long? MaxBytesPerSecond = null);

public sealed record DashAdaptationResult(
    bool IsDrmProtected,
    string? DrmScheme,
    IReadOnlyList<MediaStreamCandidate> VideoTracks,
    IReadOnlyList<MediaStreamCandidate> AudioTracks);

public interface IDashManifestParser
{
    DashAdaptationResult Parse(string mpdXml, Uri baseUri, string pageTitle, IReadOnlyDictionary<string, string> headers);
}

public interface IExtractorBridge
{
    Task<IReadOnlyList<MediaStreamCandidate>> ExtractAsync(
        string pageUrl,
        string? netscapeCookiesFilePath,
        CancellationToken cancellationToken);

    Task<string> DownloadWithYtDlpAsync(
        string url,
        string outputPath,
        IReadOnlyDictionary<string, string> headers,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IHighSpeedDownloadManager : IDownloadManager
{
    Task<string> DownloadDirectRangeParallelAsync(
        Uri url,
        IReadOnlyDictionary<string, string> headers,
        string destinationPath,
        HighSpeedDownloadOptions options,
        IProgress<DownloadProgress>? progress,
        string jobId,
        CancellationToken cancellationToken);
}
