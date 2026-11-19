let currentStreams = [];
let currentSubs = [];

function cleanTitle(raw) {
  let s = (raw || "").split("|")[0].trim();
  s = s.replace(/(\d+)\.\s*sezon\s*(\d+)\.\s*b[oö]l[uü]m/gi, (_, a, b) => `S${a.padStart(2, "0")}E${b.padStart(2, "0")}`);
  s = s.replace(/\b(izle|türkçe|altyazılı|dublaj|full|hd|1080p|720p)\b/gi, " ");
  return s.replace(/\s+/g, " ").trim() || "Video";
}

function parseTitleSeasonEpisode(raw) {
  let base = (raw || "").split(/[|\-]/)[0];
  let season = null, episode = null;
  const m = base.match(/(?:S(\d{1,2})[\s:._-]*E(\d{1,2})|(\d+)\.\s*sezon\s*(\d+)\.\s*b[oö]l[uü]m|(\d{1,2})x(\d{1,2}))/i);
  if (m) {
    season = parseInt(m[1] || m[3] || m[5], 10);
    episode = parseInt(m[2] || m[4] || m[6], 10);
    base = base.slice(0, m.index);
  }
  base = base.replace(/\b(izle|türkçe|altyazılı|dublaj|1080p|720p|4k|full|hd)\b/gi, " ");
  return { title: base.replace(/\s+/g, " ").trim(), season, episode };
}

function formatQuality(url, proto) {
  const m = url.match(/(2160|1440|1080|720|480|360)/i);
  if (m) return `${m[1]}p`;
  return proto.toUpperCase();
}

async function resolveMasterVariants(stream, fallbackTitle) {
  if (stream.protocol !== "hls") return [{ ...stream, title: cleanTitle(stream.title || fallbackTitle) }];
  try {
    const res = await fetch(stream.url);
    const text = await res.text();
    const lines = text.split(/\r?\n/).map((l) => l.trim());
    if (!lines.some((l) => l.startsWith("#EXT-X-STREAM-INF:"))) {
      return [{ ...stream, title: cleanTitle(stream.title || fallbackTitle) }];
    }
    const variants = [];
    for (let i = 0; i < lines.length - 1; i++) {
      if (!lines[i].startsWith("#EXT-X-STREAM-INF:")) continue;
      const info = lines[i];
      const next = lines[i + 1];
      if (!next || next.startsWith("#")) continue;
      const resM = info.match(/RESOLUTION=(\d+x\d+)/);
      const bwM = info.match(/BANDWIDTH=(\d+)/);
      const absUrl = new URL(next, stream.url).toString();
      variants.push({
        url: absUrl,
        protocol: "hls",
        title: cleanTitle(stream.title || fallbackTitle),
        resolution: resM ? resM[1] : "HLS",
        bandwidth: bwM ? parseInt(bwM[1], 10) : 0,
        relevance: Math.min(100, (stream.relevance || 60) + (resM && /1080|2160/.test(resM[1]) ? 20 : 10))
      });
    }
    return variants.length ? variants.sort((a, b) => (b.bandwidth || 0) - (a.bandwidth || 0)) : [stream];
  } catch {
    return [{ ...stream, title: cleanTitle(stream.title || fallbackTitle) }];
  }
}

async function init() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab) return;
  const pageTitle = cleanTitle(tab.title || "");
  document.getElementById("subQuery").value = pageTitle;

  chrome.runtime.sendMessage({ type: "vf_get_state", tabId: tab.id }, async (state) => {
    const pill = document.getElementById("pill");
    const sel = document.getElementById("streamSelect");
    const otherSel = document.getElementById("otherSelect");
    const dlBtn = document.getElementById("dlBtn");

    if (state?.drm) {
      pill.textContent = `🔒 Korumalı (${state.drm})`;
      pill.style.color = "#F87171";
      return;
    }

    const rawStreams = state?.streams || [];
    const expanded = [];
    for (const s of rawStreams.slice(0, 6)) {
      const vars = await resolveMasterVariants(s, pageTitle);
      expanded.push(...vars);
    }

    const primary = expanded.filter((x) => x.relevance >= 40);
    const other = expanded.filter((x) => x.relevance < 40);
    currentStreams = primary;

    sel.innerHTML = "";
    primary.forEach((s, i) => {
      const q = s.resolution || formatQuality(s.url, s.protocol);
      const bw = s.bandwidth ? ` • ${(s.bandwidth / 1e6).toFixed(1)} Mbps` : "";
      const opt = document.createElement("option");
      opt.value = String(i);
      opt.textContent = `${q} — ${s.title}${bw} (Alaka %${s.relevance})`;
      sel.appendChild(opt);
    });

    otherSel.innerHTML = "";
    other.forEach((s) => {
      const opt = document.createElement("option");
      opt.textContent = `${s.resolution || formatQuality(s.url, s.protocol)} — ${s.url.slice(0, 48)}`;
      otherSel.appendChild(opt);
    });
    document.getElementById("otherSummary").textContent = `Diğer / Düşük Alakalı Akışlar (${other.length})`;

    currentSubs = state?.subtitles || [];
    renderSubtitles();

    if (primary.length > 0) {
      pill.textContent = `● ${primary.length} Kalite Hazır`;
      dlBtn.disabled = false;
    } else {
      pill.textContent = "Akış bekleniyor";
    }
  });
}

function renderSubtitles() {
  const subSel = document.getElementById("subSelect");
  subSel.innerHTML = '<option value="">— Altyazısız —</option>';
  currentSubs.forEach((s, i) => {
    const opt = document.createElement("option");
    opt.value = String(i);
    opt.textContent = `[${(s.lang || "tr").toUpperCase()} • ${s.source}] ${s.label}`;
    subSel.appendChild(opt);
  });
  if (currentSubs.length > 0) subSel.value = "0";
}

