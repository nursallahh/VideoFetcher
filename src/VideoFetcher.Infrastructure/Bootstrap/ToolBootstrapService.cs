using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace VideoFetcher.Infrastructure.Bootstrap;

public sealed class ToolBootstrapService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ToolBootstrapService> _logger;

    public static string ToolsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VideoFetcher", "tools");

    public ToolBootstrapService(IHttpClientFactory httpFactory, ILogger<ToolBootstrapService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public static bool IsToolAvailable(string exeName)
    {
        var candidates = new[]
        {
            Path.Combine(ToolsDirectory, exeName),
            Path.Combine(AppContext.BaseDirectory, "tools", exeName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", exeName)
        };
        if (candidates.Any(File.Exists)) return true;

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    if (File.Exists(Path.Combine(dir.Trim(), exeName))) return true;
                }
                catch { }
            }
        }
        return false;
    }

    public IReadOnlyList<string> GetMissingTools()
    {
        var missing = new List<string>();
        if (!IsToolAvailable("yt-dlp.exe")) missing.Add("yt-dlp.exe");
        if (!IsToolAvailable("ffmpeg.exe")) missing.Add("ffmpeg.exe");
        if (!IsToolAvailable("ffprobe.exe")) missing.Add("ffprobe.exe");
        return missing;
    }

    public async Task DownloadMissingToolsAsync(IProgress<string>? status, CancellationToken ct)
    {
        Directory.CreateDirectory(ToolsDirectory);
        var client = _httpFactory.CreateClient("HighSpeedMediaClient");

        if (!IsToolAvailable("yt-dlp.exe"))
        {
            status?.Report("yt-dlp.exe indiriliyor...");
            string ytDlpPath = Path.Combine(ToolsDirectory, "yt-dlp.exe");
            using var resp = await client.GetAsync("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe", HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var input = await resp.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(ytDlpPath);
            await input.CopyToAsync(output, 262144, ct);
        }

        if (!IsToolAvailable("ffmpeg.exe") || !IsToolAvailable("ffprobe.exe"))
        {
            status?.Report("FFmpeg ve FFprobe indiriliyor (~95 MB)...");
            string zipTmp = Path.Combine(Path.GetTempPath(), $"ffmpeg_{Guid.NewGuid():N}.zip");
            try
            {
                using var resp = await client.GetAsync("https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip", HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                await using (var input = await resp.Content.ReadAsStreamAsync(ct))
                await using (var fs = File.Create(zipTmp))
                {
                    await input.CopyToAsync(fs, 524288, ct);
                }

                status?.Report("FFmpeg ve FFprobe ayıklanıyor...");
                using var archive = ZipFile.OpenRead(zipTmp);
                foreach (var entry in archive.Entries)
                {
                    if (entry.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)
                     || entry.Name.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        string dest = Path.Combine(ToolsDirectory, entry.Name);
                        entry.ExtractToFile(dest, overwrite: true);
                    }
                }
            }
            finally
            {
                try { if (File.Exists(zipTmp)) File.Delete(zipTmp); } catch { }
            }
        }

        status?.Report("Tüm medya araçları hazır ✓");
    }

    public async Task EnsureToolsReadyAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(ToolsDirectory);
        string ytDlpPath = Path.Combine(ToolsDirectory, "yt-dlp.exe");
        if (File.Exists(ytDlpPath))
        {
            _ = TryPeriodicUpdateAsync(ytDlpPath);
        }
        await Task.CompletedTask;
    }

    private async Task TryPeriodicUpdateAsync(string ytDlpPath)
    {
        try
        {
            string stampFile = Path.Combine(ToolsDirectory, ".ytdlp_last_update");
            if (File.Exists(stampFile) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(stampFile)).TotalDays < 7)
                return;

            await File.WriteAllTextAsync(stampFile, DateTime.UtcNow.ToString("O"));
            var psi = new ProcessStartInfo { FileName = ytDlpPath, Arguments = "-U", UseShellExecute = false, CreateNoWindow = true };
            using var proc = Process.Start(psi);
            if (proc is not null) await proc.WaitForExitAsync();
        }
        catch { }
    }
}
