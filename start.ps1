$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$desktopExe = Join-Path $PSScriptRoot 'publish\win-x64\BiliLocal.Desktop.exe'
if (-not (Test-Path -LiteralPath $desktopExe)) {
    & (Join-Path $PSScriptRoot 'publish.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
}
# Launch the interactive desktop application.
Start-Process -FilePath $desktopExe -WorkingDirectory $PSScriptRoot
