<#
.SYNOPSIS
    Builds a bootable WinPE image (ISO or USB drive) that starts FreePartition automatically.

.DESCRIPTION
    Lets you modify disks "offline" (system disk included): boot the PC from the USB drive / ISO
    and FreePartition opens on its own, with no Windows running on the disks.

    Requirements (see tools\winpe\README.md):
      * Windows 10/11, run from an elevated (Administrator) PowerShell
      * Windows ADK  +  "Windows PE add-on for the ADK"   (both are installers, not a single file)
      * .NET SDK 10  (to publish the app as self-contained)

.PARAMETER Iso
    Path of the ISO to create. Default: .\out\FreePartition-WinPE.iso

.PARAMETER Usb
    Drive letter of a USB drive (example: E:). The drive is FORMATTED and its content is lost.
    When omitted, only the ISO is produced.

.PARAMETER WorkDir
    Temporary working folder. Default: C:\FPWinPE\work (deleted and recreated on every run).
    Keep it on a short path outside the repository and outside synced/monitored folders.

.PARAMETER AdkRoot
    Folder of the ADK (the one that contains "Deployment Tools"). Detected automatically.

.PARAMETER SkipPublish
    Reuses the app already published in <WorkDir>\..\publish instead of running "dotnet publish".

.EXAMPLE
    .\build-winpe.ps1
    .\build-winpe.ps1 -Usb E:
#>
[CmdletBinding()]
param(
    [string] $Iso = (Join-Path $PSScriptRoot 'out\FreePartition-WinPE.iso'),
    [string] $Usb,
    [string] $WorkDir = 'C:\FPWinPE\work',
    [string] $AdkRoot,
    [switch] $SkipPublish
)

$ErrorActionPreference = 'Stop'
$repoRoot   = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$publishDir = Join-Path (Split-Path $WorkDir -Parent) 'publish'

function Write-Step([string] $text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

function Invoke-Native([string] $file, [string[]] $arguments) {
    & $file @arguments
    if ($LASTEXITCODE -ne 0) { throw "'$file' failed with exit code $LASTEXITCODE." }
}

# ---------------------------------------------------------------- 1. checks
Write-Step 'Checking the environment'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell (Run as administrator).'
}

if (-not $AdkRoot) {
    $kits = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -ErrorAction SilentlyContinue).KitsRoot10
    if (-not $kits) { $kits = "${env:ProgramFiles(x86)}\Windows Kits\10\" }
    $AdkRoot = Join-Path $kits 'Assessment and Deployment Kit'
}

$envBat  = Join-Path $AdkRoot 'Deployment Tools\DandISetEnv.bat'
$ocsRoot = Join-Path $AdkRoot 'Windows Preinstallation Environment\amd64\WinPE_OCs'

# Use the ADK's own DISM (same one copype uses), not the older one in System32.
$env:PATH = (Join-Path $AdkRoot 'Deployment Tools\amd64\DISM') + ';' + $env:PATH

if (-not (Test-Path $envBat)) {
    throw "Windows ADK not found in '$AdkRoot'. Install the 'Windows ADK' (Deployment Tools) first. See tools\winpe\README.md."
}
if (-not (Test-Path $ocsRoot)) {
    throw "'Windows PE add-on for the ADK' not found ('$ocsRoot'). Install it after the ADK. See tools\winpe\README.md."
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK not found. Install the .NET SDK 10.'
}

