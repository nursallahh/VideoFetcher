(() => {
  if (window.__vfExtHooked) return;
  window.__vfExtHooked = true;

  const send = (payload) => {
    try { chrome.runtime.sendMessage(payload); } catch {}
  };

  if (navigator.requestMediaKeySystemAccess) {
    const origDrm = navigator.requestMediaKeySystemAccess.bind(navigator);
    navigator.requestMediaKeySystemAccess = function (keySystem, configs) {
      send({ type: "vf_drm", keySystem });
      return origDrm(keySystem, configs);
    };
  }

  // Standart dışı uzantı/MIME için fetch ve XHR kancası
  const inspectText = (url, text) => {
    if (!url || !text || typeof text !== "string") return;
    const t = text.trimStart();
    if (t.startsWith("#EXTM3U")) send({ type: "vf_media", url, protocol: "hls", title: document.title, fromPlayer: true });
    else if (t.includes("<MPD")) send({ type: "vf_media", url, protocol: "dash", title: document.title, fromPlayer: true });
    else if (t.startsWith("WEBVTT")) send({ type: "vf_subtitle", url, label: "Site İçi Altyazı", lang: "tr" });
  };

  const origFetch = window.fetch;
  if (origFetch) {
    window.fetch = async function (...args) {
      const res = await origFetch.apply(this, args);
      try {
        const cl = parseInt(res.headers?.get("content-length") || "0", 10);
        if (cl === 0 || cl < 2097152) {
          res.clone().text().then((txt) => inspectText(res.url || (typeof args[0] === "string" ? args[0] : args[0]?.url), txt)).catch(() => {});
        }
      } catch {}
      return res;
    };
  }

  const origOpen = XMLHttpRequest.prototype.open;
  const origSend = XMLHttpRequest.prototype.send;
  XMLHttpRequest.prototype.open = function (m, u) { this.__vfUrl = u; return origOpen.apply(this, arguments); };
  XMLHttpRequest.prototype.send = function () {
    this.addEventListener("load", function () {
      try { if (this.responseType === "" || this.responseType === "text") inspectText(this.responseURL || this.__vfUrl, this.responseText); } catch {}
    });
    return origSend.apply(this, arguments);
  };

  const scanDom = () => {
    try {
      document.querySelectorAll("video, video source").forEach((el) => {
        const src = el.currentSrc || el.src;
        if (src && src.startsWith("http")) {
          send({ type: "vf_media", url: src, protocol: src.includes(".m3u8") ? "hls" : src.includes(".mpd") ? "dash" : "mp4", title: document.title, fromPlayer: true });
        }
      });
      document.querySelectorAll("track").forEach((tr) => {
        const src = tr.src;
        if (src && src.startsWith("http")) {
          send({ type: "vf_subtitle", url: src, label: tr.label || tr.srclang || "Site İçi Altyazı", lang: tr.srclang || "tr" });
        }
      });
    } catch {}
  };

  document.addEventListener("DOMContentLoaded", () => {
    scanDom();
    new MutationObserver(scanDom).observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ["src"] });
  });
})();
