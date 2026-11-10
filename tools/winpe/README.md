# FreePartition on WinPE (offline disk editing via USB)

`build-winpe.ps1` builds a bootable WinPE image that opens FreePartition automatically.
Booting from it lets you resize/move/format the system disk and recover files with no Windows running on it.

> Experimental: WPF and the .NET runtime are not officially supported in WinPE. Test in a VM first.

## What you need

| Item | Where | Notes |
|---|---|---|
| **Windows ADK** | <https://learn.microsoft.com/windows-hardware/get-started/adk-install> | Installer (`adksetup.exe`). Tick only **Deployment Tools**. |
| **Windows PE add-on for the ADK** | same page | Second installer (`adkwinpesetup.exe`). Install it after the ADK. |
| **.NET SDK 10** | <https://dotnet.microsoft.com/download> | Used to publish the app self-contained. |
| A **USB drive** (4 GB+) | | Optional. It is erased. Without it you only get an ISO. |

## Usage

Open PowerShell **as administrator**:

```powershell
# ISO only (test it in a VM: Hyper-V, VirtualBox...)
.\tools\winpe\build-winpe.ps1

# ISO + bootable USB drive (E: is formatted!)
.\tools\winpe\build-winpe.ps1 -Usb E:
```

Output: `tools\winpe\out\FreePartition-WinPE.iso`. Boot the PC from the USB drive (boot menu, usually F12/F8/Esc).

## Troubleshooting

### DISM fails with `0xc1420117` (cannot unmount) or `0xc1420114` (mount folder not empty)

Windows Security's **Controlled folder access** (ransomware protection) can block `Dism.exe` from
deleting the files of the mounted image. The notification reads *"Protected memory access blocked"*,
app `Dism.exe`, blocked by *Controlled folder access*. A Defender folder exclusion does **not** fix it:
you have to allow the app.

Run in an elevated PowerShell (this allows both the ADK DISM and the Windows one):

```powershell
Add-MpPreference -ControlledFolderAccessAllowedApplications "C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\AMD64\DISM\Dism.exe"
Add-MpPreference -ControlledFolderAccessAllowedApplications "C:\Windows\System32\Dism.exe"
```

Or use the UI: **Windows Security > Virus & threat protection > Manage ransomware protection >
Allow an app through Controlled folder access**.

Alternative: turn the protection off only while you build, then back on.

```powershell
Set-MpPreference -EnableControlledFolderAccess Disabled
Set-MpPreference -EnableControlledFolderAccess Enabled
```

Then clean up the failed run and start again:

```powershell
dism /Cleanup-Wim
dism /Cleanup-Mountpoints
Remove-Item C:\FPWinPE -Recurse -Force
.\tools\winpe\build-winpe.ps1
```

If `Remove-Item` reports files in use, reboot and repeat. If Windows blocks another program
(for example `cmd.exe` or `MakeWinPEMedia`), allow it the same way.

## Known limits

- The image must boot in UEFI or BIOS depending on the PC; `MakeWinPEMedia` supports both.
- The app was written for a normal Windows: drive letters differ in WinPE (`X:` is the RAM disk).
- Disk operations call `powershell.exe`; the script adds the WinPE PowerShell/Storage packages, but support is partial.
