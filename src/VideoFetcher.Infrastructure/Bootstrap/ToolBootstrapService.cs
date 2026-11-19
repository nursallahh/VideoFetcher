using System.Diagnostics;
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

    public async Task EnsureToolsReadyAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(ToolsDirectory);
        string ytDlpPath = Path.Combine(ToolsDirectory, "yt-dlp.exe");

        if (!File.Exists(ytDlpPath))
        {
            try
            {
                _logger.LogInformation("yt-dlp.exe indiriliyor...");
                var client = _httpFactory.CreateClient("HighSpeedMediaClient");
                using var resp = await client.GetAsync("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe", HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                await using var input = await resp.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(ytDlpPath);
                await input.CopyToAsync(output, 262144, ct);
                _logger.LogInformation("yt-dlp.exe hazır: {Path}", ytDlpPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "yt-dlp.exe otomatik indirilemedi.");
            }
        }
        else
        {
            _ = TryPeriodicUpdateAsync(ytDlpPath);
        }
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
