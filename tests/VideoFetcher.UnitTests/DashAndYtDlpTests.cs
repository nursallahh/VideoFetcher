using VideoFetcher.Core.Services;
using VideoFetcher.Infrastructure.Extractors;
using Xunit;

namespace VideoFetcher.UnitTests;

public sealed class DashAndYtDlpTests
{
    [Fact]
    public void ParseDashMpd_ShouldSeparateVideoAndAudioAndDetectDrm()
    {
        var parser = new DashManifestParser();
        string mpd = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011">
              <Period>
                <AdaptationSet mimeType="video/mp4" contentType="video">
                  <ContentProtection schemeIdUri="urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed" />
                  <Representation id="1" bandwidth="5000000" width="1920" height="1080" codecs="av01.0.08M.08">
                    <BaseURL>video_1080p.mp4</BaseURL>
                  </Representation>
                </AdaptationSet>
                <AdaptationSet mimeType="audio/webm" contentType="audio">
                  <Representation id="2" bandwidth="160000" codecs="opus">
                    <BaseURL>audio.webm</BaseURL>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var result = parser.Parse(mpd, new Uri("https://cdn.example.com/dash/manifest.mpd"), "Film", new Dictionary<string, string>());

        Assert.True(result.IsDrmProtected);
        Assert.Single(result.VideoTracks);
        Assert.Single(result.AudioTracks);
        Assert.Equal("av01.0.08M.08", result.VideoTracks[0].VideoCodec);
    }

    [Fact]
    public void ParseYtDlpJson_ShouldExtractFormatsAndHeaders()
    {
        string json = """
            {
              "title": "Sample Video",
              "formats": [
                {
                  "format_id": "137",
                  "url": "https://cdn.example.com/1080p.mp4",
                  "ext": "mp4",
                  "vcodec": "avc1.640028",
                  "acodec": "none",
                  "resolution": "1920x1080",
                  "tbr": 4200.5,
                  "http_headers": { "Referer": "https://example.com/" }
                }
              ]
            }
            """;

        var list = YtDlpExtractorBridge.ParseYtDlpJson(json);

        Assert.Single(list);
        Assert.Equal("Sample Video", list[0].Title);
        Assert.Equal("https://example.com/", list[0].RequestHeaders["Referer"]);
    }
}
