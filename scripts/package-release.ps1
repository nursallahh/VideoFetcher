param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "./artifacts/release"
)

$ErrorActionPreference = "Stop"
Write-Host "Video Fetcher derleniyor ($Configuration | $Runtime)..." -ForegroundColor Cyan

dotnet test ./tests/VideoFetcher.UnitTests/VideoFetcher.UnitTests.csproj -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Birim testleri basarisiz!" }

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
dotnet publish ./src/VideoFetcher.Core/VideoFetcher.Core.csproj -c $Configuration -r $Runtime --self-contained false -o "$OutputDir/$Runtime"

$zipPath = "$OutputDir/VideoFetcher-$Runtime.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path "$OutputDir/$Runtime/*" -DestinationPath $zipPath

Write-Host "Paket hazir: $zipPath" -ForegroundColor Green
