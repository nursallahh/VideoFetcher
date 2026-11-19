# Video Fetcher

**Video Fetcher**, içinde Chromium tabanlı tarayıcı ([Microsoft Edge WebView2](https://learn.microsoft.com/en-us/microsoft-edge/webview2/)) barındıran, arka planda açık medya akışlarını (HLS, DASH, MP4/WebM) tespit edip kayıpsız remux (`ffmpeg -c copy`) ile kaydeden ve aynı Wi-Fi üzerinden QR kod ile mobil cihazlara aktarabilen açık kaynaklı masaüstü uygulamasıdır.

## Temel Özellikler
- **3-Tık İndirme Akışı:** Sayfayı aç → Bildirimden veya yüzen HUD kartından kalite/cihaz seç → İndir.
- **Kayıpsız Öncelikli Medya İşleme:** Önce `-c copy -movflags +faststart` ile kayıpsız MP4/MKV paketleme yapılır; yalnızca hedef cihaz (ör. iOS/mobil) codec'i desteklemiyorsa kullanıcı bilgilendirilerek donanım hızlandırmalı (NVENC/QSV/AMF) dönüştürme uygulanır.
- **Yüksek Hızlı Paralel İndirme:** `Accept-Ranges: bytes` destekli dosyalarda `RandomAccess.WriteAsync` ile 16–32 kanallı paralel parça indirme ve çok kanallı HLS/DASH segment indirme.
- **Yerel Wi-Fi QR & HTTP Aktarım:** Gömülü Kestrel sunucusu ve 15 dakikalık tek kullanımlık token ile telefon/tablete kablosuz aktarım.

## Kurulum ve Gereksinimler
- Windows 10 (1809+) veya Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/) (kaynak koddan derlemek için)
- [WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) (Windows 11'de yerleşik)
- Harici araçlar (PATH üzerinde veya uygulama dizininde):
  - `ffmpeg` (LGPL/GPL CLI binary)
  - `yt-dlp` (Unlicense)

```bash
git clone https://github.com/<org>/video-fetcher.git
cd video-fetcher
dotnet build VideoFetcher.sln --configuration Release
dotnet test tests/VideoFetcher.UnitTests/VideoFetcher.UnitTests.csproj
```

## Yasal Sorumluluk, Telif Hakkı ve Etik Çerçeve
- **DRM Koruması Atlatılmaz:** Video Fetcher, Widevine, PlayReady veya FairPlay gibi Dijital Hak Yönetimi (DRM / EME) sistemlerini **atlatmaz, kırmaz ve şifre çözmez**. Korumalı akışlar tespit edildiğinde indirme işlemi teknik olarak kilitlenir ve kullanıcı bilgilendirilir.
- **Kullanıcı Sorumluluğu:** Bu yazılım yalnızca kişisel arşivleme, açık lisanslı/telifsiz medya, eğitim materyalleri veya içerik sahibinin açık iznine sahip olunan videolar için kullanılmalıdır. Kullanıcılar, ziyaret ettikleri web sitelerinin kullanım koşullarına ve yürürlükteki telif hakkı yasalarına uymaktan bizzat sorumludur.

## Üçüncü Taraf Lisans Uyumu
- **Video Fetcher (.NET / C#):** MIT Lisansı
- **FFmpeg:** LGPL v2.1+ / GPL v2+ (harici CLI süreç olarak çağrılır)
- **yt-dlp:** The Unlicense (harici CLI süreç olarak çağrılır)
