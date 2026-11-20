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

Get-Process -Name "VideoFetcher.App" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

function Sync-DeltaDirectory {
    param([string]$SourceDir, [string]$TargetDir)
    $updated = 0
    $files = Get-ChildItem -Path $SourceDir -Recurse -File
    foreach ($f in $files) {
        $rel = $f.FullName.Substring($SourceDir.Length).TrimStart('\', '/')
        $dest = Join-Path $TargetDir $rel
        $needCopy = $true
        if (Test-Path $dest) {
            $df = Get-Item $dest
            if ($df.Length -eq $f.Length -and (Get-FileHash $f.FullName -Algorithm SHA256).Hash -eq (Get-FileHash $dest -Algorithm SHA256).Hash) {
                $needCopy = $false
            }
        }
        if ($needCopy) {
            New-Item -ItemType Directory -Force -Path (Split-Path $dest -Parent) | Out-Null
            Copy-Item -Path $f.FullName -Destination $dest -Force
            $updated++
        }
    }
    Write-Host "$updated değişen/yeni dosya güncellendi (değişmeyen dosyalar korundu)." -ForegroundColor Green
}

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

$stageDir = Join-Path $env:TEMP ("vf_stage_" + [guid]::NewGuid().ToString("N").Substring(0, 8))
try {
    if ($assetUrl) {
        $zipPath = Join-Path $env:TEMP "VideoFetcher-win-x64.zip"
        Write-Host "Son sürüm indiriliyor: $assetUrl" -ForegroundColor Green
        Invoke-WebRequest -Uri $assetUrl -OutFile $zipPath -UseBasicParsing
        Expand-Archive -Path $zipPath -DestinationPath $stageDir -Force
        Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
        Sync-DeltaDirectory -SourceDir $stageDir -TargetDir $InstallDir
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
        & dotnet publish $proj.FullName -c Release -p:Platform=x64 --self-contained true -o $stageDir
        if ($LASTEXITCODE -ne 0) { throw "Derleme başarısız oldu." }

        Sync-DeltaDirectory -SourceDir $stageDir -TargetDir $InstallDir
        Remove-Item $srcZip -Force -ErrorAction SilentlyContinue
        Remove-Item $srcDir -Recurse -Force -ErrorAction SilentlyContinue
    }
} finally {
    if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue }
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
