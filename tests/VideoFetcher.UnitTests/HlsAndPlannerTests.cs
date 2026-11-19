using VideoFetcher.Core.Models;
using VideoFetcher.Core.Services;
using Xunit;

namespace VideoFetcher.UnitTests;

public sealed class HlsAndPlannerTests
{
    [Fact]
    public void ParseMasterPlaylist_ShouldReturnOrderedVariants()
    {
        var parser = new HlsManifestParser();
        string m3u8 = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=1500000,RESOLUTION=1280x720,CODECS="avc1.4d401f,mp4a.40.2"
            720p/index.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=6000000,RESOLUTION=3840x2160,CODECS="hvc1.1.6.L150,mp4a.40.2"
            4k/index.m3u8
            """;

        var result = parser.Parse(m3u8, new Uri("https://cdn.example.com/hls/master.m3u8"), "Test", new Dictionary<string, string>());

        Assert.True(result.IsMaster);
        Assert.False(result.IsEncrypted);
        Assert.Equal(2, result.Variants.Count);
        Assert.Equal("3840x2160", result.Variants[0].Resolution);
    }

    [Fact]
    public void BuildPlan_ShouldPreferLosslessRemux_ForCompatibleCodecs()
    {
        var planner = new ConversionPlanner();
        var candidate = new MediaStreamCandidate("1", "https://ex.com/v.m3u8", "Video", StreamProtocol.Hls, "hvc1", "mp4a", "3840x2160", 6000000, false, null, new Dictionary<string, string>());

        var plan = planner.BuildPlan(candidate, TargetDeviceProfile.MobileCompatible, "h264_nvenc");

        Assert.Equal(ConversionMode.LosslessRemux, plan.Mode);
        Assert.Contains("-c", plan.FfmpegArguments);
        Assert.Contains("copy", plan.FfmpegArguments);
    }

    [Fact]
    public void BuildPlan_ShouldUseHardwareTranscode_WhenAv1TargetsMobile()
    {
        var planner = new ConversionPlanner();
        var candidate = new MediaStreamCandidate("2", "https://ex.com/v.mpd", "Video", StreamProtocol.Dash, "av01", "opus", "2560x1440", 4000000, false, null, new Dictionary<string, string>());

        var plan = planner.BuildPlan(candidate, TargetDeviceProfile.MobileCompatible, "h264_nvenc");

        Assert.Equal(ConversionMode.HardwareTranscode, plan.Mode);
        Assert.True(plan.RequiresUserNotice);
        Assert.Contains("h264_nvenc", plan.FfmpegArguments);
    }

    [Fact]
    public void ParseMasterPlaylist_ShouldExtractSubtitlesTrack()
    {
        var parser = new HlsManifestParser();
        string m3u8 = """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="subs",NAME="Türkçe",LANGUAGE="tr",URI="subs/tr.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1920x1080,SUBTITLES="subs"
            1080p/index.m3u8
            """;

        var result = parser.Parse(m3u8, new Uri("https://cdn.example.com/hls/master.m3u8"), "Test", new Dictionary<string, string>());

        Assert.NotNull(result.Subtitles);
        Assert.Single(result.Subtitles!);
        Assert.Equal("tr", result.Subtitles![0].Language);
        Assert.Equal("https://cdn.example.com/hls/subs/tr.m3u8", result.Subtitles![0].Url.ToString());
    }

    [Fact]
    public void FindMpegTsSyncOffset_ShouldSkipFakeImageHeader()
    {
        byte[] buf = new byte[600];
        // Fake PNG header at 0..7, MPEG-TS 0x47 packets starting at offset 20
        buf[0] = 0x89; buf[1] = 0x50; buf[2] = 0x4E; buf[3] = 0x47;
        buf[20] = 0x47;
        buf[20 + 188] = 0x47;
        buf[20 + 376] = 0x47;

        int offset = VideoFetcher.Infrastructure.Download.HighSpeedDownloadManager.FindMpegTsSyncOffset(buf, buf.Length);
        Assert.Equal(20, offset);
    }
}
