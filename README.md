<h1 align="center">FreePartition</h1>

<p align="center">
  <b>Free, open-source disk management for Windows.</b><br>
  Manage partitions, check disk health and recover deleted NTFS files, all in one app.
</p>

<p align="center">
  Create, resize, move, merge, split and format partitions with a visual disk map and a
  review-then-apply queue, so nothing touches your disk until you confirm.<br>
  Read-only SMART and NVMe health reports, and a deleted-file scanner that recovers to another
  drive, never the one you are scanning.<br>
  Built with C# / .NET and WPF, inspired by the best disk managers available.
</p>

<p align="center">
  <img alt="Platform" src="https://img.shields.io/badge/platform-Windows-0078D6">
  <img alt="Target" src="https://img.shields.io/badge/.NET-10.0--windows-512BD4">
  <img alt="UI" src="https://img.shields.io/badge/UI-WPF-blue">
  <a href="https://github.com/rfiori/FreePartition/releases"><img alt="Release" src="https://img.shields.io/github/v/release/rfiori/FreePartition?include_prereleases"></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/github/license/rfiori/FreePartition"></a>
  <a href="https://github.com/rfiori/FreePartition/releases"><img alt="Downloads" src="https://img.shields.io/github/downloads/rfiori/FreePartition/total"></a>
</p>

