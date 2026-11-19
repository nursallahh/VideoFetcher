// Pure-JS MPEG-TS (H.264 + AAC) -> MP4 (ISO-BMFF) Transmuxer for Chrome MV3
(function (global) {
  function u16(v) { return [(v >> 8) & 255, v & 255]; }
  function u32(v) { return [(v >>> 24) & 255, (v >>> 16) & 255, (v >>> 8) & 255, v & 255]; }
  function i32(v) { return u32(v | 0); }
  function strBytes(s) { return Array.from(s, (c) => c.charCodeAt(0)); }
  function box(type, ...parts) {
    const len = 8 + parts.reduce((a, b) => a + b.length, 0);
    const out = new Uint8Array(len);
    out.set(u32(len), 0);
    out.set(strBytes(type), 4);
    let off = 8;
    for (const p of parts) { out.set(p, off); off += p.length; }
    return out;
  }
  function fullBox(type, ver, flags, ...parts) {
    return box(type, new Uint8Array([ver, (flags >> 16) & 255, (flags >> 8) & 255, flags & 255]), ...parts);
  }

  // 33-bit unsigned PTS/DTS parser (avoids JS 32-bit signed << overflow)
  function parsePts(buf, off) {
    return ((buf[off] & 0x0e) * 536870912) +
           (buf[off + 1] * 4194304) +
           ((buf[off + 2] & 0xfe) * 16384) +
           (buf[off + 3] * 128) +
           ((buf[off + 4] & 0xfe) >> 1);
  }

  function splitAnnexB(data) {
    const nals = [];
    let i = 0, len = data.length, start = -1;
    while (i < len - 3) {
      if (data[i] === 0 && data[i + 1] === 0 && (data[i + 2] === 1 || (data[i + 2] === 0 && data[i + 3] === 1))) {
        const scLen = data[i + 2] === 1 ? 3 : 4;
        if (start >= 0) nals.push(data.subarray(start, i));
        start = i + scLen;
        i += scLen;
      } else i++;
    }
    if (start >= 0 && start < len) nals.push(data.subarray(start));
    return nals;
  }

  function concatU8(arrays) {
    const total = arrays.reduce((a, b) => a + b.length, 0);
    const out = new Uint8Array(total);
    let off = 0;
    for (const a of arrays) { out.set(a, off); off += a.length; }
    return out;
  }

  function transmuxTsToMp4(tsBytes) {
    let pmtPid = -1, videoPid = -1, audioPid = -1;
    const pesBuffers = new Map();
    const videoSamples = [];
    const audioSamples = [];
    let sps = null, pps = null;
    let audioProfile = 2, audioFreqIdx = 4, audioChannels = 2;

    const flushPes = (pid) => {
      const chunks = pesBuffers.get(pid);
      if (!chunks || chunks.length === 0) return;
      pesBuffers.set(pid, []);
      const pes = concatU8(chunks);
      if (pes.length < 9 || pes[0] !== 0 || pes[1] !== 0 || pes[2] !== 1) return;
      const flags = pes[7];
      const hdrLen = pes[8];
      const payloadStart = 9 + hdrLen;
      if (payloadStart >= pes.length) return;
      let pts = 0, dts = 0;
      if (flags & 0x80) pts = parsePts(pes, 9);
      dts = (flags & 0x40) ? parsePts(pes, 14) : pts;
      const payload = pes.subarray(payloadStart);

      if (pid === videoPid) {
        const nals = splitAnnexB(payload);
        const frameNals = [];
        let isKey = false;
        for (const nal of nals) {
          if (nal.length === 0) continue;
          const nt = nal[0] & 0x1f;
          if (nt === 7) sps = nal.slice();
          else if (nt === 8) pps = nal.slice();
          else if (nt === 9 || nt === 6) continue;
          else {
            if (nt === 5) isKey = true;
            const lenPrefixed = new Uint8Array(4 + nal.length);
            lenPrefixed.set(u32(nal.length), 0);
            lenPrefixed.set(nal, 4);
            frameNals.push(lenPrefixed);
          }
        }
        if (frameNals.length > 0) {
          videoSamples.push({ data: concatU8(frameNals), pts, dts, isKey });
        }
      } else if (pid === audioPid) {
        let off = 0;
        while (off + 7 <= payload.length) {
          if (payload[off] === 0xff && (payload[off + 1] & 0xf0) === 0xf0) {
            const frameLen = ((payload[off + 3] & 0x03) << 11) | (payload[off + 4] << 3) | ((payload[off + 5] & 0xe0) >> 5);
            if (frameLen < 7 || off + frameLen > payload.length) break;
            audioProfile = ((payload[off + 2] & 0xc0) >> 6) + 1;
            audioFreqIdx = (payload[off + 2] & 0x3c) >> 2;
            audioChannels = ((payload[off + 2] & 0x01) << 2) | ((payload[off + 3] & 0xc0) >> 6);
            const hdrSize = (payload[off + 1] & 0x01) ? 7 : 9;
            if (off + hdrSize < off + frameLen) {
              audioSamples.push({ data: payload.subarray(off + hdrSize, off + frameLen) });
            }
            off += frameLen;
          } else off++;
        }
      }
    };

    let i = 0;
    while (i + 188 <= tsBytes.length) {
      if (tsBytes[i] !== 0x47 || (i + 376 < tsBytes.length && (tsBytes[i + 188] !== 0x47 || tsBytes[i + 376] !== 0x47))) { i++; continue; }
      const pusi = (tsBytes[i + 1] & 0x40) !== 0;
      const pid = ((tsBytes[i + 1] & 0x1f) << 8) | tsBytes[i + 2];
      const afc = (tsBytes[i + 3] & 0x30) >> 4;
      let off = i + 4;
      if (afc === 2 || afc === 0) { i += 188; continue; }
      if (afc === 3) off += 1 + tsBytes[off];
      if (off >= i + 188) { i += 188; continue; }
      const pktData = tsBytes.subarray(off, i + 188);

      if (pid === 0 && pusi) {
        const ptr = pktData[0];
        const sec = pktData.subarray(1 + ptr);
        if (sec.length >= 12) {
          const secLen = ((sec[1] & 0x0f) << 8) | sec[2];
          const end = Math.min(sec.length, 3 + secLen - 4);
          for (let p = 8; p + 4 <= end; p += 4) {
            const progNum = (sec[p] << 8) | sec[p + 1];
            if (progNum !== 0) { pmtPid = ((sec[p + 2] & 0x1f) << 8) | sec[p + 3]; break; }
          }
        }
      } else if (pid === pmtPid && pusi) {
        const ptr = pktData[0];
        const sec = pktData.subarray(1 + ptr);
        if (sec.length >= 12) {
          const secLen = ((sec[1] & 0x0f) << 8) | sec[2];
          const progInfoLen = ((sec[10] & 0x0f) << 8) | sec[11];
          let p = 12 + progInfoLen;
          const end = Math.min(sec.length, 3 + secLen - 4);
          while (p + 5 <= end) {
            const st = sec[p];
            const ep = ((sec[p + 1] & 0x1f) << 8) | sec[p + 2];
            const esLen = ((sec[p + 3] & 0x0f) << 8) | sec[p + 4];
            if (st === 0x1b && videoPid < 0) videoPid = ep;
            else if (st === 0x0f && audioPid < 0) audioPid = ep;
            p += 5 + esLen;
          }
        }
      } else if (pid === videoPid || pid === audioPid) {
        if (pusi) flushPes(pid);
        if (!pesBuffers.has(pid)) pesBuffers.set(pid, []);
        pesBuffers.get(pid).push(pktData);
      }
      i += 188;
    }
    if (videoPid >= 0) flushPes(videoPid);
    if (audioPid >= 0) flushPes(audioPid);

    if (videoSamples.length === 0 || !sps || !pps) return null;

    const width = 1280, height = 720;
    const timescale = 90000;
    const deltas = [];
    let totalDur = 0;
    for (let k = 0; k < videoSamples.length; k++) {
      let d = k + 1 < videoSamples.length ? (videoSamples[k + 1].dts - videoSamples[k].dts) : 3000;
      if (d <= 0 || d > 90000) d = 3000;
      deltas.push(d);
      totalDur += d;
    }
    totalDur = Math.max(3000, totalDur >>> 0);

    const vData = concatU8(videoSamples.map((s) => s.data));
    const aData = concatU8(audioSamples.map((s) => s.data));
    const mdatHeaderLen = 8;

    const buildVideoTrak = (vOffset) => {
      const avcC = box("avcC", new Uint8Array([1, sps[1], sps[2], sps[3], 0xff, 0xe1, ...u16(sps.length), ...sps, 1, ...u16(pps.length), ...pps]));
      const avc1 = box("avc1", new Uint8Array([0,0,0,0,0,0,0,1, 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0, ...u16(width), ...u16(height), 0,72,0,0, 0,72,0,0, 0,0,0,0, 0,1, 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0, 0,24, 0xff,0xff]), avcC);
      const stsd = fullBox("stsd", 0, 0, new Uint8Array(u32(1)), avc1);

      const sttsBuf = new Uint8Array(4 + deltas.length * 8);
      sttsBuf.set(u32(deltas.length), 0);
      for (let k = 0; k < deltas.length; k++) {
        sttsBuf.set(u32(1), 4 + k * 8);
        sttsBuf.set(u32(deltas[k]), 8 + k * 8);
      }
      const stts = fullBox("stts", 0, 0, sttsBuf);

      const hasCtts = videoSamples.some((s) => s.pts !== s.dts);
      let ctts = null;
      if (hasCtts) {
        const cttsBuf = new Uint8Array(4 + videoSamples.length * 8);
        cttsBuf.set(u32(videoSamples.length), 0);
        for (let k = 0; k < videoSamples.length; k++) {
          cttsBuf.set(u32(1), 4 + k * 8);
          cttsBuf.set(i32(videoSamples[k].pts - videoSamples[k].dts), 8 + k * 8);
        }
        ctts = fullBox("ctts", 1, 0, cttsBuf);
      }

      const keys = [];
      for (let k = 0; k < videoSamples.length; k++) if (videoSamples[k].isKey) keys.push(k + 1);
      const stssBuf = new Uint8Array(4 + keys.length * 4);
      stssBuf.set(u32(keys.length), 0);
      for (let k = 0; k < keys.length; k++) stssBuf.set(u32(keys[k]), 4 + k * 4);
      const stss = fullBox("stss", 0, 0, stssBuf);

      const stsc = fullBox("stsc", 0, 0, new Uint8Array(u32(1)), new Uint8Array([...u32(1), ...u32(videoSamples.length), ...u32(1)]));
      const stszBuf = new Uint8Array(8 + videoSamples.length * 4);
      stszBuf.set(u32(0), 0);
      stszBuf.set(u32(videoSamples.length), 4);
      for (let k = 0; k < videoSamples.length; k++) stszBuf.set(u32(videoSamples[k].data.length), 8 + k * 4);
      const stsz = fullBox("stsz", 0, 0, stszBuf);

      const stco = fullBox("stco", 0, 0, new Uint8Array([...u32(1), ...u32(vOffset)]));
      const stblParts = ctts ? [stsd, stts, ctts, stss, stsc, stsz, stco] : [stsd, stts, stss, stsc, stsz, stco];
      const stbl = box("stbl", ...stblParts);

      const dref = fullBox("dref", 0, 0, new Uint8Array(u32(1)), fullBox("url ", 0, 1));
      const dinf = box("dinf", dref);
      const minf = box("minf", fullBox("vmhd", 0, 1, new Uint8Array(8)), dinf, stbl);
      const mdhd = fullBox("mdhd", 0, 0, new Uint8Array([...u32(0), ...u32(0), ...u32(timescale), ...u32(totalDur), 0x55, 0xc4, 0, 0]));
      const hdlr = fullBox("hdlr", 0, 0, new Uint8Array([...u32(0), ...strBytes("vide"), ...u32(0), ...u32(0), ...u32(0), ...strBytes("VideoHandler\0")]));
      const mdia = box("mdia", mdhd, hdlr, minf);
      const tkhd = fullBox("tkhd", 0, 3, new Uint8Array([...u32(0), ...u32(0), ...u32(1), ...u32(0), ...u32(totalDur), ...u32(0), ...u32(0), ...u16(0), ...u16(0), ...u16(0), ...u16(0), ...u32(0x10000),0,0,0,0, ...u32(0), ...u32(0x10000),0,0,0,0, ...u32(0), ...u32(0x40000000), ...u32(width << 16), ...u32(height << 16)]));
      return box("trak", tkhd, mdia);
    };

    const buildAudioTrak = (aOffset) => {
      if (audioSamples.length === 0) return null;
      const freqMap = [96000,88200,64000,48000,44100,32000,24000,22050,16000,12000,11025,8000,7350];
      const sr = freqMap[audioFreqIdx] || 44100;
      const asc = new Uint8Array([((audioProfile << 3) | (audioFreqIdx >> 1)) & 255, (((audioFreqIdx & 1) << 7) | (audioChannels << 3)) & 255]);
      const esds = fullBox("esds", 0, 0, new Uint8Array([3, 25, 0, 0, 0, 4, 17, 0x40, 0x15, 0, 0, 0, 0, 2, 0xee, 0, 0, 2, 0xee, 0, 5, 2, ...asc, 6, 1, 2]));
      const mp4a = box("mp4a", new Uint8Array([0,0,0,0,0,0,0,1, 0,0,0,0,0,0,0,0, ...u16(audioChannels), ...u16(16), 0,0, 0,0, ...u16(sr), 0,0]), esds);
      const stsd = fullBox("stsd", 0, 0, new Uint8Array(u32(1)), mp4a);
      const stts = fullBox("stts", 0, 0, new Uint8Array([...u32(1), ...u32(audioSamples.length), ...u32(1024)]));
      const stsc = fullBox("stsc", 0, 0, new Uint8Array([...u32(1), ...u32(1), ...u32(audioSamples.length), ...u32(1)]));

      const stszBuf = new Uint8Array(8 + audioSamples.length * 4);
      stszBuf.set(u32(0), 0);
      stszBuf.set(u32(audioSamples.length), 4);
      for (let k = 0; k < audioSamples.length; k++) stszBuf.set(u32(audioSamples[k].data.length), 8 + k * 4);
      const stsz = fullBox("stsz", 0, 0, stszBuf);

      const stco = fullBox("stco", 0, 0, new Uint8Array([...u32(1), ...u32(aOffset)]));
      const stbl = box("stbl", stsd, stts, stsc, stsz, stco);
      const dinf = box("dinf", fullBox("dref", 0, 0, new Uint8Array(u32(1)), fullBox("url ", 0, 1)));
      const minf = box("minf", fullBox("smhd", 0, 0, new Uint8Array(4)), dinf, stbl);
      const aDur = audioSamples.length * 1024;
      const mdhd = fullBox("mdhd", 0, 0, new Uint8Array([...u32(0), ...u32(0), ...u32(sr), ...u32(aDur), 0x55, 0xc4, 0, 0]));
      const hdlr = fullBox("hdlr", 0, 0, new Uint8Array([...u32(0), ...strBytes("soun"), ...u32(0), ...u32(0), ...u32(0), ...strBytes("SoundHandler\0")]));
      const mdia = box("mdia", mdhd, hdlr, minf);
      const tkhd = fullBox("tkhd", 0, 3, new Uint8Array([...u32(0), ...u32(0), ...u32(2), ...u32(0), ...u32(totalDur), ...u32(0), ...u32(0), ...u16(0), ...u16(0), ...u16(0x0100), ...u16(0), ...u32(0x10000),0,0,0,0, ...u32(0), ...u32(0x10000),0,0,0,0, ...u32(0), ...u32(0x40000000), ...u32(0), ...u32(0)]));
      return box("trak", tkhd, mdia);
    };

    const ftyp = box("ftyp", new Uint8Array([...strBytes("isom"), ...u32(512), ...strBytes("isomiso2avc1mp41")]));
    const mvhd = fullBox("mvhd", 0, 0, new Uint8Array([...u32(0), ...u32(0), ...u32(timescale), ...u32(totalDur), ...u32(0x10000), ...u16(0x0100), 0,0, 0,0,0,0,0,0,0,0, ...u32(0x10000),0,0,0,0, ...u32(0), ...u32(0x10000),0,0,0,0, ...u32(0), ...u32(0x40000000), ...new Array(24).fill(0), ...u32(3)]));

    const dummyV = buildVideoTrak(0);
    const dummyA = buildAudioTrak(0);
    const dummyMoov = dummyA ? box("moov", mvhd, dummyV, dummyA) : box("moov", mvhd, dummyV);
    const vOff = ftyp.length + dummyMoov.length + mdatHeaderLen;
    const aOff = vOff + vData.length;

    const realV = buildVideoTrak(vOff);
    const realA = buildAudioTrak(aOff);
    const moov = realA ? box("moov", mvhd, realV, realA) : box("moov", mvhd, realV);
    const mdat = box("mdat", vData, aData);

    return concatU8([ftyp, moov, mdat]);
  }

  global.transmuxTsToMp4 = transmuxTsToMp4;
  if (typeof module !== "undefined" && module.exports) module.exports = { transmuxTsToMp4 };
})(typeof self !== "undefined" ? self : globalThis);
