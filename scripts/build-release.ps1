$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

New-Item -ItemType Directory -Force -Path "dist" | Out-Null
Copy-Item -Force "bin\Release\net10.0-windows\win-x64\publish\SpotifyLyricsOverlay.Wpf.exe" "dist\SpotifyLyricsOverlay.Wpf.exe"
Copy-Item -Force "USER_GUIDE.txt" "dist\USER_GUIDE.txt"
Compress-Archive -Force -Path "dist\SpotifyLyricsOverlay.Wpf.exe","dist\USER_GUIDE.txt" -DestinationPath "dist\SpotifyLyricsOverlay-Windows-x64.zip"

$iscc = Get-Command iscc.exe -ErrorAction SilentlyContinue
$defaultIscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if ($iscc) {
    & $iscc.Source "installer\SpotifyLyricsOverlay.iss"
} elseif (Test-Path $defaultIscc) {
    & $defaultIscc "installer\SpotifyLyricsOverlay.iss"
} else {
    Write-Host "Inno Setup not found. Zip package was created; install Inno Setup and rerun this script to build the setup exe."
}
