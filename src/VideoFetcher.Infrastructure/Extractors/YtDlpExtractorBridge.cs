using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VideoFetcher.Core.Interfaces;
using VideoFetcher.Core.Models;
using VideoFetcher.Infrastructure.Bootstrap;

namespace VideoFetcher.Infrastructure.Extractors;

public sealed partial class YtDlpExtractorBridge : IExtractorBridge
{
    private readonly ToolBootstrapService _bootstrap;
    private readonly ILogger<YtDlpExtractorBridge> _logger;

    [GeneratedRegex(@"(\d+(?:\.\d+)?)%")]
    private static partial Regex PercentRegex();

    public YtDlpExtractorBridge(ToolBootstrapService bootstrap, ILogger<YtDlpExtractorBridge> logger)
    {
        _bootstrap = bootstrap;
        _logger = logger;
    }

    private static string ResolveYtDlpPath()
    {
        var candidates = new[]
        {
            Path.Combine(ToolBootstrapService.ToolsDirectory, "yt-dlp.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "yt-dlp.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "yt-dlp.exe"),
            "yt-dlp"
        };
        foreach (var c in candidates)
            if (c == "yt-dlp" || File.Exists(c)) return c;
        return "yt-dlp";
    }

    private static string? ResolveFfmpegDir()
    {
        var candidates = new[]
        {
            Path.Combine(ToolBootstrapService.ToolsDirectory, "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "ffmpeg.exe")
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return Path.GetDirectoryName(c);
        return null;
    }

    public async Task<IReadOnlyList<MediaStreamCandidate>> ExtractAsync(
        string pageUrl,
        string? netscapeCookiesFilePath,
        CancellationToken cancellationToken)
    {
        await _bootstrap.EnsureToolsReadyAsync(cancellationToken);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ResolveYtDlpPath(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("--dump-single-json");
            psi.ArgumentList.Add("--no-warnings");
            psi.ArgumentList.Add("--skip-download");
            psi.ArgumentList.Add("--no-playlist");

            if (!string.IsNullOrWhiteSpace(netscapeCookiesFilePath) && File.Exists(netscapeCookiesFilePath))
            {
                psi.ArgumentList.Add("--cookies");
                psi.ArgumentList.Add(netscapeCookiesFilePath);
            }
            psi.ArgumentList.Add(pageUrl);

            using var proc = Process.Start(psi);
            if (proc is null) return [];

            await using var reg = cancellationToken.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch { } });
            string stdout = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
            string stderr = await proc.StandardError.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken);

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                _logger.LogWarning("yt-dlp sonuç döndürmedi ({Code}): {Err}", proc.ExitCode, stderr);
                return [];
            }
            return ParseYtDlpJson(stdout);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "yt-dlp çalıştırılamadı.");
            return [];
        }
    }

    public async Task<string> DownloadWithYtDlpAsync(
        string url,
        string outputPath,
        IReadOnlyDictionary<string, string> headers,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        await _bootstrap.EnsureToolsReadyAsync(cancellationToken);

        var psi = new ProcessStartInfo
        {
            FileName = ResolveYtDlpPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("bv*[ext=mp4]+ba[ext=m4a]/bv*+ba/b");
        psi.ArgumentList.Add("--merge-output-format");
        psi.ArgumentList.Add("mp4");
        psi.ArgumentList.Add("--concurrent-fragments");
        psi.ArgumentList.Add("64");
        psi.ArgumentList.Add("--newline");
        psi.ArgumentList.Add("--no-warnings");

        string? ffDir = ResolveFfmpegDir();
        if (!string.IsNullOrWhiteSpace(ffDir))
        {
            psi.ArgumentList.Add("--ffmpeg-location");
            psi.ArgumentList.Add(ffDir);
        }

        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outputPath);

        if (headers.TryGetValue("Referer", out var refVal))
        {
            psi.ArgumentList.Add("--referer");
            psi.ArgumentList.Add(refVal);
        }
        if (headers.TryGetValue("User-Agent", out var uaVal))
        {
            psi.ArgumentList.Add("--user-agent");
            psi.ArgumentList.Add(uaVal);
        }
        if (headers.TryGetValue("Origin", out var origVal))
        {
            psi.ArgumentList.Add("--add-header");
            psi.ArgumentList.Add($"Origin:{origVal}");
        }
        if (headers.TryGetValue("Cookie", out var cookieVal))
        {
            psi.ArgumentList.Add("--add-header");
            psi.ArgumentList.Add($"Cookie:{cookieVal}");
        }

        psi.ArgumentList.Add(url);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("yt-dlp başlatılamadı.");
        await using var reg = cancellationToken.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch { } });

        while (await proc.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            var m = PercentRegex().Match(line);
            if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double pct))
            {
                progress?.Report(new DownloadProgress("ytdlp", JobState.Downloading, (int)pct, 100, pct, $"yt-dlp (64 Kanal): %{pct:F1}"));
            }
        }

        string err = await proc.StandardError.ReadToEndAsync(cancellationToken);
        await proc.WaitForExitAsync(cancellationToken);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"yt-dlp indirme hatası ({proc.ExitCode}): {err}");

        return outputPath;
    }

    public static IReadOnlyList<MediaStreamCandidate> ParseYtDlpJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "Video" : "Video";
        if (!root.TryGetProperty("formats", out var formats) || formats.ValueKind != JsonValueKind.Array) return [];

        var results = new List<MediaStreamCandidate>();
        foreach (var fmt in formats.EnumerateArray())
        {
            string url = fmt.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(url)) continue;
            string vcodec = fmt.TryGetProperty("vcodec", out var vc) ? vc.GetString() ?? "none" : "none";
            string acodec = fmt.TryGetProperty("acodec", out var ac) ? ac.GetString() ?? "none" : "none";
            if (vcodec == "none" && acodec == "none") continue;
            string ext = fmt.TryGetProperty("ext", out var ex) ? ex.GetString() ?? "mp4" : "mp4";
            string res = fmt.TryGetProperty("resolution", out var r) ? r.GetString() ?? "Auto" : "Auto";
            long? tbr = fmt.TryGetProperty("tbr", out var tb) && tb.TryGetDouble(out var d) ? (long)(d * 1000) : null;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (fmt.TryGetProperty("http_headers", out var hh) && hh.ValueKind == JsonValueKind.Object)
                foreach (var prop in hh.EnumerateObject())
                    if (prop.Value.ValueKind == JsonValueKind.String) headers[prop.Name] = prop.Value.GetString()!;

            results.Add(new MediaStreamCandidate(
                fmt.TryGetProperty("format_id", out var fid) ? fid.GetString() ?? "fmt" : "fmt",
                url, title, ext.Contains("m3u8") ? StreamProtocol.Hls : StreamProtocol.DirectFile,
                vcodec, acodec, res, tbr, false, null, headers));
        }
        return results.OrderByDescending(c => c.BandwidthBps ?? 0).ToList();
    }
}
