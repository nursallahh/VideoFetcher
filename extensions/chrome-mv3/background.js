const tabState = new Map();

function ensureTab(tabId) {
  if (!tabState.has(tabId)) {
    tabState.set(tabId, { url: "", title: "", streams: [], subtitles: [], drm: null });
  }
  return tabState.get(tabId);
}

function cleanPageTitle(raw) {
  if (!raw) return "Video";
  let s = raw.split("|")[0].trim();
  s = s.replace(/(\d+)\.\s*sezon\s*(\d+)\.\s*b[oö]l[uü]m/gi, (_, a, b) => `S${a.padStart(2, "0")}E${b.padStart(2, "0")}`);
  s = s.replace(/\b(izle|türkçe|altyazılı|dublaj|full|hd|1080p|720p)\b/gi, " ");
  return s.replace(/\s+/g, " ").trim() || "Video";
}

function calcRelevance(url, protocol, sizeBytes = 0, fromPlayer = false) {
  let score = 50;
  if (protocol === "hls" || protocol === "dash") score += 25;
  if (fromPlayer) score += 25;
  if (sizeBytes >= 10 * 1024 * 1024) score += 30;
  else if (sizeBytes >= 2 * 1024 * 1024) score += 15;
  if (/2160|4k/i.test(url)) score += 20;
  else if (/1080/i.test(url)) score += 15;
  else if (/720/i.test(url)) score += 10;
  const adPatterns = ["/ads/", "/ad/", "doubleclick", "googlesyndication", "preroll", "vast", "trailer", "teaser", "/promo/", "preview"];
  if (adPatterns.some((p) => url.toLowerCase().includes(p))) score -= 50;
  return Math.max(0, Math.min(100, score));
}

function addStream(tabId, item) {
  if (tabId < 0) return;
  const st = ensureTab(tabId);
  const basePath = item.url.split("?")[0];
  const score = calcRelevance(item.url, item.protocol, item.sizeBytes || 0, item.fromPlayer || false);
  const existing = st.streams.find((x) => x.url === item.url || (item.protocol === "mp4" && x.protocol === "mp4" && x.url.split("?")[0] === basePath));
  if (existing) {
    if (score > existing.relevance) existing.relevance = score;
    return;
  }
  const title = cleanPageTitle(item.title || st.title);
  st.streams.push({ ...item, title, relevance: score });
  st.streams.sort((a, b) => b.relevance - a.relevance);
  chrome.action.setBadgeText({ tabId, text: String(st.streams.length) });
  chrome.action.setBadgeBackgroundColor({ tabId, color: "#10B981" });
}

function addSubtitle(tabId, sub) {
  if (tabId < 0) return;
  const st = ensureTab(tabId);
  if (st.subtitles.some((x) => x.url === sub.url)) return;
  st.subtitles.push(sub);
}

chrome.tabs.onUpdated.addListener((tabId, changeInfo, tab) => {
  if (changeInfo.status === "loading" && changeInfo.url) {
    tabState.set(tabId, { url: changeInfo.url, title: tab.title || "", streams: [], subtitles: [], drm: null });
    chrome.action.setBadgeText({ tabId, text: "" });
  } else if (changeInfo.title) {
    ensureTab(tabId).title = changeInfo.title;
  }
});

chrome.tabs.onRemoved.addListener((tabId) => tabState.delete(tabId));

chrome.webRequest.onCompleted.addListener(
  (details) => {
    if (details.tabId < 0) return;
    const url = details.url || "";
    const pathOnly = url.split("?")[0].toLowerCase();
    const hdrs = details.responseHeaders || [];
    const ct = (hdrs.find((h) => h.name.toLowerCase() === "content-type")?.value || "").toLowerCase();
    const cl = parseInt(hdrs.find((h) => h.name.toLowerCase() === "content-length")?.value || "0", 10);

    if (pathOnly.endsWith(".vtt") || pathOnly.endsWith(".srt") || ct.includes("text/vtt") || ct.includes("subrip")) {
      const lang = pathOnly.includes("tr") ? "tr" : pathOnly.includes("en") ? "en" : "tr";
      addSubtitle(details.tabId, { url, label: pathOnly.split("/").pop() || "Dahili Altyazı", lang, source: "Dahili" });
      return;
    }

    if (pathOnly.endsWith(".m3u8") || ct.includes("mpegurl")) {
      addStream(details.tabId, { url, protocol: "hls", sizeBytes: cl });
    } else if (pathOnly.endsWith(".mpd") || ct.includes("dash+xml")) {
      addStream(details.tabId, { url, protocol: "dash", sizeBytes: cl });
    } else if ((pathOnly.endsWith(".mp4") || pathOnly.endsWith(".webm") || ct.startsWith("video/")) && !pathOnly.endsWith(".ts") && !ct.includes("mp2t")) {
      if (cl > 0 && cl < 512 * 1024) return;
      addStream(details.tabId, { url, protocol: "mp4", sizeBytes: cl });
    }
  },
  { urls: ["<all_urls>"] },
  ["responseHeaders"]
);

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  const tabId = msg.tabId ?? sender.tab?.id ?? -1;
  if (msg.type === "vf_media" && tabId >= 0) {
    addStream(tabId, { url: msg.url, protocol: msg.protocol || "mp4", title: msg.title, fromPlayer: !!msg.fromPlayer });
  } else if (msg.type === "vf_subtitle" && tabId >= 0) {
    addSubtitle(tabId, { url: msg.url, label: msg.label || "Dahili Altyazı", lang: msg.lang || "tr", source: "Dahili" });
  } else if (msg.type === "vf_drm" && tabId >= 0) {
    const st = ensureTab(tabId);
    st.drm = msg.keySystem || "EME";
    chrome.action.setBadgeText({ tabId, text: "🔒" });
  } else if (msg.type === "vf_get_state") {
    chrome.tabs.get(msg.tabId, (tab) => {
      const st = ensureTab(msg.tabId);
      if (tab?.title) st.title = tab.title;
      sendResponse(st);
    });
    return true;
  }
});
