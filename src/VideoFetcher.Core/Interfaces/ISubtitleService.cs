namespace VideoFetcher.Core.Interfaces;

public sealed record SubtitleCandidate(
    string Id,
    string Language,
    string ReleaseName,
    int DownloadCount,
    string SourceKind,
    string? DownloadUrlOrFileId,
    string? InlineContent = null);

public interface ISubtitleService
{
    Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(string rawPageTitle, string languageCode, CancellationToken ct);
    Task<string> DownloadToSrtAsync(SubtitleCandidate candidate, string destinationPath, CancellationToken ct);
}
