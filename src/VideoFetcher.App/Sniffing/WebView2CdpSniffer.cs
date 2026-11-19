using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using VideoFetcher.Core.Interfaces;
using VideoFetcher.Core.Models;

namespace VideoFetcher.App.Sniffing;

public sealed class WebView2CdpSniffer
{
    private static readonly HashSet<string> AllowedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Referer", "Origin", "User-Agent", "Cookie", "Authorization", "Accept", "Accept-Language"
    };

    private static readonly string[] IgnoredStaticExtensions =
    [
        ".js", ".css", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".ico",
        ".woff", ".woff2", ".ttf", ".eot", ".html", ".htm"
    ];

    private readonly ILogger<WebView2CdpSniffer> _logger;
    public event EventHandler<MediaStreamCandidate>? MediaCandidateDetected;
    public event EventHandler<SubtitleCandidate>? SubtitleDetected;
    public event EventHandler<string>? DrmDetected;

    public WebView2CdpSniffer(ILogger<WebView2CdpSniffer> logger) => _logger = logger;

    public async Task AttachAsync(CoreWebView2 webView, CancellationToken ct)
    {
        // Tüm frame ve iframe'lerde EME DRM + standart dışı uzantı/MIME fetch/XHR/video + dahili altyazı (<track> / WEBVTT) yakala
        await webView.AddScriptToExecuteOnDocumentCreatedAsync(@"
            (() => {
                if (window.__vfHooked) return;
                window.__vfHooked = true;

                const post = (obj) => {
                    try { window.chrome.webview.postMessage(JSON.stringify(obj)); } catch {}
                };

                const origDrm = navigator.requestMediaKeySystemAccess;
                if (origDrm) {
                    navigator.requestMediaKeySystemAccess = function(keySystem, configs) {
                        post({ type: 'drm_detected', keySystem });
                        return origDrm.apply(this, arguments);
                    };
                }

                const inspectText = (url, text) => {
                    if (!url || !text || typeof text !== 'string') return;
                    const trimmed = text.trimStart();
                    if (trimmed.startsWith('#EXTM3U')) {
                        post({ type: 'media_manifest', protocol: 'hls', url, content: text });
                    } else if (trimmed.includes('<MPD')) {
                        post({ type: 'media_manifest', protocol: 'dash', url, content: text });
                    } else if (trimmed.startsWith('WEBVTT') || /^1\r?\n\d{2}:\d{2}:\d{2},\d{3}\s+-->/.test(trimmed)) {
                        post({ type: 'subtitle_detected', url, content: text, label: '' });
                    }
                };

                const origFetch = window.fetch;
                if (origFetch) {
                    window.fetch = async function(...args) {
                        const res = await origFetch.apply(this, args);
                        try {
                            const reqUrl = (typeof args[0] === 'string' ? args[0] : args[0]?.url) || res.url;
                            const cl = parseInt(res.headers?.get('content-length') || '0', 10);
                            if (cl === 0 || cl < 2097152) {
                                const clone = res.clone();
                                clone.text().then(t => inspectText(res.url || reqUrl, t)).catch(() => {});
                            }
                        } catch {}
                        return res;
                    };
                }

                const origOpen = XMLHttpRequest.prototype.open;
                const origSend = XMLHttpRequest.prototype.send;
                XMLHttpRequest.prototype.open = function(method, url) {
                    this.__vfUrl = url;
                    return origOpen.apply(this, arguments);
                };
                XMLHttpRequest.prototype.send = function() {
                    this.addEventListener('load', function() {
                        try {
                            if (this.responseType === '' || this.responseType === 'text') {
                                inspectText(this.responseURL || this.__vfUrl, this.responseText);
                            }
                        } catch {}
                    });
                    return origSend.apply(this, arguments);
                };

                const scanMediaDom = () => {
                    try {
                        document.querySelectorAll('video, video source').forEach(el => {
                            const s = el.currentSrc || el.src;
                            if (s && s.startsWith('http')) {
                                post({ type: 'video_element_src', url: s });
                            }
                        });
                        document.querySelectorAll('track').forEach(tr => {
                            const s = tr.src;
                            if (s && s.startsWith('http')) {
                                post({ type: 'subtitle_detected', url: s, content: '', label: tr.label || tr.srclang || 'Dahili Altyazı', lang: tr.srclang || 'tr' });
                            }
                        });
                    } catch {}
                };
                document.addEventListener('DOMContentLoaded', () => {
                    scanMediaDom();
                    new MutationObserver(scanMediaDom).observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['src'] });
                });
            })();
        ");

        webView.WebMessageReceived += async (_, args) =>
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                using var doc = JsonDocument.Parse(args.WebMessageAsJson);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t)) return;
                string? type = t.GetString();

                if (type == "drm_detected")
                {
                    DrmDetected?.Invoke(this, root.GetProperty("keySystem").GetString() ?? "EME");
                }
                else if (type == "media_manifest")
                {
                    string? url = root.GetProperty("url").GetString();
                    string? content = root.GetProperty("content").GetString();
                    string? proto = root.GetProperty("protocol").GetString();
                    if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(content)) return;

                    var headers = await BuildSafeHeadersAsync(webView, url, null);
                    string mime = proto == "dash" ? "application/dash+xml" : "application/vnd.apple.mpegurl";
                    TryEmitCandidate(url, mime, webView.DocumentTitle, headers, content);
                }
                else if (type == "video_element_src")
                {
                    string? url = root.GetProperty("url").GetString();
                    if (string.IsNullOrWhiteSpace(url) || url.StartsWith("blob:") || url.StartsWith("data:")) return;

                    var headers = await BuildSafeHeadersAsync(webView, url, null);
                    TryEmitCandidate(url, "video/mp4", webView.DocumentTitle, headers, null);
                }
                else if (type == "subtitle_detected")
                {
                    string? url = root.TryGetProperty("url", out var up) ? up.GetString() : null;
                    string? content = root.TryGetProperty("content", out var cp) ? cp.GetString() : null;
                    string? label = root.TryGetProperty("label", out var lp) ? lp.GetString() : null;
                    string? lang = root.TryGetProperty("lang", out var lg) ? lg.GetString() : null;
                    if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(content)) return;

                    EmitSubtitle(url ?? "", content, label, lang);
                }
            }
            catch { }
        };

        webView.WebResourceResponseReceived += async (_, args) =>
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                string url = args.Request.Uri;
                if (string.IsNullOrWhiteSpace(url) || url.StartsWith("data:") || url.StartsWith("blob:")) return;

                string mime = args.Response.Headers.Contains("Content-Type")
                    ? args.Response.Headers.GetHeader("Content-Type")
                    : "";

                if (IsSubtitleCandidate(url, mime))
                {
                    string? subContent = null;
                    try
                    {
                        using var sStream = await args.Response.GetContentAsync();
                        if (sStream is not null)
                        {
                            using var net = sStream.AsStreamForRead();
                            using var sr = new StreamReader(net, Encoding.UTF8);
                            subContent = await sr.ReadToEndAsync(ct);
                        }
                    }
                    catch { }
                    EmitSubtitle(url, subContent, null, null);
                    return;
                }

                bool isKnownMedia = IsMediaCandidate(url, mime);
                bool isManifestUrlOrMime = mime.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
                                        || url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                                        || mime.Contains("dash+xml", StringComparison.OrdinalIgnoreCase)
                                        || url.Contains(".mpd", StringComparison.OrdinalIgnoreCase);
                string? prefetchedText = null;
                string? detectedMagicMime = null;

                if (isManifestUrlOrMime || (!isKnownMedia && IsInspectableResponse(mime, url, args.Response.Headers)))
                {
                    try
                    {
                        using var stream = await args.Response.GetContentAsync();
                        if (stream is not null)
                        {
                            using var netStream = stream.AsStreamForRead();
                            byte[] headBytes = new byte[1024];
                            int read = await netStream.ReadAsync(headBytes.AsMemory(0, headBytes.Length), ct);
                            if (read >= 6)
                            {
                                string sample = Encoding.UTF8.GetString(headBytes, 0, read).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
                                if (sample.StartsWith("#EXTM3U", StringComparison.Ordinal) || sample.Contains("<MPD", StringComparison.OrdinalIgnoreCase))
                                {
                                    using var ms = new MemoryStream();
                                    ms.Write(headBytes, 0, read);
                                    await netStream.CopyToAsync(ms, ct);
                                    prefetchedText = Encoding.UTF8.GetString(ms.ToArray());
                                    isKnownMedia = true;
                                }
                                else if (sample.StartsWith("WEBVTT", StringComparison.Ordinal))
                                {
                                    using var ms = new MemoryStream();
                                    ms.Write(headBytes, 0, read);
                                    await netStream.CopyToAsync(ms, ct);
                                    EmitSubtitle(url, Encoding.UTF8.GetString(ms.ToArray()), null, null);
                                    return;
                                }
                                else if (read >= 12 && headBytes[4] == (byte)'f' && headBytes[5] == (byte)'t' && headBytes[6] == (byte)'y' && headBytes[7] == (byte)'p')
                                {
                                    detectedMagicMime = "video/mp4";
                                    isKnownMedia = true;
                                }
                                else if (headBytes[0] == 0x1A && headBytes[1] == 0x45 && headBytes[2] == 0xDF && headBytes[3] == 0xA3)
                                {
                                    detectedMagicMime = "video/webm";
                                    isKnownMedia = true;
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (!isKnownMedia) return;

                var safeHeaders = await BuildSafeHeadersAsync(webView, url, args.Request.Headers);
                TryEmitCandidate(url, detectedMagicMime ?? mime, webView.DocumentTitle, safeHeaders, prefetchedText);
            }
            catch { }
        };
    }

    private void EmitSubtitle(string url, string? content, string? label, string? lang)
    {
        string detectedLang = !string.IsNullOrWhiteSpace(lang) ? lang : GuessLangFromUrl(url);
        string name = !string.IsNullOrWhiteSpace(label) ? label : Path.GetFileName(url.Split('?')[0]);
        if (string.IsNullOrWhiteSpace(name)) name = "Site İçi Altyazı";

        SubtitleDetected?.Invoke(this, new SubtitleCandidate(
            Id: Guid.NewGuid().ToString("N")[..8],
            Language: detectedLang,
            ReleaseName: name,
            DownloadCount: 999999,
            SourceKind: "Siteİçi",
            DownloadUrlOrFileId: url,
            InlineContent: string.IsNullOrWhiteSpace(content) ? null : content));
    }

    private static string GuessLangFromUrl(string url)
    {
        if (url.Contains("tur", StringComparison.OrdinalIgnoreCase) || url.Contains("/tr", StringComparison.OrdinalIgnoreCase) || url.Contains(".tr.", StringComparison.OrdinalIgnoreCase)) return "tr";
        if (url.Contains("eng", StringComparison.OrdinalIgnoreCase) || url.Contains("/en", StringComparison.OrdinalIgnoreCase) || url.Contains(".en.", StringComparison.OrdinalIgnoreCase)) return "en";
        return "tr";
    }

    private static bool IsSubtitleCandidate(string url, string mime)
    {
        string p = url.Split('?')[0];
        return p.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith(".ass", StringComparison.OrdinalIgnoreCase)
            || mime.Contains("text/vtt", StringComparison.OrdinalIgnoreCase)
            || mime.Contains("subrip", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<Dictionary<string, string>> BuildSafeHeadersAsync(
        CoreWebView2 webView,
        string url,
        CoreWebView2HttpRequestHeaders? reqHeaders)
    {
        var safeHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (reqHeaders is not null)
        {
            foreach (var h in reqHeaders)
            {
                if (AllowedHeaders.Contains(h.Key)) safeHeaders[h.Key] = h.Value;
            }
        }
        if (!safeHeaders.ContainsKey("User-Agent"))
            safeHeaders["User-Agent"] = webView.Settings.UserAgent;

        try
        {
            var cookies = await webView.CookieManager.GetCookiesAsync(url);
            if (cookies.Count > 0)
                safeHeaders["Cookie"] = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
        }
        catch { }
        return safeHeaders;
    }

    private static bool IsInspectableResponse(string mime, string url, CoreWebView2HttpResponseHeaders headers)
    {
        string pathOnly = url.Split('?')[0];
        if (IgnoredStaticExtensions.Any(ext => pathOnly.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (headers.Contains("Content-Length") && long.TryParse(headers.GetHeader("Content-Length"), out long len) && len > 2 * 1024 * 1024)
            return false;

        return string.IsNullOrWhiteSpace(mime)
            || mime.Contains("text/", StringComparison.OrdinalIgnoreCase)
            || mime.Contains("json", StringComparison.OrdinalIgnoreCase)
            || mime.Contains("octet-stream", StringComparison.OrdinalIgnoreCase)
            || mime.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
            || mime.Contains("xml", StringComparison.OrdinalIgnoreCase)
            || mime.Contains("application/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMediaCandidate(string url, string mime)
    {
        bool isHls = mime.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) || url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
        bool isDash = mime.Contains("dash+xml", StringComparison.OrdinalIgnoreCase) || url.Contains(".mpd", StringComparison.OrdinalIgnoreCase);
        bool isDirect = (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || url.Contains(".mp4", StringComparison.OrdinalIgnoreCase) || url.Contains(".webm", StringComparison.OrdinalIgnoreCase))
                        && !url.Contains(".ts", StringComparison.OrdinalIgnoreCase) && !mime.Contains("mp2t", StringComparison.OrdinalIgnoreCase);
        return isHls || isDash || isDirect;
    }

    private void TryEmitCandidate(string url, string mime, string pageTitle, IReadOnlyDictionary<string, string> headers, string? prefetched)
    {
        bool isHls = (prefetched?.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith("#EXTM3U", StringComparison.Ordinal) ?? false)
                  || mime.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
                  || url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
        bool isDash = (prefetched?.Contains("<MPD", StringComparison.OrdinalIgnoreCase) ?? false)
                   || mime.Contains("dash+xml", StringComparison.OrdinalIgnoreCase)
                   || url.Contains(".mpd", StringComparison.OrdinalIgnoreCase);
        var protocol = isHls ? StreamProtocol.Hls : isDash ? StreamProtocol.Dash : StreamProtocol.DirectFile;

        MediaCandidateDetected?.Invoke(this, new MediaStreamCandidate(
            Id: Guid.NewGuid().ToString("N")[..8],
            Url: url,
            Title: string.IsNullOrWhiteSpace(pageTitle) ? "Video" : pageTitle,
            Protocol: protocol,
            VideoCodec: "h264",
            AudioCodec: "aac",
            Resolution: isHls ? "HLS" : isDash ? "DASH" : "MP4",
            BandwidthBps: null,
            IsDrmProtected: false,
            DrmSystem: null,
            RequestHeaders: headers,
            PrefetchedManifestContent: prefetched));
    }
}
