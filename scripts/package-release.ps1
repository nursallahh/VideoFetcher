param(
    [string]$OutputDir = "./dist/release"
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

dotnet test tests/VideoFetcher.UnitTests/VideoFetcher.UnitTests.csproj -c Release
dotnet publish src/VideoFetcher.App/VideoFetcher.App.csproj -c Release -p:Platform=x64 --self-contained true -o "$OutputDir/VideoFetcher-win-x64"

Compress-Archive -Path "$OutputDir/VideoFetcher-win-x64/*" -DestinationPath "$OutputDir/VideoFetcher-win-x64.zip" -Force
Compress-Archive -Path "extensions/chrome-mv3/*" -DestinationPath "$OutputDir/VideoFetcher-ChromeMV3.zip" -Force
Write-Host "Release paketleri hazır: $OutputDir"
