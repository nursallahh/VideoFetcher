# Video Fetcher

High-speed, lossless desktop video & subtitle downloader for Windows (`.NET 10` / `WinUI 3` / `WebView2`) with a companion **Chrome / Edge Manifest V3** extension.

---

## Key Features

- **Embedded WebView2 Browser:** Multi-tab browsing, built-in ad/tracker shield (`WebResourceRequested`), session restore, bookmarks (`⭐`), browsing history (`🕒`), and an in-app media library (`📚`).
- **Universal Stream Detection:** Automatically captures HLS (`.m3u8`, fMP4, separate audio, and embedded subtitle tracks), MPEG-DASH (`.mpd`), and direct MP4/WebM streams—even when extensions or MIME types are obfuscated—while blocking DRM-protected content.
- **64-Channel Adaptive Download Engine:** HTTP/2 & HTTP/3 support, `32 → 64` channel AIMD concurrency scaling, fake segment header stripping, and `Zero-RAM` streaming merge.
- **Lossless Remuxing & Enhancement:**
  - Default **Lossless + Auto HDR→SDR** profile saves standard SDR videos untouched via `-c:v copy`.
  - Optional **Cinema Enhancement** mode uses GPU acceleration (`NVENC` / `QSV` / `AMF`) for `CAS` sharpening on lower-resolution content and subtle color/contrast balancing on high-resolution video.
- **In-Page + Multi-Database Subtitles:** Captures in-page `.vtt`/`.srt` tracks alongside IMDb-matched OpenSubtitles & Podnapisi search, with automatic start-time-aligned soft-subtitle (`mov_text`) embedding.
- **Queue, Trimming & Binge Mode:** Sequential download queue, `-ss` / `-to` time-range trimming, and automatic episode downloading.
- **First-Run Tool Bootstrap & Auto-Update:** Prompts for permission on first launch to install missing `yt-dlp.exe`, `ffmpeg.exe`, and `ffprobe.exe` into `%LocalAppData%\VideoFetcher\tools`, and notifies users in-app when new GitHub Releases are published.

---

## Installation

### 1. One-Line PowerShell Installer (Recommended)
Checks system architecture, downloads the latest `VideoFetcher-win-x64.zip` from GitHub Releases (or installs `.NET 10 SDK` via `winget` and builds from source if no release binary is found), preserves existing tabs/bookmarks/settings on upgrades, creates a Desktop shortcut, and launches the app:

```powershell
irm https://raw.githubusercontent.com/nursallahh/VideoFetcher/main/scripts/install.ps1 | iex
```

### 2. Prebuilt Release (Manual ZIP)
1. Download the latest `VideoFetcher-win-x64.zip` from the [Releases](https://github.com/nursallahh/VideoFetcher/releases) page and extract it.
2. Run `VideoFetcher.App.exe`.
3. If prompted for missing media tools, click **İzin Ver ve İndir** (Allow & Download).

### 3. Build from Source
Requirements: Windows 10/11 (x64) and [.NET 10 SDK](https://dotnet.microsoft.com/).

```powershell
git clone https://github.com/nursallahh/VideoFetcher.git
cd VideoFetcher
dotnet test tests/VideoFetcher.UnitTests/VideoFetcher.UnitTests.csproj
powershell -ExecutionPolicy Bypass -File .\scripts\package-release.ps1
```

---

## Chrome / Edge Manifest V3 Extension *(In Development)*

1. Open `chrome://extensions` (or `edge://extensions`) in your browser.
2. Enable **Developer mode** in the top-right corner.
3. Click **Load unpacked** and select the `extensions/chrome-mv3` folder.

---

## Project Structure

```text
VideoFetcher/
├── src/
│   ├── VideoFetcher.Core/            # Models, HLS/DASH parsers, conversion planner
│   ├── VideoFetcher.Infrastructure/  # 64-ch downloader, FFmpeg, yt-dlp, subtitles, bootstrap
│   └── VideoFetcher.App/             # WinUI 3 + WebView2 desktop UI
├── tests/
│   └── VideoFetcher.UnitTests/       # xUnit unit tests
├── extensions/
│   └── chrome-mv3/                   # Chrome / Edge Manifest V3 extension (In Development)
├── scripts/
│   ├── install.ps1                   # One-line smart installer & updater
│   └── package-release.ps1           # Release packaging script
└── .github/workflows/                # CI & Release automation
```

---

## Troubleshooting

- **PowerShell script execution policy error:** Run `powershell -ExecutionPolicy Bypass -File .\scripts\package-release.ps1` if direct script execution is restricted.
- **Missing FFmpeg / yt-dlp:** The app prompts once on first launch to download `yt-dlp.exe`, `ffmpeg.exe`, and `ffprobe.exe` from official GitHub releases.

## License

[MIT](LICENSE)
