using System.Text.RegularExpressions;
using VideoFetcher.Core.Interfaces;
using VideoFetcher.Core.Models;

namespace VideoFetcher.Core.Services;

public sealed partial class HlsManifestParser : IHlsManifestParser
{
    [GeneratedRegex(@"BANDWIDTH=(\d+)")]
    private static partial Regex BandwidthRegex();

    [GeneratedRegex(@"RESOLUTION=(\d+x\d+)")]
    private static partial Regex ResolutionRegex();

    [GeneratedRegex(@"CODECS=""([^""]+)""")]
    private static partial Regex CodecsRegex();

    [GeneratedRegex(@"METHOD=([A-Z0-9\-]+)")]
    private static partial Regex KeyMethodRegex();

    [GeneratedRegex(@"GROUP-ID=[""']?([^""',\s]+)[""']?")]
    private static partial Regex GroupIdRegex();

    [GeneratedRegex(@"URI=[""']?([^""',\s]+)[""']?")]
    private static partial Regex UriAttrRegex();

    [GeneratedRegex(@"AUDIO=[""']?([^""',\s]+)[""']?")]
    private static partial Regex AudioGroupAttrRegex();

    [GeneratedRegex(@"LANGUAGE=""([^""]+)""")]
    private static partial Regex LanguageAttrRegex();

    [GeneratedRegex(@"NAME=""([^""]+)""")]
    private static partial Regex NameAttrRegex();

    public HlsPlaylistResult Parse(
        string manifestContent,
        Uri baseUri,
        string pageTitle,
        IReadOnlyDictionary<string, string> headers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestContent);

        var lines = manifestContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool isMaster = lines.Any(l => l.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase));

        string? keyMethod = null;
        bool isEncrypted = false;
        foreach (var line in lines.Where(l => l.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase)))
        {
            var m = KeyMethodRegex().Match(line);
            if (m.Success && !m.Groups[1].Value.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            {
                isEncrypted = true;
                keyMethod = m.Groups[1].Value;
                break;
            }
        }

        if (isMaster)
        {
            var audioGroups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var audioLines = lines
                .Where(l => l.StartsWith("#EXT-X-MEDIA:", StringComparison.OrdinalIgnoreCase) && l.Contains("TYPE=AUDIO", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(l => l.Contains("DEFAULT=YES", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(l => l.Contains("AUTOSELECT=YES", StringComparison.OrdinalIgnoreCase));

            foreach (var line in audioLines)
            {
                var gMatch = GroupIdRegex().Match(line);
                var uMatch = UriAttrRegex().Match(line);
                if (gMatch.Success && uMatch.Success && !audioGroups.ContainsKey(gMatch.Groups[1].Value))
                {
                    audioGroups[gMatch.Groups[1].Value] = new Uri(baseUri, uMatch.Groups[1].Value).ToString();
                }
            }

            // HLS gömülü altyazı izlerini (#EXT-X-MEDIA:TYPE=SUBTITLES) topla
            var subTracks = new List<HlsSubtitleTrack>();
            foreach (var line in lines.Where(l => l.StartsWith("#EXT-X-MEDIA:", StringComparison.OrdinalIgnoreCase) && l.Contains("TYPE=SUBTITLES", StringComparison.OrdinalIgnoreCase)))
            {
                var uMatch = UriAttrRegex().Match(line);
                if (!uMatch.Success) continue;
                var lMatch = LanguageAttrRegex().Match(line);
                var nMatch = NameAttrRegex().Match(line);
                string lang = lMatch.Success ? lMatch.Groups[1].Value : "tr";
                string name = nMatch.Success ? nMatch.Groups[1].Value : $"HLS Altyazı ({lang})";
                subTracks.Add(new HlsSubtitleTrack(lang, name, new Uri(baseUri, uMatch.Groups[1].Value)));
            }

            var variants = new List<MediaStreamCandidate>();
            for (int i = 0; i < lines.Length - 1; i++)
            {
                if (!lines[i].StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase)) continue;
                var info = lines[i];
                var streamLine = lines[i + 1];
                if (streamLine.StartsWith('#')) continue;

                var bwMatch = BandwidthRegex().Match(info);
                var resMatch = ResolutionRegex().Match(info);
                var codecMatch = CodecsRegex().Match(info);
                var audioAttr = AudioGroupAttrRegex().Match(info);

                string? sepAudio = null;
                if (audioAttr.Success && audioGroups.TryGetValue(audioAttr.Groups[1].Value, out var aUrl))
                    sepAudio = aUrl;
                else if (audioGroups.Count > 0)
                    sepAudio = audioGroups.Values.First();

                var (vCodec, aCodec) = SplitCodecs(codecMatch.Success ? codecMatch.Groups[1].Value : "avc1,mp4a");
                var resolvedUrl = new Uri(baseUri, streamLine).ToString();

                variants.Add(new MediaStreamCandidate(
                    Id: Guid.NewGuid().ToString("N")[..8],
                    Url: resolvedUrl,
                    Title: pageTitle,
                    Protocol: StreamProtocol.Hls,
                    VideoCodec: vCodec,
                    AudioCodec: aCodec,
                    Resolution: resMatch.Success ? resMatch.Groups[1].Value : "Auto",
                    BandwidthBps: bwMatch.Success && long.TryParse(bwMatch.Groups[1].Value, out var bw) ? bw : null,
                    IsDrmProtected: isEncrypted,
                    DrmSystem: keyMethod,
                    RequestHeaders: headers,
                    SeparateAudioUrl: sepAudio));
            }

            return new HlsPlaylistResult(true, isEncrypted, keyMethod, variants.OrderByDescending(v => v.BandwidthBps ?? 0).ToList(), [], subTracks);
        }

        var segments = new List<HlsSegment>();
        double nextDuration = 0;
        int index = 0;

        foreach (var line in lines)
        {
            if (line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
            {
                var mapUri = UriAttrRegex().Match(line);
                if (mapUri.Success)
                    segments.Add(new HlsSegment(index++, new Uri(baseUri, mapUri.Groups[1].Value), 0));
                continue;
            }
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var raw = line["#EXTINF:".Length..].Split(',')[0];
                double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out nextDuration);
                continue;
            }
            if (line.StartsWith('#')) continue;
            segments.Add(new HlsSegment(index++, new Uri(baseUri, line), nextDuration));
            nextDuration = 0;
        }

        return new HlsPlaylistResult(false, isEncrypted, keyMethod, [], segments);
    }

    private static (string Video, string Audio) SplitCodecs(string raw)
    {
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string v = parts.FirstOrDefault(p => p.StartsWith("avc") || p.StartsWith("hev") || p.StartsWith("hvc") || p.StartsWith("vp09") || p.StartsWith("av01")) ?? "h264";
        string a = parts.FirstOrDefault(p => p.StartsWith("mp4a") || p.StartsWith("opus") || p.StartsWith("ac-3")) ?? "aac";
        return (v, a);
    }
}