> [!WARNING]
> **Alpha software that touches disks.** The MFT scan has not yet been validated on real
> hardware, and the partition operations run real PowerShell cmdlets. Try it on a **test VHD
> or a virtual machine first**, and keep backups. See [Safety](#safety) and
> [Known limitations](#known-limitations).

---

## Table of contents

- [What it does](#what-it-does)
- [Screenshots](#screenshots)
- [Requirements](#requirements)
- [Quick start](#quick-start)
- [Releases](#releases)
- [How to try it safely (test VHD)](#how-to-try-it-safely-test-vhd)
- [How recovery works](#how-recovery-works)
- [Safety](#safety)
- [Project structure](#project-structure)
- [Roadmap](#roadmap)
- [Known limitations](#known-limitations)
- [Contributing](#contributing)
- [License](#license)

## What it does

| Area | Feature |
| --- | --- |
| **Disks and partitions** | Lists disks, partitions and volumes through WMI, grouped by disk with a graphical map, and tells SSDs from HDDs. A read-only **Properties** window shows a usage bar (used/free, and on a drive with Windows the estimated share of the operating system, programs and users) plus partition, NTFS volume and disk details. |
| **Deleted-file recovery** | MFT scan of NTFS volumes: original path, recovery chance (cross-checked against `$Bitmap`) and recovery to another drive, browsing results as a folder tree (with check boxes) and recreating the original folders. Filters by name, type, size and modified date, and a read-only preview (image, text or hex dump) of the selected file. Shows a TRIM warning when the drive is on an SSD. |
| **Partition operations** | Initialize a disk (MBR/GPT), create, delete, resize/extend (drag the partition bar), merge, split and move partitions, format (NTFS, FAT32, exFAT), change label, change drive letter, set active (MBR), hide/show, check for errors (`chkdsk`, scan or repair), convert FAT32 ↔ NTFS keeping the files. Also available from the right-click menu. |
| **Disk health** | Read-only report per disk (Tools → Disk health): overall level, temperature, power-on time and wear, with the ATA S.M.A.R.T. attribute table or the NVMe health log. |
| **Pending-operations queue** | Nothing runs until you click **Apply**: *Undo / Undo all / Apply / Simulation* (simulation is on by default), plus a log in `%LocalAppData%\FreePartition\operations.log` that you can show in the app or open from the Tools menu. |

Not implemented yet: secure wipe, file carving, lost-partition recovery, clone partition, MBR ↔ GPT conversion, Windows migration and boot repair. See the [Roadmap](#roadmap).

## Screenshots

<img src="docs/screenshots/main-window.webp" alt="FreePartition main window" width="100%">
<img src="docs/screenshots/create_partition.webp" alt="FreePartition Create Partition" width="32%">
<img src="docs/screenshots/move_partition.webp" alt="FreePartition Move Partition" width="32%">
<img src="docs/screenshots/resize_partition.webp" alt="FreePartition Resize Partition" width="32%">
<img src="docs/screenshots/split_partiion.webp" alt="FreePartition Split Partition" width="32%">
<img src="docs/screenshots/recover_delete_files.webp" alt="FreePartition Recover Delete files" width="32%">
<img src="docs/screenshots/disk_health.webp" alt="FreePartition Disk Health" width="32%">

## Requirements

- Windows 10 / 11 (the project uses WMI, P/Invoke, PowerShell and WPF — Windows only).
- [.NET SDK 10](https://dotnet.microsoft.com/download) (or change the target, see below).
- Administrator rights to run the app (raw volume access and partition cmdlets).

## Quick start

```bash
git clone https://github.com/rfiori/FreePartition.git
cd FreePartition
dotnet build
dotnet test
dotnet run --project src/FreePartition.Wpf
```

The app requests elevation (UAC) on startup. If you run it from Visual Studio, open
Visual Studio **as administrator**.

<details>
<summary><b>Using a different .NET version</b></summary>

The default target is `net10.0-windows`, set in a single place. Edit `TargetFramework` in
[`Directory.Build.props`](Directory.Build.props), for example to `net8.0-windows`.

</details>

<details>
<summary><b>If your tooling does not support the <code>.slnx</code> solution format</b></summary>

`.slnx` needs Visual Studio 2022 17.13+ or a recent `dotnet`. To generate a classic `.sln`:

```bash
dotnet new sln --format sln -n FreePartition
dotnet sln add src/FreePartition.Core src/FreePartition.Wpf tests/FreePartition.Tests
```

</details>

## Releases

Pushing a tag such as `v0.0.5` runs [the release workflow](.github/workflows/release.yml): it runs
the tests (a failure blocks the release), publishes two builds and attaches them to a GitHub
release with a `SHA256SUMS.txt` file.

- `FreePartition-<version>-win-x64.zip` — small, needs the .NET Desktop Runtime installed.
- `FreePartition-<version>-win-x64-self-contained.zip` — larger, runs without installing .NET.

Verify a download with `Get-FileHash <file>.zip -Algorithm SHA256` and compare it to `SHA256SUMS.txt`.
The builds are not code-signed yet, so Windows SmartScreen may warn on first run.

## How to try it safely (test VHD)

Run in an **elevated** PowerShell. This creates a throwaway 2 GB NTFS disk you can scan,
format and delete files on without risking real data:

```powershell
$vhd = "$env:USERPROFILE\FreePartition-test.vhdx"
New-VHD -Path $vhd -SizeBytes 2GB -Dynamic |
  Mount-VHD -Passthru |
  Initialize-Disk -PartitionStyle GPT -PassThru |
  New-Partition -UseMaximumSize -AssignDriveLetter |
  Format-Volume -FileSystem NTFS -NewFileSystemLabel FreePartition_TEST -Confirm:$false
```

Copy some files to the new drive, delete them, then scan that volume in FreePartition.
When you are done: `Dismount-VHD -Path $vhd` and delete the file.

> `New-VHD` comes from the Hyper-V PowerShell module. Without it, create the VHD from
> **Disk Management → Action → Create VHD**.

## How recovery works

1. Reads the boot sector → cluster size and where the MFT starts.
2. Reads record 0 (`$MFT`) and follows its *data runs*, so a fragmented MFT works too.
3. For each record: applies the *fixups*, reads `$FILE_NAME` (name and parent folder) and
   `$DATA`. Records without the "in use" flag are deleted files.
4. Rebuilds the original path from the parent folders (in use or deleted).
5. Cross-checks the file's clusters against `$Bitmap`: clusters still free = data probably intact.
6. Recover = copy the clusters (or the content resident in the MFT) to a file on **another disk**.

## Safety

The guarantees the code is built around:

- The volume is opened **read-only** — nothing is written to the source disk during a scan.
- Recovery **refuses a destination folder on the same drive** as the file being recovered.
- Disk operations never run directly: they go into a queue and only run on **Apply**, after an
  explicit confirmation that lists the risk.
- **Simulation mode is checked by default** — it shows the commands without running anything.
- Formatting or changing the drive letter of the **Windows drive** is blocked.
- PowerShell scripts are generated only from validated values (drive letter, numeric size,
  file system from a closed list, label with restricted characters) — no command injection.
- **Move partition is the one exception to "nothing is written outside the PowerShell cmdlets"**, because Windows
  has no cmdlet that changes where a partition starts. It only runs after **Apply**, never on the Windows drive,
  only inside the partition's own space plus free space of the same disk (the table is checked first, and the drive
  letter must be exactly the selected partition), with the volume locked and unmounted. If the partition table
  cannot be updated after the copy, the data is copied back. **If the power fails or the computer is turned off
  while it runs, the data on that partition is corrupted** — the app says so before and when applying, and it
  cannot be canceled once the copy has started.
- The queue stops at the first failure; the remaining operations stay pending.
- **Undo** only works before applying. An applied operation is **not** reversible.
- Every event is logged to `%LocalAppData%\FreePartition\operations.log`.

## Project structure

```
FreePartition.slnx                     # the solution
Directory.Build.props                  # TargetFramework and shared options
src/FreePartition.Core/                # all logic, no UI, testable without a real disk
  Disks/       DiskService (WMI), DiskHealthService (S.M.A.R.T.), RawVolume (reads \\.\C:), Models,
               DriveLayout + SectorCopier + PhysicalDiskMover (move partition)
  Ntfs/        NtfsBootSector, MftParser, RunReader, DeletedFileScanner, FileRecoverer
  Operations/  DiskOperation (+ types), ConvertVolume (FAT32 ↔ NTFS), OperationQueue, PowerShellExecutor, OperationLog
  Util/        ByteFormatter
src/FreePartition.Wpf/                 # WPF UI (app.manifest: requireAdministrator)
tests/FreePartition.Tests/             # xUnit tests (synthetic MFT records, fake executor)
```

## Roadmap

**Basic**
- ✅ List disks, partitions and volumes
- ✅ Format (NTFS, FAT32, exFAT)
- ✅ Shrink and extend partition
- ✅ Change drive letter and label
- ✅ Create and delete partition
- ✅ Initialize disk (MBR/GPT)
- ✅ Set active partition, hide/show
- ✅ Disk health (S.M.A.R.T.)

**Intermediate**
- ✅ Pending-operations queue (Apply/Undo)
- ✅ Operations log
- ✅ Merge and split partitions
- ✅ Convert FAT32 ↔ NTFS without data loss (not yet run on a real disk; test on a VHD first)
- ✅ Check for errors (`chkdsk`; scan or repair)
- ⚪ Securely wipe a disk / 4K alignment for SSD

**Data recovery**
- ✅ Find deleted files (MFT scan, NTFS)
- ✅ Recovery chance estimate
- ✅ Always recover to another disk
- ✅ Filter by name
- ✅ Filters by type, size and date; preview before recovering (image, text or hex dump)
- ⚪ File carving by signature (JPEG, PNG, PDF, DOCX…) for FAT/exFAT or a damaged MFT
- ⚪ Recover lost partitions
- ✅ Explicit TRIM warning on SSD

**Advanced (high risk — needs heavy VM/VHD testing)**
- ✅ Move partition (same disk, into free space; verified on a VHD, not yet on a physical disk)
- ⚪ Clone partition
- ⚪ Convert MBR ↔ GPT without data loss
- ⚪ Migrate Windows to another SSD/HDD
- ⚪ Rebuild MBR/BCD, bootable media (WinPE)

## Known limitations

- **NTFS only.** FAT/exFAT need another routine (directory entries with `0xE5`) and/or *file carving*.
- Files with an `$ATTRIBUTE_LIST` (very fragmented, or with many attributes) may appear incomplete.
- Compressed or encrypted (EFS) files are marked "Unavailable".
- Alternate data streams (ADS) are ignored.
- The sequence number of the parent reference is not validated — paths of reused folders may be wrong.
- On SSDs with TRIM, deleted data has usually already been discarded by the controller.
- Move partition: same disk only, MBR (without extended partitions) and GPT, 4 KB-aligned partitions, and the
  partition needs a drive letter. You drag the partition inside the free space touching it (before and/or after), in steps of 1 MiB.
  Boot-sector "hidden sectors" are not rewritten, so the app asks for confirmation when the partition is marked
  Boot, Active, Page File or Crash Dump, and refuses the Windows drive outright.
- Convert FAT32 ↔ NTFS: FAT32 → NTFS runs Windows' in-place `convert.exe`. NTFS → FAT32 has no in-place tool, so the
  files are copied to another drive, verified (file count and bytes), the volume is formatted and the files are copied
  back and verified again; the copy is deleted only then. It needs free space on another drive, refuses volumes over
  32 GB (the Windows FAT32 format limit) and files of 4 GB or more, and FAT32 loses NTFS permissions, compression,
  encryption, alternate data streams and hard links. Never on the Windows drive. If the power fails after the format
  starts, the files are in `\FreePartition_Convert_<drive>` on the staging drive.
- The partition "Type" column only shows Primary / EFI / Other: WMI does not distinguish logical partitions.
- The UI is code-behind (no MVVM yet).

## Contributing

Issues and pull requests are welcome.

- Everything in the repository is written in **US English**: identifiers, comments, UI text,
  log messages, tests, docs and commit messages.
- Keep the logic in `FreePartition.Core` and testable without a real disk; the UI only calls Core.
- `dotnet build` must produce no new warnings, and `dotnet test` must pass.
- Any new binary-format parser needs unit tests with hand-built records, including invalid cases
  (see `MftParserTests` for the pattern).
- Never test destructive operations on a real disk — use a VHD or a VM.

## License

MIT — see [LICENSE](LICENSE).

Privacy policy: [English](docs/privacy/PRIVACY.md) · [Español](docs/privacy/PRIVACY.es.md) ·
[Português (Brasil)](docs/privacy/PRIVACY.pt-BR.md). FreePartition collects no data and makes no network connections.

FreePartition is an independent project and is not affiliated with EaseUS.
