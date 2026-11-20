# Video Fetcher

Windows (`.NET 10` / `WinUI 3` / `WebView2`) için yüksek hızlı, kayıpsız video ve altyazı indirme uygulaması + **Chrome / Edge Manifest V3** tarayıcı eklentisi.

---

## Temel Özellikler

- **Gömülü WebView2 Tarayıcı:** Çoklu sekme, yerleşik reklam/izleyici kalkanı (`WebResourceRequested`), oturum geri yükleme, yer imleri (`⭐`), ziyaret geçmişi (`🕒`) ve uygulama içi kütüphane (`📚`).
- **Evrensel Akış Yakalama:** HLS (`.m3u8`, fMP4, ayrı ses ve gömülü altyazı izleri), MPEG-DASH (`.mpd`) ve doğrudan MP4/WebM akışlarını uzantı/MIME gizlenmiş olsa bile ilk baytlardan ve `fetch`/`XHR` kancasından yakalar; DRM korumalı içerikleri engeller.
- **64 Kanal Adaptif İndirme Motoru:** HTTP/2 ve HTTP/3 destekli, `32 → 64` kanal AIMD hız yönetimi, sahte segment başlığı temizleme ve `Zero-RAM` akış tabanlı birleştirme.
- **Kayıpsız Birleştirme & Görüntü İyileştirme:**
  - Varsayılan **Kayıpsız + Otomatik HDR→SDR** modu normal SDR videolara dokunmadan anında `-c:v copy` ile kaydeder.
  - Opsiyonel **Sinema İyileştirme** modu GPU hızlandırma (`NVENC` / `QSV` / `AMF`) ile düşük çözünürlüklü içeriklerde `CAS` netleştirme, yüksek çözünürlükte hafif renk/kontrast dengesi uygular.
- **Dahili + Çoklu Veritabanı Altyazı:** Sayfa içi `.vtt`/`.srt` altyazıların yanında IMDb eşleştirmeli OpenSubtitles & Podnapisi araması ve videonun başlangıç zamanına otomatik hizalanan soft-sub (`mov_text`) gömme.
- **Kuyruk, Kırpma & Binge Modu:** Sıralı indirme kuyruğu, `-ss` / `-to` zaman aralığı kırpma ve bölüm geçişlerinde otomatik indirme.
- **İlk Açılışta Otomatik Araç Kurulumu:** Sistemde `yt-dlp.exe`, `ffmpeg.exe` veya `ffprobe.exe` eksikse ilk açılışta kullanıcı onayı alarak resmi GitHub sürümlerinden `%LocalAppData%\VideoFetcher\tools` altına kurar.

---

## Kurulum

### 1. Tek Satır PowerShell Kurulumu (Önerilen)
Bu komut önce sistem mimarisini kontrol eder; GitHub Releases'ta hazır `VideoFetcher-win-x64.zip` varsa indirir, yoksa `.NET 10 SDK` kurulumunu kontrol edip kaynak koddan derler, masaüstüne kısayol oluşturur ve uygulamayı başlatır:

```powershell
irm https://raw.githubusercontent.com/nursallahh/VideoFetcher/main/scripts/install.ps1 | iex
```

### 2. Hazır Sürüm (Manuel ZIP)
1. [Releases](https://github.com/nursallahh/VideoFetcher/releases) sayfasından son `VideoFetcher-win-x64.zip` dosyasını indirip bir klasöre çıkarın.
2. `VideoFetcher.App.exe` dosyasını çalıştırın.
3. Eksik medya araçları varsa açılan pencerede **İzin Ver ve İndir** seçeneğini onaylayın.

### 3. Kaynak Koddan Derleme (Geliştiriciler İçin)
Gereksinimler: Windows 10/11 (x64) ve [.NET 10 SDK](https://dotnet.microsoft.com/).

```powershell
git clone https://github.com/nursallahh/VideoFetcher.git
cd VideoFetcher
dotnet test tests/VideoFetcher.UnitTests/VideoFetcher.UnitTests.csproj
powershell -ExecutionPolicy Bypass -File .\scripts\package-release.ps1
```

---

## Chrome / Edge Manifest V3 Eklentisi Kurulumu

1. Tarayıcıda `chrome://extensions` (veya `edge://extensions`) adresini açın.
2. Sağ üstten **Geliştirici modu**nu açın.
3. **Paketi açılmış öğe yükle** butonuna tıklayıp `extensions/chrome-mv3` klasörünü seçin.
4. Eklenti, HLS segmentlerini tarayıcı içinde saf JS (`ts2mp4.js`) ile doğrudan `.mp4` formatına dönüştürür ve site içi/harici altyazıları listeler.

---

## Proje Yapısı

```text
VideoFetcher/
├── src/
│   ├── VideoFetcher.Core/            # Modeller, HLS/DASH ayrıştırıcılar, dönüştürme planlayıcı
│   ├── VideoFetcher.Infrastructure/  # 64 kanal indirici, FFmpeg, yt-dlp, altyazı servisleri, bootstrap
│   └── VideoFetcher.App/             # WinUI 3 + WebView2 masaüstü arayüzü
├── tests/
│   └── VideoFetcher.UnitTests/       # xUnit birim testleri
├── extensions/
│   └── chrome-mv3/                   # Chrome / Edge Manifest V3 eklentisi + ts2mp4.js
├── scripts/
│   ├── install.ps1                   # Tek satır otomatik kurulum betiği
│   └── package-release.ps1           # Release paketleme betiği
└── .github/workflows/                # CI & Release otomasyonu
```

---

## Sık Karşılaşılan Durumlar

- **PowerShell betik çalıştırma izni hatası:** `.\scripts\package-release.ps1` doğrudan engellenirse `powershell -ExecutionPolicy Bypass -File .\scripts\package-release.ps1` komutunu kullanın.
- **Araçlar kurulu değilse:** Uygulama ilk açılışta `yt-dlp.exe`, `ffmpeg.exe` ve `ffprobe.exe` için otomatik kurulum izni ister; onayladıktan sonra birkaç saniye içinde hazır olur.

## Lisans

[MIT](LICENSE)
