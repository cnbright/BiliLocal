param([switch]$Force)
$ErrorActionPreference = 'Stop'

$expectedExeHash = '74DB6C184A03DBA2BDFE23E1A1F41CF5A8385BC1DE6A7A1B26DB1DC541ABEF93'
$expectedArchiveHash = '467CDE100A47ED4B03A897988AEB4A296890C1E2B2D2864204657D002BC5FB90'
$downloadUrl = 'https://github.com/GyanD/codexffmpeg/releases/download/8.0.1/ffmpeg-8.0.1-full_build.zip'
$destination = Join-Path $PSScriptRoot 'vendor/ffmpeg/win-x64/ffmpeg.exe'

if (!$Force -and (Test-Path -LiteralPath $destination) -and
    (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq $expectedExeHash) {
    Write-Host 'FFmpeg 8.0.1 is ready (SHA256 verified).'
    return
}

$cacheDirectory = Join-Path $PSScriptRoot 'artifacts/dependencies'
New-Item -ItemType Directory -Path $cacheDirectory -Force | Out-Null
$archive = Join-Path $cacheDirectory 'ffmpeg-8.0.1-full_build.zip'
if (!(Test-Path -LiteralPath $archive) -or
    (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedArchiveHash) {
    Write-Host 'Downloading pinned FFmpeg 8.0.1 from GyanD/codexffmpeg...'
    $previousProgressPreference = $ProgressPreference
    try {
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $downloadUrl -OutFile "$archive.partial" -UseBasicParsing
    } finally {
        $ProgressPreference = $previousProgressPreference
    }
    if ((Get-FileHash -LiteralPath "$archive.partial" -Algorithm SHA256).Hash -ne $expectedArchiveHash) {
        throw 'FFmpeg archive SHA256 mismatch. No executable was installed.'
    }
    Move-Item -LiteralPath "$archive.partial" -Destination $archive -Force
}

$expandedDirectory = Join-Path $cacheDirectory 'ffmpeg-8.0.1'
Expand-Archive -LiteralPath $archive -DestinationPath $expandedDirectory -Force
$executable = Join-Path $expandedDirectory 'ffmpeg-8.0.1-full_build/bin/ffmpeg.exe'
if ((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -ne $expectedExeHash) {
    throw 'FFmpeg executable SHA256 mismatch. No executable was installed.'
}
New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
Copy-Item -LiteralPath $executable -Destination $destination -Force
Write-Host 'FFmpeg 8.0.1 is ready (archive and executable SHA256 verified).'