document.getElementById("subSearchBtn").addEventListener("click", async () => {
  const q = document.getElementById("subQuery").value.trim();
  const status = document.getElementById("status");
  if (!q) return;
  status.textContent = "Altyazı aranıyor...";
  try {
    const { title, season, episode } = parseTitleSeasonEpisode(q);
    const slug = title.toLowerCase().replace(/[^a-z0-9\s]/g, "").trim();
    const imdbRes = await fetch(`https://v3.sg.media-imdb.com/suggestion/${slug[0] || "x"}/${encodeURIComponent(slug)}.json`);
    const imdbJson = await imdbRes.json();
    const imdbId = imdbJson?.d?.find((x) => x.id?.startsWith("tt"))?.id;
    if (imdbId) {
      const api = (season && episode)
        ? `https://opensubtitles-v3.strem.io/subtitles/series/${imdbId}:${season}:${episode}.json`
        : `https://opensubtitles-v3.strem.io/subtitles/movie/${imdbId}.json`;
      const sRes = await fetch(api);
      const sJson = await sRes.json();
      const found = (sJson.subtitles || []).filter((x) => x.lang === "tur" || x.lang === "eng").slice(0, 10);
      found.forEach((f) => currentSubs.push({ url: f.url, label: `OpenSubtitles (${imdbId})`, lang: f.lang === "tur" ? "tr" : "en", source: "Harici" }));
      renderSubtitles();
      status.textContent = `${found.length} harici altyazı eklendi.`;
    } else {
      status.textContent = "IMDb eşleşmesi bulunamadı.";
    }
  } catch {
    status.textContent = "Altyazı araması tamamlanamadı.";
  }
});

document.getElementById("subDlBtn").addEventListener("click", () => {
  const idx = document.getElementById("subSelect").value;
  if (idx === "" || !currentSubs[Number(idx)]) return;
  chrome.downloads.download({ url: currentSubs[Number(idx)].url, filename: "altyazi.srt" });
});

document.getElementById("dlBtn").addEventListener("click", async () => {
  const idx = Number(document.getElementById("streamSelect").value || 0);
  const item = currentStreams[idx];
  const status = document.getElementById("status");
  if (!item) return;

  if (item.protocol === "mp4") {
    chrome.downloads.download({ url: item.url, filename: "video.mp4" });
    status.textContent = "Doğrudan MP4 indirmesi başlatıldı.";
    return;
  }

  if (item.protocol === "hls") {
    status.textContent = "HLS segmentleri indiriliyor...";
    try {
      const m3u8 = await (await fetch(item.url)).text();
      const lines = m3u8.split(/\r?\n/).map((l) => l.trim());
      const segUrls = [];
      for (const l of lines) {
        if (l.startsWith("#EXT-X-MAP:")) {
          const mm = l.match(/URI=["']?([^"',\s]+)["']?/i);
          if (mm) segUrls.push(new URL(mm[1], item.url).toString());
        } else if (l && !l.startsWith("#")) {
          segUrls.push(new URL(l, item.url).toString());
        }
      }
      const chunks = new Array(segUrls.length);
      let done = 0;
      const concurrency = 16;
      let cursor = 0;
      const workers = Array.from({ length: Math.min(concurrency, segUrls.length) }, async () => {
        while (cursor < segUrls.length) {
          const myIdx = cursor++;
          const buf = await (await fetch(segUrls[myIdx])).arrayBuffer();
          chunks[myIdx] = new Uint8Array(buf);
          done++;
          status.textContent = `HLS indiriliyor: ${done}/${segUrls.length}`;
        }
      });
      await Promise.all(workers);
      status.textContent = "MP4 formatına dönüştürülüyor...";
      const totalLen = chunks.reduce((a, b) => a + b.length, 0);
      const merged = new Uint8Array(totalLen);
      let off = 0;
      for (const c of chunks) { merged.set(c, off); off += c.length; }
      const mp4Bytes = typeof transmuxTsToMp4 === "function" ? transmuxTsToMp4(merged) : null;
      const safeName = (item.title || "video").replace(/[\\/:*?"<>|]/g, "_").trim() || "video";
      if (mp4Bytes) {
        const blob = new Blob([mp4Bytes], { type: "video/mp4" });
        chrome.downloads.download({ url: URL.createObjectURL(blob), filename: `${safeName}.mp4` });
        status.textContent = `Tamamlandı ✓ (${safeName}.mp4 kaydedildi)`;
      } else {
        const isFmp4 = merged.length > 8 && String.fromCharCode(merged[4], merged[5], merged[6], merged[7]) === "ftyp";
        const ext = isFmp4 ? "mp4" : "ts";
        const blob = new Blob([merged], { type: isFmp4 ? "video/mp4" : "video/mp2t" });
        chrome.downloads.download({ url: URL.createObjectURL(blob), filename: `${safeName}.${ext}` });
        status.textContent = isFmp4 ? `Tamamlandı ✓ (${safeName}.mp4 kaydedildi)` : `Tamamlandı ✓ (${safeName}.ts olarak kaydedildi)`;
      }
    } catch {
      chrome.downloads.download({ url: item.url, filename: "playlist.m3u8" });
      status.textContent = "CORS nedeniyle .m3u8 kaydedildi.";
    }
  } else {
    chrome.downloads.download({ url: item.url, filename: "manifest.mpd" });
    status.textContent = "DASH manifest kaydedildi.";
  }
});

init();
