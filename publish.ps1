param([switch]$SingleFile)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
& (Join-Path $PSScriptRoot 'setup-ffmpeg.ps1')
if ($SingleFile) {
    dotnet publish BiliLocal.Desktop/BiliLocal.Desktop.csproj -c Release -r win-x64 --self-contained true -o publish/single-win-x64 -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Single-file publishing failed.' }
    $destination = Join-Path $PSScriptRoot 'dist'
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -LiteralPath 'publish/single-win-x64/BiliLocal.Desktop.exe' -Destination (Join-Path $destination 'BiliLocal.exe')
    $checksum = (Get-FileHash -LiteralPath (Join-Path $destination 'BiliLocal.exe') -Algorithm SHA256).Hash
    "$checksum  BiliLocal.exe" | Set-Content -LiteralPath (Join-Path $destination 'BiliLocal.exe.sha256') -Encoding ascii
    Write-Host 'Ready: dist\BiliLocal.exe (self-contained, includes FFmpeg)' -ForegroundColor Green
    return
}
dotnet publish BiliLocal.Desktop/BiliLocal.Desktop.csproj -c Release -r win-x64 --self-contained true -o publish/win-x64 -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Publishing failed.' }
Write-Host 'Ready: publish\win-x64\BiliLocal.Desktop.exe' -ForegroundColor Green
