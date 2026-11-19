using System.Xml.Linq;
using VideoFetcher.Core.Interfaces;
using VideoFetcher.Core.Models;

namespace VideoFetcher.Core.Services;

public sealed class DashManifestParser : IDashManifestParser
{
    public DashAdaptationResult Parse(
        string mpdXml,
        Uri baseUri,
        string pageTitle,
        IReadOnlyDictionary<string, string> headers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mpdXml);

        var doc = XDocument.Parse(mpdXml);
        var drmElements = doc.Descendants().Where(e => e.Name.LocalName == "ContentProtection").ToList();
        bool isDrm = drmElements.Count > 0;
        string? drmScheme = drmElements.FirstOrDefault()?.Attribute("schemeIdUri")?.Value;

        var videos = new List<MediaStreamCandidate>();
        var audios = new List<MediaStreamCandidate>();

        foreach (var adapt in doc.Descendants().Where(e => e.Name.LocalName == "AdaptationSet"))
        {
            string mime = adapt.Attribute("mimeType")?.Value ?? "";
            string contentType = adapt.Attribute("contentType")?.Value ?? "";
            bool isVideo = mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || contentType == "video";
            bool isAudio = mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) || contentType == "audio";

            foreach (var rep in adapt.Elements().Where(e => e.Name.LocalName == "Representation"))
            {
                string codecs = rep.Attribute("codecs")?.Value ?? adapt.Attribute("codecs")?.Value ?? (isVideo ? "avc1" : "mp4a");
                string width = rep.Attribute("width")?.Value ?? "";
                string height = rep.Attribute("height")?.Value ?? "";
                string res = (!string.IsNullOrEmpty(width) && !string.IsNullOrEmpty(height)) ? $"{width}x{height}" : "Auto";
                long? bw = long.TryParse(rep.Attribute("bandwidth")?.Value, out var b) ? b : null;

                string? baseNode = rep.Elements().FirstOrDefault(e => e.Name.LocalName == "BaseURL")?.Value
                    ?? adapt.Elements().FirstOrDefault(e => e.Name.LocalName == "BaseURL")?.Value;
                string resolvedUrl = !string.IsNullOrEmpty(baseNode) ? new Uri(baseUri, baseNode).ToString() : baseUri.ToString();

                var candidate = new MediaStreamCandidate(
                    Id: Guid.NewGuid().ToString("N")[..8],
                    Url: resolvedUrl,
                    Title: pageTitle,
                    Protocol: StreamProtocol.Dash,
                    VideoCodec: isVideo ? codecs : "none",
                    AudioCodec: isAudio ? codecs : "none",
                    Resolution: res,
                    BandwidthBps: bw,
                    IsDrmProtected: isDrm,
                    DrmSystem: drmScheme,
                    RequestHeaders: headers);

                if (isVideo) videos.Add(candidate);
                else if (isAudio) audios.Add(candidate);
            }
        }

        return new DashAdaptationResult(
            isDrm,
            drmScheme,
            videos.OrderByDescending(v => v.BandwidthBps ?? 0).ToList(),
            audios.OrderByDescending(a => a.BandwidthBps ?? 0).ToList());
    }
}
