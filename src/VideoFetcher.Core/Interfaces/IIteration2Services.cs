namespace VideoFetcher.Core.Interfaces;

public sealed record TransferSession(
    string SessionId,
    string FileName,
    string LocalUrl,
    DateTimeOffset ExpiresAtUtc);

public sealed record LibraryVideoEntry(
    string Id,
    string Title,
    string FilePath,
    string ThumbnailPath,
    string FolderCategory,
    string Resolution,
    string CodecSummary,
    long FileSizeBytes,
    DateTimeOffset CreatedAtUtc);

public interface ITransferService : IAsyncDisposable
{
    Task<TransferSession> StartShareSessionAsync(string filePath, TimeSpan? ttl, CancellationToken cancellationToken);
    Task StopSessionAsync(string sessionId);
}

public interface INotificationService
{
    void ShowStreamReadyNotification(string jobId, string title, string summaryText);
    void ShowDrmBlockedNotification(string title, string drmSystem);
}

public interface ILibraryService
{
    Task<LibraryVideoEntry> AddVideoAsync(LibraryVideoEntry entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<LibraryVideoEntry>> QueryAsync(string? folderCategory, string? searchText, CancellationToken cancellationToken);
    Task DeleteAsync(string id, bool deleteFileFromDisk, CancellationToken cancellationToken);
}
