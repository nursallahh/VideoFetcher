using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using VideoFetcher.Core.Interfaces;
using VideoFetcher.Core.Models;

namespace VideoFetcher.Infrastructure.Download;

/// <summary>
/// 1000+ Mbps ve yavaş bağlantılara otomatik uyum sağlayan adaptif AIMD paralel indirme motoru (32 -> 64 kanal).
/// </summary>
public sealed class HighSpeedDownloadManager : IHighSpeedDownloadManager
{
    private const int IoBufferBytes = 512 * 1024;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HighSpeedDownloadManager> _logger;

    public HighSpeedDownloadManager(
        IHttpClientFactory httpClientFactory,
        ILogger<HighSpeedDownloadManager> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string> DownloadDirectRangeParallelAsync(
        Uri url,
        IReadOnlyDictionary<string, string> headers,
        string destinationPath,
        HighSpeedDownloadOptions options,
        IProgress<DownloadProgress>? progress,
        string jobId,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("HighSpeedMediaClient");
        var (supportsRange, totalBytes) = await ProbeRangeSupportAsync(client, url, headers, cancellationToken);

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        long adaptiveChunkSize = totalBytes switch
        {
            > 512L * 1024 * 1024 => 8L * 1024 * 1024,
            > 64L * 1024 * 1024  => 4L * 1024 * 1024,
            _                    => Math.Min(options.ChunkSizeBytes, 2L * 1024 * 1024)
        };

        if (!supportsRange || totalBytes <= adaptiveChunkSize)
        {
            await DownloadSingleStreamAsync(client, url, headers, destinationPath, cancellationToken);
            progress?.Report(new DownloadProgress(jobId, JobState.Done, 1, 1, 100, "Tek akışta indirildi"));
            return destinationPath;
        }

        var ranges = new List<(int Index, long Start, long End)>();
        int idx = 0;
        for (long offset = 0; offset < totalBytes; offset += adaptiveChunkSize)
        {
            long end = Math.Min(offset + adaptiveChunkSize - 1, totalBytes - 1);
            ranges.Add((idx++, offset, end));
        }

        long downloadedBytes = 0;
        var sw = Stopwatch.StartNew();
        int maxCap = 64;
        using var gate = new AdaptiveChannelGate(initialChannels: Math.Min(32, maxCap), minChannels: 8, maxChannels: maxCap);

        using SafeFileHandle handle = File.OpenHandle(destinationPath, FileMode.Create, FileAccess.Write, FileShare.Write, FileOptions.Asynchronous);
        RandomAccess.SetLength(handle, totalBytes);

        var workers = ranges.Select(async chunk =>
        {
            await gate.WaitAsync(cancellationToken);
            var itemSw = Stopwatch.StartNew();
            try
            {
                bool throttled = await DownloadChunkWithRetryAsync(client, url, headers, handle, chunk.Start, chunk.End, gate, cancellationToken);
                if (!throttled) gate.ReportSuccess(itemSw.ElapsedMilliseconds);

                long current = Interlocked.Add(ref downloadedBytes, chunk.End - chunk.Start + 1);
                double elapsed = Math.Max(0.2, sw.Elapsed.TotalSeconds);
                double mbps = (current * 8.0) / (elapsed * 1_000_000.0);
                double pct = Math.Min(100.0, Math.Round((double)current / totalBytes * 100, 1));
                progress?.Report(new DownloadProgress(jobId, JobState.Downloading, (int)(current / 1024), (int)(totalBytes / 1024), pct,
                    $"{gate.CurrentLimit} Kanal • {mbps:F0} Mbps • İndiriliyor (%{pct:F1})"));
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(workers);
        _logger.LogInformation("Job {JobId}: {Bytes} bayt adaptif {Threads} kanal ile indirildi.", jobId, totalBytes, gate.CurrentLimit);
        return destinationPath;
    }

    public async Task<string> DownloadHlsSegmentsAsync(
        IReadOnlyList<HlsSegment> segments,
        IReadOnlyDictionary<string, string> headers,
        string workingDirectory,
        IProgress<DownloadProgress>? progress,
        string jobId,
        CancellationToken cancellationToken)
    {
        if (segments.Count == 0)
            throw new InvalidOperationException("HLS segment listesi boş.");

        Directory.CreateDirectory(workingDirectory);
        var client = _httpClientFactory.CreateClient("HighSpeedMediaClient");
        var segmentPaths = new ConcurrentDictionary<int, string>();
        int completed = 0;
        long totalBytes = 0;
        var sw = Stopwatch.StartNew();

        // 32 kanal ile başla, hatasız akışta 64 kanala kadar çık; 429/503 veya yavaşlıkta otomatik düşür
        int initial = Math.Min(32, Math.Max(8, segments.Count));
        using var gate = new AdaptiveChannelGate(initialChannels: initial, minChannels: 8, maxChannels: 64);

        var workers = segments.Select(async seg =>
        {
            await gate.WaitAsync(cancellationToken);
            var segSw = Stopwatch.StartNew();
            try
            {
                string localPath = Path.Combine(workingDirectory, $"seg_{seg.Index:D5}.ts");
                long bytes = 0;
                if (!File.Exists(localPath))
                {
                    bytes = await DownloadSegmentWithRetryAsync(client, seg.Url, headers, localPath, gate, cancellationToken);
                }
                else
                {
                    bytes = new FileInfo(localPath).Length;
                }
                segmentPaths[seg.Index] = localPath;
                gate.ReportSuccess(segSw.ElapsedMilliseconds);

                long curBytes = Interlocked.Add(ref totalBytes, bytes);
                int done = Interlocked.Increment(ref completed);
                double elapsed = Math.Max(0.2, sw.Elapsed.TotalSeconds);
                double mbps = (curBytes * 8.0) / (elapsed * 1_000_000.0);
                double pct = Math.Round((double)done / segments.Count * 100, 1);

                progress?.Report(new DownloadProgress(
                    jobId,
                    JobState.Downloading,
                    done,
                    segments.Count,
                    pct,
                    $"{gate.CurrentLimit} Kanal • {mbps:F0} Mbps • HLS ({done}/{segments.Count})"));
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(workers);

        // Segmentleri ikili (binary) birleştir; sahte PNG/GIF/JPG başlıklarını (0x47 TS sync öncesi) otomatik temizle
        var ordered = segments.OrderBy(s => s.Index).ToList();
        bool isFmp4 = false;
        bool isAdtsAac = false;
        bool isTs = false;
        if (ordered.Count > 0 && segmentPaths.TryGetValue(ordered[0].Index, out var firstFile) && File.Exists(firstFile))
        {
            byte[] probe = new byte[8192];
            await using var pf = File.OpenRead(firstFile);
            int pn = await pf.ReadAsync(probe, cancellationToken);
            if (pn >= 8)
            {
                string box = System.Text.Encoding.ASCII.GetString(probe, 4, 4);
                if (box is "ftyp" or "styp" or "sidx" or "moof") isFmp4 = true;
                else if (probe[0] == 0xFF && (probe[1] & 0xF0) == 0xF0) isAdtsAac = true;
                else if (probe[0] == 0x47 || FindMpegTsSyncOffset(probe, pn) > 0) isTs = true;
            }
        }

        string ext = isFmp4 ? ".mp4" : isAdtsAac ? ".aac" : isTs ? ".ts" : ".bin";
        string combinedPath = Path.Combine(workingDirectory, "combined_stream" + ext);
        byte[] streamBuf = new byte[512 * 1024];
        await using (var outStream = new FileStream(combinedPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
        {
            foreach (var seg in ordered)
            {
                if (segmentPaths.TryGetValue(seg.Index, out var segFile) && File.Exists(segFile))
                {
                    await using var inStream = new FileStream(segFile, FileMode.Open, FileAccess.Read, FileShare.Read, 512 * 1024, useAsync: true);
                    int firstRead = await inStream.ReadAsync(streamBuf.AsMemory(0, streamBuf.Length), cancellationToken);
                    if (firstRead > 0)
                    {
                        int offset = isTs ? FindMpegTsSyncOffset(streamBuf, firstRead) : 0;
                        await outStream.WriteAsync(streamBuf.AsMemory(offset, firstRead - offset), cancellationToken);
                        int read;
                        while ((read = await inStream.ReadAsync(streamBuf.AsMemory(0, streamBuf.Length), cancellationToken)) > 0)
                        {
                            await outStream.WriteAsync(streamBuf.AsMemory(0, read), cancellationToken);
                        }
                    }
                }
            }
        }

        return combinedPath;
    }

    public static int FindMpegTsSyncOffset(byte[] data, int length)
    {
        if (length == 0 || data[0] == 0x47) return 0;
        int limit = Math.Min(8192, length - 377);
        for (int i = 1; i <= limit; i++)
        {
            if (data[i] == 0x47 && data[i + 188] == 0x47 && data[i + 376] == 0x47)
                return i;
        }
        return 0;
    }

    private static async Task<(bool SupportsRange, long TotalBytes)> ProbeRangeSupportAsync(
        HttpClient client,
        Uri url,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Head, url);
        foreach (var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        bool accepts = resp.Headers.AcceptRanges.Contains("bytes", StringComparer.OrdinalIgnoreCase);
        long len = resp.Content.Headers.ContentLength ?? 0;
        return (accepts && len > 0, len);
    }

    private static async Task<bool> DownloadChunkWithRetryAsync(
        HttpClient client,
        Uri url,
        IReadOnlyDictionary<string, string> headers,
        SafeFileHandle handle,
        long start,
        long end,
        AdaptiveChannelGate gate,
        CancellationToken ct)
    {
        bool hadThrottle = false;
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                foreach (var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                req.Headers.Range = new RangeHeaderValue(start, end);

                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.StatusCode == HttpStatusCode.TooManyRequests || resp.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    hadThrottle = true;
                    gate.ReportThrottle();
                    throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");
                }
                resp.EnsureSuccessStatusCode();

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                byte[] buffer = new byte[IoBufferBytes];
                long writeOffset = start;
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), writeOffset, ct);
                    writeOffset += read;
                }
                return hadThrottle;
            }
            catch when (attempt < 4 && !ct.IsCancellationRequested)
            {
                if (!hadThrottle) gate.ReportThrottle();
                await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1) + Random.Shared.Next(50, 150)), ct);
            }
        }
        return hadThrottle;
    }

    private static async Task<long> DownloadSegmentWithRetryAsync(
        HttpClient client,
        Uri url,
        IReadOnlyDictionary<string, string> headers,
        string path,
        AdaptiveChannelGate gate,
        CancellationToken ct)
    {
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                foreach (var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.StatusCode == HttpStatusCode.TooManyRequests || resp.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    gate.ReportThrottle();
                    throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");
                }
                resp.EnsureSuccessStatusCode();

                await using var input = await resp.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, IoBufferBytes, useAsync: true);
                await input.CopyToAsync(output, IoBufferBytes, ct);
                return output.Length;
            }
            catch when (attempt < 4 && !ct.IsCancellationRequested)
            {
                gate.ReportThrottle();
                await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1) + Random.Shared.Next(50, 150)), ct);
            }
        }
        throw new HttpRequestException($"Segment indirilemedi: {url}");
    }

    private static async Task DownloadSingleStreamAsync(
        HttpClient client,
        Uri url,
        IReadOnlyDictionary<string, string> headers,
        string path,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var input = await resp.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, IoBufferBytes, useAsync: true);
        await input.CopyToAsync(output, IoBufferBytes, ct);
    }

    /// <summary>
    /// AIMD (Additive Increase / Multiplicative Decrease) adaptif eşzamanlılık kapısı.
    /// </summary>
    private sealed class AdaptiveChannelGate : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly int _min;
        private readonly int _max;
        private readonly object _lock = new();
        private int _currentLimit;
        private int _activeCount;
        private int _successStreak;

        public int CurrentLimit => Volatile.Read(ref _currentLimit);

        public AdaptiveChannelGate(int initialChannels, int minChannels, int maxChannels)
        {
            _min = minChannels;
            _max = maxChannels;
            _currentLimit = Math.Clamp(initialChannels, minChannels, maxChannels);
            _semaphore = new SemaphoreSlim(_max, _max);
        }

        public async Task WaitAsync(CancellationToken ct)
        {
            while (true)
            {
                await _semaphore.WaitAsync(ct);
                lock (_lock)
                {
                    if (_activeCount < _currentLimit)
                    {
                        _activeCount++;
                        return;
                    }
                }
                _semaphore.Release();
                await Task.Delay(25, ct);
            }
        }

        public void Release()
        {
            lock (_lock)
            {
                if (_activeCount > 0) _activeCount--;
            }
            _semaphore.Release();
        }

        public void ReportSuccess(long elapsedMs)
        {
            lock (_lock)
            {
                // Yavaş bağlantılarda (>2.5 sn/parça) kanalı gereksiz şişirme; hızlı bağlantıda 64'e çıkar
                if (elapsedMs > 2500) return;
                _successStreak++;
                if (_successStreak >= 10 && _currentLimit < _max)
                {
                    _currentLimit = Math.Min(_max, _currentLimit + 4);
                    _successStreak = 0;
                }
            }
        }

        public void ReportThrottle()
        {
            lock (_lock)
            {
                _successStreak = 0;
                _currentLimit = Math.Max(_min, _currentLimit / 2);
            }
        }

        public void Dispose() => _semaphore.Dispose();
    }
}
