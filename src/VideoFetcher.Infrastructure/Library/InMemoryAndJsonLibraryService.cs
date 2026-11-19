using System.Text.Json;
using VideoFetcher.Core.Interfaces;

namespace VideoFetcher.Infrastructure.Library;

public sealed class JsonLibraryService : ILibraryService
{
    private readonly string _indexFilePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<LibraryVideoEntry> _entries = [];
    private bool _loaded;

    public JsonLibraryService(string libraryRootDirectory)
    {
        Directory.CreateDirectory(libraryRootDirectory);
        _indexFilePath = Path.Combine(libraryRootDirectory, "library_index.json");
    }

    public async Task<LibraryVideoEntry> AddVideoAsync(LibraryVideoEntry entry, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            _entries.Insert(0, entry);
            await SaveAsync(cancellationToken);
            return entry;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<LibraryVideoEntry>> QueryAsync(string? folderCategory, string? searchText, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            return _entries.Where(e =>
                (string.IsNullOrWhiteSpace(folderCategory) || folderCategory == "all" || e.FolderCategory.Equals(folderCategory, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(searchText) || e.Title.Contains(searchText, StringComparison.OrdinalIgnoreCase) || e.CodecSummary.Contains(searchText, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteAsync(string id, bool deleteFileFromDisk, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            var item = _entries.FirstOrDefault(e => e.Id == id);
            if (item is null) return;
            _entries.Remove(item);
            if (deleteFileFromDisk && File.Exists(item.FilePath)) File.Delete(item.FilePath);
            await SaveAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return;
        if (File.Exists(_indexFilePath))
        {
            await using var fs = File.OpenRead(_indexFilePath);
            _entries = await JsonSerializer.DeserializeAsync<List<LibraryVideoEntry>>(fs, cancellationToken: ct) ?? [];
        }
        _loaded = true;
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        await using var fs = File.Create(_indexFilePath);
        await JsonSerializer.SerializeAsync(fs, _entries, new JsonSerializerOptions { WriteIndented = true }, ct);
    }
}
