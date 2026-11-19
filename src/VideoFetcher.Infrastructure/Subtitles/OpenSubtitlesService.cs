using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using VideoFetcher.Core.Interfaces;

namespace VideoFetcher.Infrastructure.Subtitles;

public sealed partial class OpenSubtitlesService : ISubtitleService
{
    private const string XmlRpcUrl = "https://api.opensubtitles.org/xml-rpc";
    private const string VlSubUserAgent = "VLSub 0.11.1";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<OpenSubtitlesService> _logger;

    [GeneratedRegex(@"(?i)(?:S(\d{1,2})[\s:._-]*E(\d{1,2})|(\d+)\.\s*sezon\s*(\d+)\.\s*b[oö]l[uü]m|(\d{1,2})x(\d{1,2}))")]
    private static partial Regex SeasonEpisodeRegex();

    [GeneratedRegex(@"(?i)\b(izle|türkçe|altyazılı|dublaj|1080p|720p|4k|full|hd|cinejoy|dizibox|dizilla|hdfilmcehennemi)\b")]
    private static partial Regex NoiseWordsRegex();

    public OpenSubtitlesService(IHttpClientFactory httpFactory, ILogger<OpenSubtitlesService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(string rawPageTitle, string languageCode, CancellationToken ct)
    {
        var (cleanTitle, season, episode) = ParseTitleSeasonEpisode(rawPageTitle);
        if (string.IsNullOrWhiteSpace(cleanTitle)) return [];

        string subLang3 = ToIso639_2(languageCode);
        var client = _httpFactory.CreateClient("HighSpeedMediaClient");

        // IMDb ID çözümle (tt33043892 gibi) — hem OpenSubtitles hem Stremio/TurkceAltyazi doğruluğunu artırır
        string? imdbId = await ResolveImdbIdAsync(client, cleanTitle, ct);

        // Tüm harici altyazı veritabanlarını paralel sorgula
        var vlSubTask = SearchOpenSubtitlesXmlRpcAsync(client, cleanTitle, imdbId, season, episode, subLang3, languageCode, ct);
        var stremioTask = SearchOpenSubtitlesStremioAsync(client, imdbId, season, episode, subLang3, languageCode, ct);
        var podnapisiTask = SearchPodnapisiAsync(client, cleanTitle, season, episode, languageCode, ct);

        await Task.WhenAll(vlSubTask, stremioTask, podnapisiTask);

        var combined = new List<SubtitleCandidate>();
        combined.AddRange(await vlSubTask);
        combined.AddRange(await stremioTask);
        combined.AddRange(await podnapisiTask);

        return combined
            .Where(x => !string.IsNullOrWhiteSpace(x.DownloadUrlOrFileId))
            .GroupBy(x => x.DownloadUrlOrFileId!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(x => x.DownloadCount)
            .Take(25)
            .ToList();
    }

    private static string ToIso639_2(string languageCode) => languageCode.ToLowerInvariant() switch
    {
        "tr" => "tur", "en" => "eng", "de" => "ger", "fr" => "fre", "es" => "spa", "ar" => "ara", "ru" => "rus", _ => "eng"
    };

    private async Task<string?> ResolveImdbIdAsync(HttpClient client, string title, CancellationToken ct)
    {
        try
        {
            string slug = Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9\s]", "").Trim();
            if (string.IsNullOrWhiteSpace(slug)) return null;
            char first = char.IsLetterOrDigit(slug[0]) ? slug[0] : 'x';
            string url = $"https://v3.sg.media-imdb.com/suggestion/{first}/{Uri.EscapeDataString(slug)}.json";
            using var resp = await client.GetAsync(url, ct);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("d", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        string? id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(id) && id.StartsWith("tt", StringComparison.Ordinal))
                            return id;
                    }
                }
            }
        }
        catch { }

        try
        {
            using var resp = await client.GetAsync($"https://api.tvmaze.com/singlesearch/shows?q={Uri.EscapeDataString(title)}", ct);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("externals", out var ext) && ext.TryGetProperty("imdb", out var imdb))
                    return imdb.GetString();
            }
        }
        catch { }
        return null;
    }

    private async Task<IReadOnlyList<SubtitleCandidate>> SearchOpenSubtitlesXmlRpcAsync(
        HttpClient client, string cleanTitle, string? imdbId, int? season, int? episode, string subLang, string languageCode, CancellationToken ct)
    {
        try
        {
            string? token = await LoginAnonymousAsync(client, ct);
            if (string.IsNullOrEmpty(token)) return [];

            // Önce IMDb ID varsa onunla, yoksa başlık ile ara
            if (!string.IsNullOrWhiteSpace(imdbId))
            {
                string imdbNum = imdbId.TrimStart('t', 'T');
                var byImdb = await QueryXmlRpcAsync(client, token, null, imdbNum, season, episode, subLang, languageCode, ct);
                if (byImdb.Count > 0) return byImdb;
            }

            var byQuery = await QueryXmlRpcAsync(client, token, cleanTitle, null, season, episode, subLang, languageCode, ct);
            if (byQuery.Count > 0) return byQuery;

            // Sezon/Bölüm filtresi boş dönerse SxxExx birleşik sorgu dene
            if (season.HasValue && episode.HasValue)
                return await QueryXmlRpcAsync(client, token, $"{cleanTitle} S{season.Value:D2}E{episode.Value:D2}", null, null, null, subLang, languageCode, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VLSub XML-RPC altyazı arama hatası");
        }
        return [];
    }

    private static async Task<IReadOnlyList<SubtitleCandidate>> QueryXmlRpcAsync(
        HttpClient client, string token, string? query, string? imdbNumeric, int? season, int? episode, string subLang, string languageCode, CancellationToken ct)
    {
        string queryXml = !string.IsNullOrWhiteSpace(query)
            ? $"<member><name>query</name><value><string>{SecurityElement.Escape(query)}</string></value></member>"
            : $"<member><name>imdbid</name><value><string>{SecurityElement.Escape(imdbNumeric)}</string></value></member>";
        string seasonXml = season.HasValue ? $"<member><name>season</name><value><string>{season.Value}</string></value></member>" : "";
        string episodeXml = episode.HasValue ? $"<member><name>episode</name><value><string>{episode.Value}</string></value></member>" : "";

        string searchXml = $"""
            <?xml version="1.0"?>
            <methodCall>
              <methodName>SearchSubtitles</methodName>
              <params>
                <param><value><string>{SecurityElement.Escape(token)}</string></value></param>
                <param><value><array><data><value><struct>
                  <member><name>sublanguageid</name><value><string>{subLang}</string></value></member>
                  {queryXml}
                  {seasonXml}
                  {episodeXml}
                </struct></value></data></array></value></param>
              </params>
            </methodCall>
            """;

        using var req = new HttpRequestMessage(HttpMethod.Post, XmlRpcUrl) { Content = new StringContent(searchXml, Encoding.UTF8, "text/xml") };
        req.Headers.TryAddWithoutValidation("User-Agent", VlSubUserAgent);
        using var resp = await client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return [];

        var doc = XDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var list = new List<SubtitleCandidate>();
        foreach (var structEl in doc.Descendants("struct"))
        {
            var fields = structEl.Elements("member")
                .Where(m => m.Element("name") is not null)
                .ToDictionary(m => m.Element("name")!.Value, m => m.Element("value")?.Value ?? "", StringComparer.OrdinalIgnoreCase);

            if (!fields.TryGetValue("SubDownloadLink", out var dlLink) || string.IsNullOrWhiteSpace(dlLink)) continue;
            string fmt = fields.GetValueOrDefault("SubFormat", "srt");
            if (!fmt.Equals("srt", StringComparison.OrdinalIgnoreCase) && !fmt.Equals("vtt", StringComparison.OrdinalIgnoreCase) && !fmt.Equals("ass", StringComparison.OrdinalIgnoreCase)) continue;

            string id = fields.GetValueOrDefault("IDSubtitleFile", Guid.NewGuid().ToString("N"));
            string fileName = fields.GetValueOrDefault("SubFileName", query ?? "subtitle");
            int.TryParse(fields.GetValueOrDefault("SubDownloadsCnt", "0"), out int dlCount);
            list.Add(new SubtitleCandidate(id, languageCode, fileName, dlCount, "OpenSubtitles", dlLink));
        }
        return list;
    }

    private static async Task<IReadOnlyList<SubtitleCandidate>> SearchOpenSubtitlesStremioAsync(
        HttpClient client, string? imdbId, int? season, int? episode, string subLang3, string languageCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(imdbId)) return [];
        try
        {
            string url = (season.HasValue && episode.HasValue)
                ? $"https://opensubtitles-v3.strem.io/subtitles/series/{imdbId}:{season.Value}:{episode.Value}.json"
                : $"https://opensubtitles-v3.strem.io/subtitles/movie/{imdbId}.json";

            using var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return [];

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("subtitles", out var subs) || subs.ValueKind != JsonValueKind.Array) return [];

            var list = new List<SubtitleCandidate>();
            int rank = 500;
            foreach (var s in subs.EnumerateArray())
            {
                string lang = s.TryGetProperty("lang", out var lp) ? lp.GetString() ?? "" : "";
                if (!lang.Equals(subLang3, StringComparison.OrdinalIgnoreCase) && !lang.Equals(languageCode, StringComparison.OrdinalIgnoreCase)) continue;
                string? dlUrl = s.TryGetProperty("url", out var up) ? up.GetString() : null;
                string? id = s.TryGetProperty("id", out var ip) ? ip.GetString() : Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(dlUrl)) continue;
                list.Add(new SubtitleCandidate(id!, languageCode, $"OpenSubtitles-v3 ({imdbId})", rank--, "OpenSubtitles-v3", dlUrl));
                if (list.Count >= 10) break;
            }
            return list;
        }
        catch { return []; }
    }

    private static async Task<IReadOnlyList<SubtitleCandidate>> SearchPodnapisiAsync(
        HttpClient client, string cleanTitle, int? season, int? episode, string languageCode, CancellationToken ct)
    {
        try
        {
            string url = $"https://www.podnapisi.net/subtitles/search/old?sXML=1&sK={Uri.EscapeDataString(cleanTitle)}&sL={Uri.EscapeDataString(languageCode)}";
            if (season.HasValue) url += $"&sTS={season.Value}";
            if (episode.HasValue) url += $"&sTE={episode.Value}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "VideoFetcher/1.1");
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return [];

            var doc = XDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var list = new List<SubtitleCandidate>();
            foreach (var sub in doc.Descendants("subtitle").Take(10))
            {
                string pid = sub.Element("pid")?.Value ?? "";
                string release = sub.Element("release")?.Value ?? sub.Element("title")?.Value ?? cleanTitle;
                int.TryParse(sub.Element("downloads")?.Value ?? "0", out int dlCount);
                if (string.IsNullOrWhiteSpace(pid)) continue;
                string dlUrl = $"https://www.podnapisi.net/subtitles/{pid}/download";
                list.Add(new SubtitleCandidate(pid, languageCode, release, dlCount, "Podnapisi", dlUrl));
            }
            return list;
        }
        catch { return []; }
    }

    private static async Task<string?> LoginAnonymousAsync(HttpClient client, CancellationToken ct)
    {
        const string loginXml = """
            <?xml version="1.0"?>
            <methodCall>
              <methodName>LogIn</methodName>
              <params>
                <param><value><string></string></value></param>
                <param><value><string></string></value></param>
                <param><value><string>en</string></value></param>
                <param><value><string>VLSub 0.11.1</string></value></param>
              </params>
            </methodCall>
            """;

        using var req = new HttpRequestMessage(HttpMethod.Post, XmlRpcUrl) { Content = new StringContent(loginXml, Encoding.UTF8, "text/xml") };
        req.Headers.TryAddWithoutValidation("User-Agent", VlSubUserAgent);
        using var resp = await client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        var doc = XDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var tokenMember = doc.Descendants("member").FirstOrDefault(m => m.Element("name")?.Value == "token");
        return tokenMember?.Element("value")?.Value;
    }

    public async Task<string> DownloadToSrtAsync(SubtitleCandidate candidate, string destinationPath, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(candidate.InlineContent))
        {
            await File.WriteAllTextAsync(destinationPath, candidate.InlineContent, Encoding.UTF8, ct);
            return destinationPath;
        }

        var client = _httpFactory.CreateClient("HighSpeedMediaClient");
        if (string.IsNullOrWhiteSpace(candidate.DownloadUrlOrFileId))
            throw new InvalidOperationException("Altyazı indirme bağlantısı boş.");

        using var req = new HttpRequestMessage(HttpMethod.Get, candidate.DownloadUrlOrFileId);
        req.Headers.TryAddWithoutValidation("User-Agent", VlSubUserAgent);
        using var resp = await client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        if (candidate.DownloadUrlOrFileId.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) || (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B))
        {
            await using var ms = new MemoryStream(bytes);
            await using var gz = new GZipStream(ms, CompressionMode.Decompress);
            await using var outFs = File.Create(destinationPath);
            await gz.CopyToAsync(outFs, ct);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0x50 && bytes[1] == 0x4B) // ZIP arşivi (Podnapisi vb.)
        {
            using var ms = new MemoryStream(bytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".ass", StringComparison.OrdinalIgnoreCase))
                        ?? zip.Entries.FirstOrDefault();
            if (entry is null) throw new InvalidOperationException("Altyazı ZIP arşivi boş.");
            await using var es = entry.Open();
            await using var outFs = File.Create(destinationPath);
            await es.CopyToAsync(outFs, ct);
        }
        else
        {
            await File.WriteAllBytesAsync(destinationPath, bytes, ct);
        }
        return destinationPath;
    }

    private static (string Title, int? Season, int? Episode) ParseTitleSeasonEpisode(string raw)
    {
        string basePart = raw.Split('|', '-')[0];
        int? season = null, episode = null;
        var m = SeasonEpisodeRegex().Match(basePart);
        if (m.Success)
        {
            string sStr = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[5].Value;
            string eStr = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[4].Success ? m.Groups[4].Value : m.Groups[6].Value;
            if (int.TryParse(sStr, out var s)) season = s;
            if (int.TryParse(eStr, out var e)) episode = e;
            basePart = basePart[..m.Index];
        }
        basePart = NoiseWordsRegex().Replace(basePart, " ");
        string clean = Regex.Replace(basePart, @"\s+", " ").Trim();
        return (clean, season, episode);
    }
}
