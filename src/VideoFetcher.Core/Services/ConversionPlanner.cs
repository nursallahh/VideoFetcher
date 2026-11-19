using VideoFetcher.Core.Interfaces;
using VideoFetcher.Core.Models;

namespace VideoFetcher.Core.Services;

public sealed class ConversionPlanner : IConversionPlanner
{
    private static readonly string[] Mp4SafeVideoPrefixes = ["h264", "avc1", "hevc", "hvc1", "hev1"];
    private static readonly string[] Mp4SafeAudioPrefixes = ["aac", "mp4a", "mp3"];

    public ConversionPlan BuildPlan(
        MediaStreamCandidate candidate,
        TargetDeviceProfile target,
        string availableHwEncoder)
    {
        if (candidate.IsDrmProtected)
        {
            throw new InvalidOperationException("DRM korumalı içerik için dönüştürme planı oluşturulamaz.");
        }

        bool videoSafe = Mp4SafeVideoPrefixes.Any(p => candidate.VideoCodec.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        bool audioSafe = Mp4SafeAudioPrefixes.Any(p => candidate.AudioCodec.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        // Kural 1: Önce kayıpsız remux (-c copy) denenir
        if (target == TargetDeviceProfile.PcOriginal || (videoSafe && audioSafe))
        {
            return new ConversionPlan(
                Mode: ConversionMode.LosslessRemux,
                OutputExtension: (videoSafe && audioSafe) ? ".mp4" : ".mkv",
                FfmpegArguments: ["-c", "copy", "-movflags", "+faststart"],
                HumanSummary: "Kayıpsız Remux (-c copy): Orijinal kalite, FPS ve HDR metadatası %100 korunur.",
                RequiresUserNotice: false);
        }

        // Yalnızca hedef mobil cihaz codec'i desteklemiyorsa transcode yapılır
        bool hasHw = !string.IsNullOrWhiteSpace(availableHwEncoder) && availableHwEncoder != "libx264";
        var mode = hasHw ? ConversionMode.HardwareTranscode : ConversionMode.SoftwareTranscode;
        var videoArgs = availableHwEncoder switch
        {
            "h264_nvenc" => new[] { "-c:v", "h264_nvenc", "-preset", "p5", "-cq", "19" },
            "h264_qsv"   => new[] { "-c:v", "h264_qsv", "-global_quality", "20" },
            "h264_amf"   => new[] { "-c:v", "h264_amf", "-quality", "quality" },
            _            => new[] { "-c:v", "libx264", "-crf", "18", "-preset", "medium" }
        };

        var args = new List<string> { "-map", "0:v:0", "-map", "0:a:0?" };
        args.AddRange(videoArgs);
        args.AddRange(["-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]);

        return new ConversionPlan(
            Mode: mode,
            OutputExtension: ".mp4",
            FfmpegArguments: [.. args],
            HumanSummary: $"Mobil uyumlu MP4 dönüştürme ({availableHwEncoder}): Yüksek kalite korunarak yeniden kodlanacak.",
            RequiresUserNotice: true);
    }
}
