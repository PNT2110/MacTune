<#
.SYNOPSIS
    Builds a self-contained release of MacTune into .\dist and zips it.

.DESCRIPTION
    The result runs on any 64-bit Windows 10/11 without installing .NET.
    Output:  dist\MacTune\            (folder to run or install from)
             dist\MacTune-<version>-win-x64.zip   (upload this to GitHub Releases)
#>
param([string]$Version = '1.0.0')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSCommandPath
$out = Join-Path $root 'dist\MacTune'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET 8 SDK not found. Install it with:  winget install Microsoft.DotNet.SDK.8'
}

Remove-Item (Join-Path $root 'dist') -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root 'MacTune.csproj') -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

Copy-Item (Join-Path $root 'install.ps1'), (Join-Path $root 'LICENSE'), (Join-Path $root 'THIRD-PARTY-NOTICES.md'), (Join-Path $root 'README.md') $out -Force

$zip = Join-Path $root "dist\MacTune-$Version-win-x64.zip"
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -Force
Write-Host "Release ready: $zip" -ForegroundColor Green
