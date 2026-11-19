using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using VideoFetcher.Core.Interfaces;
using VideoFetcher.Core.Models;

namespace VideoFetcher.Infrastructure.Download;

public sealed class ParallelSegmentDownloader : IDownloadManager
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ParallelSegmentDownloader> _logger;

    public ParallelSegmentDownloader(
        IHttpClientFactory httpClientFactory,
        ILogger<ParallelSegmentDownloader> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string> DownloadHlsSegmentsAsync(
        IReadOnlyList<HlsSegment> segments,
        IReadOnlyDictionary<string, string> headers,
        string workingDirectory,
        IProgress<DownloadProgress>? progress,
        string jobId,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workingDirectory);
        var client = _httpClientFactory.CreateClient("MediaDownloader");
        var segmentPaths = new ConcurrentDictionary<int, string>();
        int completed = 0;

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = 8,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(segments, options, async (seg, ct) =>
        {
            string localPath = Path.Combine(workingDirectory, $"seg_{seg.Index:D5}.ts");
            await DownloadWithRetryAsync(client, seg.Url, headers, localPath, ct);
            segmentPaths[seg.Index] = localPath;

            int doneCount = Interlocked.Increment(ref completed);
            progress?.Report(new DownloadProgress(
                JobId: jobId,
                State: JobState.Downloading,
                CompletedSegments: doneCount,
                TotalSegments: segments.Count,
                Percentage: Math.Round((double)doneCount / segments.Count * 100, 1),
                StatusMessage: $"Segment indiriliyor ({doneCount}/{segments.Count})"));
        });

        // FFmpeg concat demuxer dosyası oluştur
        string concatFile = Path.Combine(workingDirectory, "concat_list.txt");
        await using (var writer = new StreamWriter(concatFile, false))
        {
            for (int i = 0; i < segments.Count; i++)
            {
                string escaped = segmentPaths[i].Replace("'", "'\\''");
                await writer.WriteLineAsync($"file '{escaped}'");
            }
        }

        _logger.LogInformation("Job {JobId}: {Count} segment indirildi.", jobId, segments.Count);
        return concatFile;
    }

    private static async Task DownloadWithRetryAsync(
        HttpClient client,
        Uri url,
        IReadOnlyDictionary<string, string> headers,
        string destinationPath,
        CancellationToken ct)
    {
        const int maxRetries = 3;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                foreach (var kv in headers)
                {
                    req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                }

                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();

                await using var input = await resp.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(destinationPath);
                await input.CopyToAsync(output, ct);
                return;
            }
            catch (Exception) when (attempt < maxRetries && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt)), ct);
            }
        }
    }
}
