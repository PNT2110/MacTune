<#
.SYNOPSIS
    Installs MacTune to "C:\Program Files\MacTune" and starts it.

.DESCRIPTION
    Run this from an extracted release zip (the folder that contains MacTune.exe),
    or from a source checkout after running build-release.ps1.

    MacTune needs administrator rights (hardware sensors, power settings, fans). It is installed
    to Program Files on purpose: that folder can only be changed by an administrator, so nothing
    can swap the program behind your back and get admin rights through MacTune's no-prompt shortcut.

    Uninstall:  .\install.ps1 -Uninstall
#>
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$target = Join-Path $env:ProgramFiles 'MacTune'

# --- self-elevate ---
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($Uninstall) { $argList += '-Uninstall' }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
    return
}

function Stop-MacTune {
    Get-Process MacTune -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host 'Closing running MacTune...'
        $_.CloseMainWindow() | Out-Null
        if (-not $_.WaitForExit(3000)) { $_ | Stop-Process -Force }
    }
}

if ($Uninstall) {
    Stop-MacTune
    foreach ($task in 'MacTune', 'MacTune-Launch') { schtasks /Delete /F /TN $task 2>$null | Out-Null }
    Remove-Item (Join-Path ([Environment]::GetFolderPath('Desktop')) 'MacTune.lnk') -ErrorAction SilentlyContinue
    Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'MacTune removed. Settings are kept in C:\ProgramData\MacTune (delete that folder to remove them too).'
    Write-Host 'Power plans named "MacTune - ..." can be deleted in Control Panel > Power Options.'
    Read-Host 'Press Enter to close'
    return
}

# --- locate the files to install ---
$here = Split-Path -Parent $PSCommandPath
$source = @($here, (Join-Path $here 'dist\MacTune')) | Where-Object { Test-Path (Join-Path $_ 'MacTune.exe') } | Select-Object -First 1
if (-not $source) {
    Write-Host 'MacTune.exe not found next to this script.' -ForegroundColor Red
    Write-Host 'Download a release zip, or run .\build-release.ps1 first when installing from source.'
    Read-Host 'Press Enter to close'
    return
}

$model = (Get-CimInstance Win32_ComputerSystem)
if ($model.Manufacturer -notmatch 'Apple') {
    Write-Host "Warning: this PC is '$($model.Manufacturer) $($model.Model)', not an Apple Mac." -ForegroundColor Yellow
    Write-Host 'MacTune is made for Intel Macs running Windows through Boot Camp. Mac-specific features will not work.'
    if ((Read-Host 'Install anyway? (y/N)') -notmatch '^[yY]') { return }
}

Stop-MacTune
Write-Host "Installing to $target ..."
New-Item -ItemType Directory -Force $target | Out-Null
if ([IO.Path]::GetFullPath($source).TrimEnd('\') -ine [IO.Path]::GetFullPath($target).TrimEnd('\')) {
    Copy-Item (Join-Path $source '*') $target -Recurse -Force
}
$installedScript = Join-Path $target 'install.ps1'
if ([IO.Path]::GetFullPath($PSCommandPath) -ine [IO.Path]::GetFullPath($installedScript)) {
    Copy-Item $PSCommandPath $installedScript -Force
}

Write-Host 'Starting MacTune (it creates a desktop shortcut that opens without a UAC prompt).'
Start-Process (Join-Path $target 'MacTune.exe')
Write-Host 'Done.' -ForegroundColor Green
Start-Sleep -Seconds 2
