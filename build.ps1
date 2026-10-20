# Builds the Audio extension and its installer.
#   powershell -ExecutionPolicy Bypass -File .\build.ps1
# Output: dist\FormatWheel-Audio-Setup-<version>.exe

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$project = '.\src\FormatWheel.Audio\FormatWheel.Audio.csproj'
[xml]$proj = Get-Content $project -Encoding UTF8
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { $version = '0.0.0' }
Write-Host "==> Format Wheel Audio extension $version" -ForegroundColor Cyan

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK not found. Install it: winget install Microsoft.DotNet.SDK.10'
}

Remove-Item .\publish -Recurse -Force -ErrorAction SilentlyContinue
# the DLL and its deps, but NOT the SDK (the app already ships it)
dotnet publish $project -c Release -o .\publish
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Remove-Item .\publish\FormatWheel.Sdk.dll -ErrorAction SilentlyContinue

function Find-Iscc {
    @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
      "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
}
$iscc = Find-Iscc
if (-not $iscc) {
    winget install --id JRSoftware.InnoSetup -e --accept-source-agreements --accept-package-agreements
    $iscc = Find-Iscc
    if (-not $iscc) { throw 'Install Inno Setup 6 manually: https://jrsoftware.org/isdl.php' }
}

& $iscc "/DAppVersion=$version" "/DPublishDir=$PSScriptRoot\publish" .\installer\FormatWheel.Audio.iss
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
Write-Host "==> Done: dist\FormatWheel-Audio-Setup-$version.exe" -ForegroundColor Green
