namespace VideoFetcher.Core.Models;

public enum StreamProtocol { DirectFile, Hls, Dash }
public enum TargetDeviceProfile { PcOriginal, MobileCompatible }
public enum ConversionMode { LosslessRemux, HardwareTranscode, SoftwareTranscode }
public enum JobState { Detected, Analyzed, BlockedDrm, Queued, Downloading, Converting, Done, Failed }

public sealed record MediaStreamCandidate(
    string Id,
    string Url,
    string Title,
    StreamProtocol Protocol,
    string VideoCodec,
    string AudioCodec,
    string Resolution,
    long? BandwidthBps,
    bool IsDrmProtected,
    string? DrmSystem,
    IReadOnlyDictionary<string, string> RequestHeaders,
    double DurationSeconds = 0,
    int RelevanceScore = 50,
    string? SeparateAudioUrl = null,
    string? PrefetchedManifestContent = null,
    string? MasterUrl = null);

public sealed record HlsSegment(int Index, Uri Url, double DurationSeconds);

public sealed record HlsSubtitleTrack(string Language, string Name, Uri Url);

public sealed record HlsPlaylistResult(
    bool IsMaster,
    bool IsEncrypted,
    string? KeyMethod,
    IReadOnlyList<MediaStreamCandidate> Variants,
    IReadOnlyList<HlsSegment> Segments,
    IReadOnlyList<HlsSubtitleTrack>? Subtitles = null)
{
    public double TotalDurationSeconds => Segments.Sum(s => s.DurationSeconds);
}

public sealed record ConversionPlan(
    ConversionMode Mode,
    string OutputExtension,
    string[] FfmpegArguments,
    string HumanSummary,
    bool RequiresUserNotice);

public sealed record DownloadProgress(
    string JobId,
    JobState State,
    int CompletedSegments,
    int TotalSegments,
    double Percentage,
    string StatusMessage);
