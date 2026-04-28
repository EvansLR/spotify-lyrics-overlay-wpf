$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
dotnet publish -c Release -r win-x64 --self-contained false -o "bin\Release\net10.0-windows\win-x64\publish-lite"

New-Item -ItemType Directory -Force -Path "dist" | Out-Null
Copy-Item -Force "bin\Release\net10.0-windows\win-x64\publish\SpotifyLyricsOverlay.Wpf.exe" "dist\SpotifyLyricsOverlay.Wpf.exe"
Copy-Item -Force "USER_GUIDE.txt" "dist\USER_GUIDE.txt"
Copy-Item -Force "USER_GUIDE.txt" "bin\Release\net10.0-windows\win-x64\publish-lite\USER_GUIDE.txt"
Remove-Item -Force "bin\Release\net10.0-windows\win-x64\publish-lite\SpotifyLyricsOverlay.Wpf.pdb" -ErrorAction SilentlyContinue
Compress-Archive -Force -Path "dist\SpotifyLyricsOverlay.Wpf.exe","dist\USER_GUIDE.txt" -DestinationPath "dist\SpotifyLyricsOverlay-Windows-x64.zip"
Compress-Archive -Force -Path "bin\Release\net10.0-windows\win-x64\publish-lite\*" -DestinationPath "dist\SpotifyLyricsOverlay-Windows-x64-lite.zip"

$iscc = Get-Command iscc.exe -ErrorAction SilentlyContinue
$defaultIscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if ($iscc) {
    & $iscc.Source "installer\SpotifyLyricsOverlay.iss"
    & $iscc.Source "installer\SpotifyLyricsOverlay-Lite.iss"
} elseif (Test-Path $defaultIscc) {
    & $defaultIscc "installer\SpotifyLyricsOverlay.iss"
    & $defaultIscc "installer\SpotifyLyricsOverlay-Lite.iss"
} else {
    Write-Host "Inno Setup not found. Zip package was created; install Inno Setup and rerun this script to build the setup exe."
}
