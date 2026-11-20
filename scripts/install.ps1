param(
    [string]$RepoOwner = "nursallahh",
    [string]$RepoName = "VideoFetcher",
    [string]$InstallDir = "$env:LOCALAPPDATA\VideoFetcher\App"
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

Write-Host "=== Video Fetcher Kurulum / Güncelleme Başlatılıyor ===" -ForegroundColor Cyan

if (-not [Environment]::Is64BitOperatingSystem) {
    throw "Video Fetcher yalnızca 64-bit (x64) Windows sistemlerini destekler."
}

# Çalışan eski VideoFetcher varsa kapat (dosya kilidini önle)
Get-Process -Name "VideoFetcher.App" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400

# Yalnızca uygulama binary klasörünü (.../VideoFetcher/App) temizle;
# browser_state.json (açık sekmeler, yer imleri, geçmiş), WebView2 çerezleri ve tools/ korunur.
if (Test-Path $InstallDir) {
    Write-Host "Eski uygulama dosyaları temizleniyor (ayarlar ve açık sekmeler korunuyor)..." -ForegroundColor Yellow
    Remove-Item -Path $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

$apiUrl = "https://api.github.com/repos/$RepoOwner/$RepoName/releases/latest"
$assetUrl = $null

try {
    $headers = @{ "User-Agent" = "VideoFetcher-Installer" }
    $release = Invoke-RestMethod -Uri $apiUrl -Headers $headers -TimeoutSec 10
    $asset = $release.assets | Where-Object { $_.name -like "*win-x64*.zip" } | Select-Object -First 1
    if ($asset) { $assetUrl = $asset.browser_download_url }
} catch {
    Write-Host "Hazır Release paketi bulunamadı, kaynak koddan derleme kontrol ediliyor..." -ForegroundColor Yellow
}

if ($assetUrl) {
    $zipPath = Join-Path $env:TEMP "VideoFetcher-win-x64.zip"
    Write-Host "Son sürüm indiriliyor: $assetUrl" -ForegroundColor Green
    Invoke-WebRequest -Uri $assetUrl -OutFile $zipPath -UseBasicParsing
    Expand-Archive -Path $zipPath -DestinationPath $InstallDir -Force
    Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
} else {
    $hasDotnet = $false
    try {
        $sdks = & dotnet --list-sdks 2>$null
        if ($sdks -match "^10\.") { $hasDotnet = $true }
    } catch {}

    if (-not $hasDotnet) {
        Write-Host ".NET 10 SDK bulunamadı, winget ile kuruluyor..." -ForegroundColor Yellow
        winget install Microsoft.DotNet.SDK.10 --accept-package-agreements --accept-source-agreements
        $env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path", "User")
    }

    $srcZip = Join-Path $env:TEMP "VideoFetcher-main.zip"
    $srcDir = Join-Path $env:TEMP "VideoFetcher-src"
    if (Test-Path $srcDir) { Remove-Item $srcDir -Recurse -Force }
    Write-Host "Kaynak kod indiriliyor..." -ForegroundColor Green
    Invoke-WebRequest -Uri "https://github.com/$RepoOwner/$RepoName/archive/refs/heads/main.zip" -OutFile $srcZip -UseBasicParsing
    Expand-Archive -Path $srcZip -DestinationPath $srcDir -Force

    $proj = Get-ChildItem -Path $srcDir -Recurse -Filter "VideoFetcher.App.csproj" | Select-Object -First 1
    if (-not $proj) { throw "VideoFetcher.App.csproj bulunamadı!" }

    Write-Host "Uygulama derleniyor..." -ForegroundColor Green
    & dotnet publish $proj.FullName -c Release -p:Platform=x64 --self-contained true -o $InstallDir
    if ($LASTEXITCODE -ne 0) { throw "Derleme başarısız oldu." }

    Remove-Item $srcZip -Force -ErrorAction SilentlyContinue
    Remove-Item $srcDir -Recurse -Force -ErrorAction SilentlyContinue
}

$exePath = Join-Path $InstallDir "VideoFetcher.App.exe"
if (-not (Test-Path $exePath)) { throw "VideoFetcher.App.exe kurulum klasöründe bulunamadı: $exePath" }

$desktop = [Environment]::GetFolderPath("Desktop")
$lnkPath = Join-Path $desktop "Video Fetcher.lnk"
$wsh = New-Object -ComObject WScript.Shell
$sc = $wsh.CreateShortcut($lnkPath)
$sc.TargetPath = $exePath
$sc.WorkingDirectory = $InstallDir
$sc.IconLocation = $exePath
$sc.Save()

Write-Host "Kurulum tamamlandı ✓ Masaüstü kısayolu: $lnkPath" -ForegroundColor Green
Start-Process $exePath
