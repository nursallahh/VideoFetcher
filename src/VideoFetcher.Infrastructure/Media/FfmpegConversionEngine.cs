using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using VideoFetcher.Core.Interfaces;
using VideoFetcher.Infrastructure.Bootstrap;
using VideoFetcher.Core.Models;

namespace VideoFetcher.Infrastructure.Media;

public sealed class FfmpegConversionEngine : IConversionEngine
{
    private readonly ILogger<FfmpegConversionEngine> _logger;
    private readonly string _ffmpegExecutable;
    private string? _cachedHwEncoder;
    public string LastDiagnosticLog { get; private set; } = "";

    public FfmpegConversionEngine(ILogger<FfmpegConversionEngine> logger)
    {
        _logger = logger;
        _ffmpegExecutable = ResolveToolPath("ffmpeg.exe", "ffmpeg");
    }

    private static string ResolveToolPath(string exeName, string fallback)
    {
        var candidates = new[]
        {
            Path.Combine(ToolBootstrapService.ToolsDirectory, exeName),
            Path.Combine(AppContext.BaseDirectory, "tools", exeName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", exeName),
            fallback
        };
        foreach (var c in candidates)
            if (c == fallback || File.Exists(c)) return c;
        return fallback;
    }

    public async Task<string> DetectBestHardwareEncoderAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_cachedHwEncoder)) return _cachedHwEncoder;
        foreach (var hw in new[] { "h264_nvenc", "h264_qsv", "h264_amf" })
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _ffmpegExecutable,
                    Arguments = $"-y -f lavfi -i color=c=black:s=256x256:d=0.1 -vframes 1 -c:v {hw} -f null -",
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc is null) continue;
                await proc.WaitForExitAsync(cancellationToken);
                if (proc.ExitCode == 0) return _cachedHwEncoder = hw;
            }
            catch { }
        }
        return _cachedHwEncoder = "libx264";
    }

    public async Task<string> ExecuteAsync(string concatOrInputPath, string outputPath, ConversionPlan plan, CancellationToken cancellationToken)
    {
        var args = new List<string> { "-y" };
        if (concatOrInputPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            args.AddRange(["-f", "concat", "-safe", "0"]);
        args.AddRange(["-fflags", "+genpts", "-err_detect", "ignore_err", "-i", concatOrInputPath]);
        args.AddRange(plan.FfmpegArguments);
        args.Add(outputPath);

        try
        {
            await RunFfmpegAsync(args, cancellationToken);
        }
        catch when (args.Contains("aac_adtstoasc"))
        {
            var fallbackArgs = args.Where(x => x != "-bsf:a" && x != "aac_adtstoasc").ToList();
            await RunFfmpegAsync(fallbackArgs, cancellationToken);
        }
        return outputPath;
    }

    public async Task RemuxHlsToMp4Async(string combinedStreamPath, string outputPath, CancellationToken cancellationToken)
    {
        var losslessArgs = new[]
        {
            "-y",
            "-analyzeduration", "100M", "-probesize", "100M",
            "-err_detect", "ignore_err",
            "-i", combinedStreamPath,
            "-map", "0:v:0",
            "-map", "0:a:0?",
            "-c:v", "copy",
            "-c:a", "copy",
            "-bsf:a", "aac_adtstoasc",
            "-avoid_negative_ts", "make_zero",
            "-movflags", "+faststart",
            outputPath
        };
        try
        {
            await RunFfmpegAsync(losslessArgs, cancellationToken);
            return;
        }
        catch { }

        var aacFallback = new[]
        {
            "-y",
            "-analyzeduration", "100M", "-probesize", "100M",
            "-err_detect", "ignore_err",
            "-i", combinedStreamPath,
            "-map", "0:v:0",
            "-map", "0:a:0?",
            "-c:v", "copy",
            "-c:a", "aac", "-b:a", "192k", "-ac", "2",
            "-avoid_negative_ts", "make_zero",
            "-movflags", "+faststart",
            outputPath
        };
        try
        {
            await RunFfmpegAsync(aacFallback, cancellationToken);
            return;
        }
        catch { }

        var reencode = new[]
        {
            "-y",
            "-analyzeduration", "100M", "-probesize", "100M",
            "-err_detect", "ignore_err",
            "-i", combinedStreamPath,
            "-map", "0:v:0",
            "-map", "0:a:0?",
            "-c:v", "libx264", "-crf", "18", "-preset", "veryfast", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "192k", "-ac", "2",
            "-avoid_negative_ts", "make_zero",
            "-movflags", "+faststart",
            outputPath
        };
        await RunFfmpegAsync(reencode, cancellationToken);
    }

    public async Task MuxSeparateVideoAndAudioAsync(string videoInputPath, string audioInputPath, string outputPath, CancellationToken cancellationToken)
    {
        var losslessArgs = new List<string> { "-y" };
        if (videoInputPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            losslessArgs.AddRange(["-f", "concat", "-safe", "0"]);
        losslessArgs.AddRange(["-analyzeduration", "100M", "-probesize", "100M", "-err_detect", "ignore_err", "-i", videoInputPath]);

        if (audioInputPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            losslessArgs.AddRange(["-f", "concat", "-safe", "0"]);
        losslessArgs.AddRange(["-analyzeduration", "100M", "-probesize", "100M", "-err_detect", "ignore_err", "-i", audioInputPath]);

        bool isMp4Audio = audioInputPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                       || audioInputPath.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase);
        losslessArgs.AddRange(["-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy", "-c:a", "copy"]);
        if (!isMp4Audio) losslessArgs.AddRange(["-bsf:a", "aac_adtstoasc"]);
        losslessArgs.AddRange(["-avoid_negative_ts", "make_zero", "-movflags", "+faststart", "-shortest", outputPath]);

        try
        {
            await RunFfmpegAsync(losslessArgs, cancellationToken);
            return;
        }
        catch { }

        var aacFallback = new[]
        {
            "-y",
            "-analyzeduration", "100M", "-probesize", "100M", "-err_detect", "ignore_err", "-i", videoInputPath,
            "-analyzeduration", "100M", "-probesize", "100M", "-err_detect", "ignore_err", "-i", audioInputPath,
            "-map", "0:v:0", "-map", "1:a:0",
            "-c:v", "copy",
            "-c:a", "aac", "-b:a", "192k", "-ac", "2",
            "-avoid_negative_ts", "make_zero",
            "-movflags", "+faststart",
            "-shortest",
            outputPath
        };
        await RunFfmpegAsync(aacFallback, cancellationToken);
    }

    public async Task EnsureCompatibleAudioAsync(string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        string audioCodec = await ProbeAudioCodecAsync(inputPath, cancellationToken);
        if (string.Equals(audioCodec, "aac", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(audioCodec))
        {
            if (!string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
                File.Move(inputPath, outputPath, overwrite: true);
            return;
        }

        var args = new[]
        {
            "-y", "-i", inputPath,
            "-map", "0:v:0", "-map", "0:a:0?",
            "-c:v", "copy",
            "-c:a", "aac", "-b:a", "192k", "-ac", "2", "-ar", "48000",
            "-movflags", "+faststart",
            outputPath
        };
        await RunFfmpegAsync(args, cancellationToken);
        try { if (File.Exists(inputPath) && !string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase)) File.Delete(inputPath); } catch { }
    }

    public async Task<bool> IsHdrVideoAsync(string videoPath, CancellationToken cancellationToken)
    {
        try
        {
            string ffprobe = ResolveToolPath("ffprobe.exe", "ffprobe");
            var psi = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v error -select_streams v:0 -show_entries stream=pix_fmt,color_space,color_transfer,color_primaries -of csv=p=0 \"{videoPath}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            string info = (await proc.StandardOutput.ReadToEndAsync(cancellationToken)).ToLowerInvariant();
            await proc.WaitForExitAsync(cancellationToken);
            return info.Contains("smpte2084") || info.Contains("arib-std-b67") || info.Contains("bt2020") || info.Contains("p10le");
        }
        catch { return false; }
    }

    public async Task<bool> EnsureSdrColorsIfHdrAsync(string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        if (!await IsHdrVideoAsync(inputPath, cancellationToken))
            return false;

        string hw = await DetectBestHardwareEncoderAsync(cancellationToken);
        var encArgs = hw switch
        {
            "h264_nvenc" => new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-cq", "19" },
            "h264_qsv"   => new[] { "-c:v", "h264_qsv", "-global_quality", "20" },
            "h264_amf"   => new[] { "-c:v", "h264_amf", "-quality", "balanced" },
            _            => new[] { "-c:v", "libx264", "-crf", "18", "-preset", "veryfast" }
        };

        var primaryArgs = new List<string>
        {
            "-y", "-i", inputPath,
            "-map", "0:v:0", "-map", "0:a?",
            "-vf", "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p"
        };
        primaryArgs.AddRange(encArgs);
        primaryArgs.AddRange(["-c:a", "copy", "-movflags", "+faststart", outputPath]);

        try
        {
            await RunFfmpegAsync(primaryArgs, cancellationToken);
            return true;
        }
        catch
        {
            var fallbackArgs = new List<string>
            {
                "-y", "-i", inputPath,
                "-map", "0:v:0", "-map", "0:a?",
                "-vf", "colorspace=all=bt709:iall=bt2020:fast=1,format=yuv420p"
            };
            fallbackArgs.AddRange(encArgs);
            fallbackArgs.AddRange(["-c:a", "copy", "-movflags", "+faststart", outputPath]);
            await RunFfmpegAsync(fallbackArgs, cancellationToken);
            return true;
        }
    }

    public async Task<int> ProbeVideoHeightAsync(string videoPath, CancellationToken cancellationToken)
    {
        try
        {
            string ffprobe = ResolveToolPath("ffprobe.exe", "ffprobe");
            var psi = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v error -select_streams v:0 -show_entries stream=height -of csv=p=0 \"{videoPath}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return 0;
            string outStr = (await proc.StandardOutput.ReadToEndAsync(cancellationToken)).Trim();
            await proc.WaitForExitAsync(cancellationToken);
            return int.TryParse(outStr, out int h) ? h : 0;
        }
        catch { return 0; }
    }

    public async Task EnhanceCinemaQualityAsync(string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        bool isHdr = await IsHdrVideoAsync(inputPath, cancellationToken);
        int height = await ProbeVideoHeightAsync(inputPath, cancellationToken);
        string hw = await DetectBestHardwareEncoderAsync(cancellationToken);
        var encArgs = hw switch
        {
            "h264_nvenc" => new[] { "-c:v", "h264_nvenc", "-preset", "p1", "-cq", "20" },
            "h264_qsv"   => new[] { "-c:v", "h264_qsv", "-preset", "veryfast", "-global_quality", "20" },
            "h264_amf"   => new[] { "-c:v", "h264_amf", "-quality", "speed" },
            _            => new[] { "-c:v", "libx264", "-crf", "18", "-preset", "ultrafast", "-threads", "0" }
        };

        bool isLowRes = height > 0 && height < 1080;
        string enhanceFilter = isLowRes
            ? "cas=0.45,eq=contrast=1.02:brightness=-0.015:saturation=1.03:gamma=0.98,format=yuv420p"
            : "eq=contrast=1.04:brightness=-0.02:saturation=1.08:gamma=0.98,format=yuv420p";

        string vf = isHdr
            ? $"zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,{enhanceFilter}"
            : enhanceFilter;

        var args = new List<string> { "-y", "-i", inputPath, "-map", "0:v:0", "-map", "0:a?", "-vf", vf };
        args.AddRange(encArgs);
        args.AddRange(["-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", "-c:a", "copy", "-movflags", "+faststart", outputPath]);

        try
        {
            await RunFfmpegAsync(args, cancellationToken);
        }
        catch
        {
            string safeFilter = isLowRes
                ? "unsharp=5:5:0.45:5:5:0.0,eq=contrast=1.02:saturation=1.03,format=yuv420p"
                : "eq=contrast=1.04:saturation=1.08,format=yuv420p";
            string fallbackVf = isHdr ? $"colorspace=all=bt709:iall=bt2020:fast=1,{safeFilter}" : safeFilter;
            var fallback = new List<string> { "-y", "-i", inputPath, "-map", "0:v:0", "-map", "0:a?", "-vf", fallbackVf };
            fallback.AddRange(encArgs);
            fallback.AddRange(["-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", "-c:a", "copy", "-movflags", "+faststart", outputPath]);
            await RunFfmpegAsync(fallback, cancellationToken);
        }
    }

    public async Task TrimVideoAsync(string inputPath, string outputPath, string? startTime, string? endTime, CancellationToken cancellationToken)
    {
        var args = new List<string> { "-y" };
        if (!string.IsNullOrWhiteSpace(startTime)) args.AddRange(["-ss", startTime.Trim()]);
        if (!string.IsNullOrWhiteSpace(endTime)) args.AddRange(["-to", endTime.Trim()]);
        args.AddRange(["-i", inputPath, "-c", "copy", "-movflags", "+faststart", outputPath]);
        await RunFfmpegAsync(args, cancellationToken);
    }

    public async Task EmbedSoftSubtitleAsync(string videoPath, string subtitlePath, string langIso639_2, string outputPath, CancellationToken cancellationToken)
    {
        // Videonun gerçek başlangıç zamanını (start_time) okuyup altyazıyı otomatik hizala
        double startOffset = await ProbeFormatStartTimeAsync(videoPath, cancellationToken);
        var args = new List<string> { "-y", "-i", videoPath };
        if (Math.Abs(startOffset) > 0.05)
            args.AddRange(["-itsoffset", startOffset.ToString("0.###", CultureInfo.InvariantCulture)]);

        args.AddRange([
            "-i", subtitlePath,
            "-map", "0:v", "-map", "0:a?", "-map", "1:0",
            "-c:v", "copy", "-c:a", "copy", "-c:s", "mov_text",
            "-metadata:s:s:0", $"language={langIso639_2}",
            "-disposition:s:0", "default",
            "-movflags", "+faststart",
            outputPath
        ]);
        await RunFfmpegAsync(args, cancellationToken);
    }

    private async Task<double> ProbeFormatStartTimeAsync(string videoPath, CancellationToken cancellationToken)
    {
        try
        {
            string ffprobe = ResolveToolPath("ffprobe.exe", "ffprobe");
            var psi = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v error -show_entries format=start_time -of csv=p=0 \"{videoPath}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return 0;
            string outStr = (await proc.StandardOutput.ReadToEndAsync(cancellationToken)).Trim();
            await proc.WaitForExitAsync(cancellationToken);
            return double.TryParse(outStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double st) ? st : 0;
        }
        catch { return 0; }
    }

    public async Task<string> ProbeAudioCodecAsync(string videoPath, CancellationToken cancellationToken)
    {
        try
        {
            string ffprobe = ResolveToolPath("ffprobe.exe", "ffprobe");
            var psi = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v error -select_streams a:0 -show_entries stream=codec_name -of csv=p=0 \"{videoPath}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return "";
            string outStr = (await proc.StandardOutput.ReadToEndAsync(cancellationToken)).Trim();
            await proc.WaitForExitAsync(cancellationToken);
            return outStr;
        }
        catch { return ""; }
    }

    public async Task<string> ProbeResolutionSummaryAsync(string videoPath, CancellationToken cancellationToken)
    {
        try
        {
            string ffprobe = ResolveToolPath("ffprobe.exe", "ffprobe");
            var psi = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v error -select_streams v:0 -show_entries stream=width,height,codec_name -of csv=p=0 \"{videoPath}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return "";
            string outStr = (await proc.StandardOutput.ReadToEndAsync(cancellationToken)).Trim();
            await proc.WaitForExitAsync(cancellationToken);
            var parts = outStr.Split(',');
            if (parts.Length >= 3) return $"{parts[1]}x{parts[2]} ({parts[0].ToUpperInvariant()})";
        }
        catch { }
        return "";
    }

    private async Task RunFfmpegAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo { FileName = _ffmpegExecutable, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        process.Start();
        await using var reg = cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });

        string stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        LastDiagnosticLog = stderr;
        if (process.ExitCode != 0)
        {
            _logger.LogError("FFmpeg hatası ({Code}): {Err}", process.ExitCode, stderr);
            string lastErr = stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
            throw new InvalidOperationException($"FFmpeg ({process.ExitCode}): {lastErr}");
        }
    }
}