if ($Usb) {
    $Usb = $Usb.TrimEnd('\').ToUpperInvariant()
    if ($Usb -notmatch '^[A-Z]:$') { throw "Invalid -Usb value '$Usb'. Use a drive letter such as E:." }
    $volume = Get-Volume -DriveLetter $Usb.Substring(0, 1) -ErrorAction Stop
    if ($volume.DriveType -ne 'Removable') {
        throw "Drive $Usb is not a removable (USB) drive ($($volume.DriveType)). Refusing to format it."
    }
    Write-Host "Drive $Usb [$($volume.FileSystemLabel)] will be ERASED and formatted." -ForegroundColor Yellow
    if ((Read-Host "Type the drive letter ($Usb) to confirm") -ne $Usb) { throw 'Cancelled.' }
}

# ---------------------------------------------------------------- 2. publish
if ($SkipPublish -and (Test-Path (Join-Path $publishDir 'FreePartition.exe'))) {
    Write-Step "Reusing the published app in $publishDir"
}
else {
    Write-Step 'Publishing FreePartition (self-contained, win-x64)'
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    # WinPE has no .NET installed: the runtime must travel with the app.
    Invoke-Native 'dotnet' @(
        'publish', (Join-Path $repoRoot 'src\FreePartition.Wpf\FreePartition.Wpf.csproj'),
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=None', '-o', $publishDir)
}

# ---------------------------------------------------------------- 3. copype
Write-Step 'Creating the WinPE base (copype)'
if (Test-Path $WorkDir) {
    # A leftover mount (failed run) would block the deletion.
    & dism.exe /Cleanup-Wim | Out-Null
    & dism.exe /Cleanup-Mountpoints | Out-Null
    Remove-Item $WorkDir -Recurse -Force
}
New-Item -ItemType Directory -Force (Split-Path $WorkDir -Parent) | Out-Null

# copype only works inside the "Deployment and Imaging Tools Environment".
Invoke-Native 'cmd.exe' @('/c', "`"$envBat`" && copype amd64 `"$WorkDir`"")

$bootWim  = Join-Path $WorkDir 'media\sources\boot.wim'
$mountDir = Join-Path $WorkDir 'mount'
$mounted  = $false

# copype mounts boot.wim read-only and unmounts it again. If that unmount cannot delete the mount folder
# (a scan or another program holds files), it leaves leftovers behind and DISM refuses to mount ("not empty").
function Test-MountDirEmpty { -not (Get-ChildItem $mountDir -Force -ErrorAction SilentlyContinue | Select-Object -First 1) }
if (-not (Test-MountDirEmpty)) {
    Write-Warning "copype left files in '$mountDir'. Cleaning up..."
    & dism.exe /Cleanup-Wim | Out-Null
    & dism.exe /Cleanup-Mountpoints | Out-Null
    & takeown.exe /F $mountDir /R /D Y 2>&1 | Out-Null
    & icacls.exe $mountDir /grant 'Administrators:(F)' /T /C /Q 2>&1 | Out-Null
    Get-ChildItem $mountDir -Force -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    if (-not (Test-MountDirEmpty)) {
        throw "Could not empty '$mountDir'. Add C:\FPWinPE to the Defender exclusions (or pause real-time protection), close Explorer windows, reboot if needed and run the script again."
    }
}

try {
    # ------------------------------------------------------------ 4. mount + packages
    Write-Step 'Mounting boot.wim'
    Invoke-Native 'dism.exe' @('/Mount-Image', "/ImageFile:$bootWim", '/Index:1', "/MountDir:$mountDir")
    $mounted = $true

    # Order matters: some packages depend on the previous ones.
    $packages = @(
        'WinPE-WMI',            # System.Management / WMI queries
        'WinPE-NetFX',          # base of the PowerShell packages
        'WinPE-Scripting',
        'WinPE-PowerShell',     # the app runs its disk operations through powershell.exe
        'WinPE-StorageWMI',     # MSFT_Disk / MSFT_Partition (Get-Partition, Resize-Partition ...)
        'WinPE-DismCmdlets',
        'WinPE-FontSupport-WinRE'   # fonts for the WPF interface
    )
    foreach ($name in $packages) {
        Write-Step "Adding package $name"
        $cab = Join-Path $ocsRoot "$name.cab"
        if (-not (Test-Path $cab)) {
            Write-Warning "$name.cab not found in this ADK version. Skipped."
            continue
        }
        Invoke-Native 'dism.exe' @('/Add-Package', "/Image:$mountDir", "/PackagePath:$cab")
        $lang = Join-Path $ocsRoot "en-us\$name`_en-us.cab"
        if (Test-Path $lang) {
            Invoke-Native 'dism.exe' @('/Add-Package', "/Image:$mountDir", "/PackagePath:$lang")
        }
    }

    # WinPE runs from a RAM disk: give it more room for the app and the temporary files.
    Invoke-Native 'dism.exe' @('/Set-ScratchSpace:512', "/Image:$mountDir")

    # ------------------------------------------------------------ 5. app + autostart
    Write-Step 'Copying FreePartition into the image'
    $appDir = Join-Path $mountDir 'FreePartition'
    New-Item -ItemType Directory -Force $appDir | Out-Null
    Copy-Item (Join-Path $publishDir '*') $appDir -Recurse -Force

    Write-Step 'Configuring the automatic start (startnet.cmd)'
    $startnet = Join-Path $mountDir 'Windows\System32\startnet.cmd'
    $lines = @(
        '@echo off',
        'wpeinit',
        'rem The single-file app extracts its native libraries on first run: use the RAM disk.',
        'mkdir X:\Temp',
        'set DOTNET_BUNDLE_EXTRACT_BASE_DIR=X:\Temp',
        'echo Starting FreePartition...',
        'X:\FreePartition\FreePartition.exe',
        'echo.',
        'echo FreePartition closed. Type "wpeutil reboot" to restart or "wpeutil shutdown" to power off.'
    )
    Set-Content -Path $startnet -Value $lines -Encoding ASCII
}
catch {
    if ($mounted) { & dism.exe /Unmount-Image "/MountDir:$mountDir" /Discard | Out-Null }
    throw
}

# ---------------------------------------------------------------- 6. commit
Write-Step 'Saving boot.wim'
for ($attempt = 1; $attempt -le 4; $attempt++) {
    & dism.exe /Unmount-Image "/MountDir:$mountDir" /Commit
    if ($LASTEXITCODE -eq 0) { break }
    if ($attempt -eq 4) {
        throw "Could not unmount '$mountDir'. Close Explorer windows / antivirus scans on that folder and run the script again."
    }
    # Typically an antivirus scan (Defender) still holds files of the mounted image.
    Write-Warning "Unmount failed (files in use?). Retrying in 15 s ($attempt/4)..."
    Start-Sleep -Seconds 15
}

# ---------------------------------------------------------------- 7. media
Write-Step "Creating the ISO: $Iso"
New-Item -ItemType Directory -Force (Split-Path $Iso -Parent) | Out-Null
if (Test-Path $Iso) { Remove-Item $Iso -Force }
Invoke-Native 'cmd.exe' @('/c', "`"$envBat`" && MakeWinPEMedia /ISO `"$WorkDir`" `"$Iso`"")

if ($Usb) {
    Write-Step "Writing the bootable USB drive: $Usb"
    # /F = does not ask again (already confirmed above). Formats the drive.
    Invoke-Native 'cmd.exe' @('/c', "`"$envBat`" && MakeWinPEMedia /UFD /F `"$WorkDir`" $Usb")
}

Write-Host "`nDone." -ForegroundColor Green
Write-Host "ISO: $Iso"
if ($Usb) { Write-Host "USB: $Usb (boot the PC from it)" }
