# Video Fetcher

Modern, yüksek hızlı masaüstü video indirme ve medya dönüştürme aracı (.NET 10 / WinUI 3 / WebView2) + Chrome/Edge Manifest V3 eklentisi.

## Özellikler

- **Gömülü WebView2 Tarayıcı:** Çoklu sekme, yerleşik reklam/izleyici kalkanı, oturum geri yükleme, yer imleri ve ziyaret geçmişi.
- **Evrensel Akış Algılama:** HLS (`.m3u8`, fMP4, ayrı ses ve gömülü altyazı izleri), MPEG-DASH (`.mpd`) ve doğrudan MP4/WebM akışlarını otomatik tanır; DRM korumalı içerikleri engeller.
- **64 Kanal Adaptif İndirme:** HTTP/2 & HTTP/3 destekli, 32→64 kanal AIMD hız yönetimi ve `Zero-RAM` akış tabanlı segment birleştirme.
- **Kayıpsız Birleştirme & Akıllı İyileştirme:** Varsayılan kayıpsız remux (`-c:v copy`), otomatik HDR → SDR ton eşleme, opsiyonel çözünürlüğe duyarlı görüntü iyileştirme ve zaman aralığı kırpma (`-ss` / `-to`).
- **Dahili + Çoklu Veritabanı Altyazı:** Sayfa içi `.vtt`/`.srt` altyazıların yanında IMDb eşleştirmeli OpenSubtitles & Podnapisi araması ve otomatik hizalamalı soft-sub (`mov_text`) gömme.
- **İlk Açılışta Otomatik Araç Kurulumu:** Eksik `yt-dlp.exe`, `ffmpeg.exe` ve `ffprobe.exe` dosyalarını ilk açılışta kullanıcı onayıyla `%LocalAppData%\VideoFetcher\tools` altına otomatik kurar.

## Kurulum & Çalıştırma

### Hazır Sürüm (Windows x64)
1. [Releases](../../releases) sayfasından son `VideoFetcher-win-x64.zip` dosyasını indirip klasöre çıkarın.
2. `VideoFetcher.App.exe` dosyasını çalıştırın.
3. Eksik medya araçları varsa açılan pencerede **İzin Ver ve İndir** seçeneğini onaylayın.

### Kaynak Koddan Derleme
Gereksinimler: [.NET 10 SDK](https://dotnet.microsoft.com/) ve Windows 10/11.

```powershell
dotnet test tests/VideoFetcher.UnitTests/VideoFetcher.UnitTests.csproj
dotnet publish src/VideoFetcher.App/VideoFetcher.App.csproj -c Release -p:Platform=x64 --self-contained true -o ./dist/VideoFetcher
```

### Chrome / Edge Eklentisi
1. Tarayıcıda `chrome://extensions` (veya `edge://extensions`) adresini açın.
2. **Geliştirici modu**nu etkinleştirin ve **Paketi açılmış öğe yükle** ile `extensions/chrome-mv3` klasörünü seçin.

## Lisans

[MIT](LICENSE)
