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

---
## Requirements

- Windows 10 / 11
- [.NET SDK 10](https://dotnet.microsoft.com/download)
- Administrator rights to run the app (raw volume access and partition)

## Quick start fo devs

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
---

## Table of contents

- [What it does](#what-it-does)
- [Screenshots](#screenshots)
- [Releases](#releases)
- [How to try it safely (test VHD)](#how-to-try-it-safely-test-vhd)
- [How recovery works](#how-recovery-works)
- [Safety](#safety)
- [Roadmap](#roadmap)
- [Known limitations](#known-limitations)
- [Contributing](#contributing)
- [License](#license)

## What it does

Everything in the **Operations** panel (and the right-click menu, which mirrors it) and the **Tools** menu is listed below. Nothing that changes a disk runs until you click **Apply**.

### Overview

| Area | Feature |
| --- | --- |
| **Disks and partitions** | Lists disks, partitions and volumes through WMI, grouped by disk with a graphical map. Tells SSDs from HDDs, flags partitions that are not aligned to 4 KB and draws the hidden Microsoft Reserved (MSR) partition as *Reserved (MSR)* instead of unallocated space. |


### Operations

**Inspect** (read-only)

| Operation | What it does |
| --- | --- |
| **Explore partition** | Opens the selected partition in Windows Explorer. |
| **Properties** | Usage bar (used/free, and on a drive with Windows the estimated share of the operating system, programs and users) plus partition, NTFS volume and disk details. |
| **Verify wipe** | Reads a whole volume (also a RAW one) and checks that nothing is left after a wipe: counts the bytes that are not zero, shows where the first one is with a hex dump and names any NTFS / exFAT / FAT32 signature that survived. Verdict: all zeros, random-looking, residual data or inconclusive. |
| **Cluster map** | Draws a whole volume (also a RAW one) as a grid. Each square is classified as zero, data or unreadable from three 4 KB samples; clicking a square shows its byte range, its NTFS clusters and a hex dump. |
| **Recover files** | Looks for deleted files on the selected NTFS drive, or carves files by signature on FAT/exFAT and damaged NTFS (see below). |
| **Recover partitions** | Looks in the unallocated space of the selected disk for NTFS, FAT32 and exFAT partitions missing from the partition table (read-only) and queues the ones you check to be put back. Only the table is written, never the files (see below). |

**Create**

| Operation | What it does |
| --- | --- |
| **Initialize disk** | Initializes a blank disk as MBR or GPT. |
| **Create partition** | Creates a partition in the selected unallocated space, formatted as NTFS, FAT32 or exFAT, with a label and drive letter. |

**Size and position**

| Operation | What it does |
| --- | --- |
| **Resize partition** | Extends or shrinks the selected partition by dragging the partition bar. |
| **Move partition** | Moves the partition into free space on the same disk (data is lost if interrupted). |
| **Align partition** | Moves a partition that is not aligned to 4 KB to the nearest 1 MB boundary (data is lost if interrupted). |
| **Clone partition** | Copies the selected partition, sector by sector, into unallocated space on the same or another disk, and adds it to that disk's partition table. The original is only read and is not changed. |
| **Merge partitions** | Merges the selected partition into the one right before it; its files are kept in a folder. |
| **Split partition** | Splits the selected partition in two, using its free space, by dragging the bar. The files stay on the original drive. |

**Settings**

| Operation | What it does |
| --- | --- |
| **Change label** | Changes the volume label. |
| **Change letter** | Changes the drive letter. |
| **Convert FAT32 / NTFS** | Converts the selected partition between FAT32 and NTFS, keeping its files. |
| **Convert MBR / GPT** | Converts the selected disk between MBR and GPT, keeping every partition and its files: only the partition table is rewritten (see below). |
| **Set active** | Marks the selected MBR partition as active (bootable). |
| **Hide partition** | Removes the drive letter; the data stays untouched. |
| **Show partition** | Gives a drive letter to a hidden partition. |

**Maintenance**

| Operation | What it does |
| --- | --- |
| **Check for errors** | Checks the selected volume for file system errors with `chkdsk` (scan or repair). |

**Destructive**

| Operation | What it does |
| --- | --- |
| **Format partition** | Formats the partition as NTFS, FAT32 or exFAT. Erases all data on it. |
| **Delete partition** | Deletes the partition. Erases all data on it. |
| **Wipe free space** | Overwrites the free space of the selected drive so deleted files cannot be recovered. The files are kept. |
| **Wipe partition** | Overwrites every sector of the selected partition so nothing on it can be recovered. |
| **Wipe disk** | Deletes every partition of the selected disk and overwrites all of its sectors. |

### Tools

| Tool | What it does |
| --- | --- |
| **Recover deleted files** (Tools menu and toolbar) | MFT scan of NTFS volumes: original path, recovery chance (cross-checked against `$Bitmap`, and marked *Overwritten (zeros)* when the start, middle and end of the file are all zeros, as after a wipe; those files cannot be checked for recovery) and recovery to another drive. Results are a folder tree with check boxes that recreates the original folders, with filters by name, type, size and modified date and a read-only preview (image, text or hex dump) of the selected file. Shows a TRIM warning when the drive is on an SSD. |
| **Disk health (S.M.A.R.T.)** (Tools menu and toolbar) | Read-only report per disk: overall level, temperature, power-on time and wear, with the ATA S.M.A.R.T. attribute table or the NVMe health log. |

Not implemented yet: SSD firmware Secure Erase. See the [Roadmap](#roadmap).

## Screenshots

<img src="docs/screenshots/main-window.webp" alt="FreePartition main window" width="100%">

<table>
  <tr>
    <td><img src="docs/screenshots/create_partition.webp" alt="FreePartition Create Partition" width="100%"></td>
    <td><img src="docs/screenshots/move_partition.webp" alt="FreePartition Move Partition" width="100%"></td>
    <td><img src="docs/screenshots/resize_partition.webp" alt="FreePartition Resize Partition" width="100%"></td>
  </tr>
  <tr>
    <td><img src="docs/screenshots/split_partiion.webp" alt="FreePartition Split Partition" width="100%"></td>
    <td><img src="docs/screenshots/recover_delete_files.webp" alt="FreePartition Recover Delete files" width="100%"></td>
    <td><img src="docs/screenshots/disk_health.webp" alt="FreePartition Disk Health" width="100%"></td>
  </tr>
</table>

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

### File carving

The **Carve files by signature** option ignores the file system and walks the whole volume looking for the bytes each
format starts with, then works out where each file ends from its own structure. Supported: JPEG, PNG, GIF, BMP, WebP,
PSD/PSB, PDF, DOCX/XLSX/PPTX, ZIP, MP4, MOV, M4A, 3GP, HEIC, AVIF, WAV, AVI, 7z, SQLite and EXE/DLL.

It is on by default for FAT/exFAT drives, and the scan switches to it by itself when the NTFS MFT cannot be read.

- Files come back as `carved_00001.jpg`, grouped by type, without names, folders or dates.
- A fragmented file is cut at the first gap, so it may be incomplete or damaged.
- Files whose end has to be searched for (JPEG, PNG, GIF, PDF, ZIP) are skipped past 256 MiB; formats that state their
  own size go up to 16 GiB.
- It reads the entire drive, so it is much slower than the MFT scan (still read-only). **Cancel** lists what was found so far.

### Lost partitions

**Recover partitions** finds partitions whose entry is gone from the partition table (a deleted partition, a corrupted
table, a disk that shows as uninitialized):

1. Looks in the unallocated ranges of the disk for the boot sector of an NTFS, FAT32 or exFAT volume, and keeps only
   partitions that fit entirely in the free space.
2. A partition whose backup boot sector (at the end of the volume) also matches is **high confidence** and checked by
   default; one with only the first boot sector is **low confidence** and left unchecked.
3. **Add to queue** queues a *Restore partition* for each checked result. On **Apply** it re-checks the boot sector and
   that nothing else occupies the range, then writes the table entry directly (`New-Partition` would zero the start
   of the partition and destroy the boot sector). The volume is **not formatted**, so its files come back as they were.

- **Quick** looks where partitions usually start (start of each free range, every 1 MB, sector 63); **Deep** reads
  every sector of the free space, so it takes as long as copying that space.
- On a disk with no partition table, queue **Initialize disk** first, then the restores.
- Only the partition comes back, not its type or GUID: Windows gives it a new GPT GUID and a basic data type.

## Safety

The guarantees the code is built around:

- Volumes are opened **read-only** for scans and recovery; recovery **refuses a destination on the same drive**.
- Disk operations never run directly: they go into a queue and only run on **Apply**, after a confirmation that lists
  the risk. **Simulation mode is on by default** and only shows the commands. **Undo** works only before applying.
- The **Windows drive** is protected: no format, letter change, move, clone or wipe. *Wipe disk* and *Convert MBR / GPT*
  are also refused on any disk holding Windows or the app itself (checked by the queue, by the script and again right
  before writing, failing closed if the disk cannot be identified).
- PowerShell scripts are generated only from validated values (drive letter, numeric size, file system from a closed
  list, label with restricted characters) — no command injection.
- The few operations that write below the cmdlet level (**Move**, **Clone**, **Restore partition**, **Convert MBR / GPT**
  and the **wipes**) only run after **Apply**, check the partition table first and write only where they must: inside the
  partition's own locked volume, into free space of the disk, or the partition table sectors. Clone and Restore write the
  table entry last; Convert saves the old table and puts it back if writing fails.
- **Move partition cannot be canceled once the copy starts, and a power failure corrupts that partition** — the app says
  so before and when applying. On SSDs, wipes cannot reach the drive's spare cells and the app warns about it.
- The queue stops at the first failure; the remaining operations stay pending.
- Every event is logged to `%LocalAppData%\FreePartition\operations.log`.

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
- ✅ Detect partitions not aligned to 4 KB (warning on the disk map and in Properties)
- ✅ Align a partition to 1 MB (moves it to the nearest boundary; same risk as a move, not yet run on a VHD)
- ✅ Wipe a partition, a whole disk or a drive's free space (zeros, verified zeros or random data; not yet run on a VHD)

**Data recovery**
- ✅ Find deleted files (MFT scan, NTFS)
- ✅ Recovery chance estimate
- ✅ Always recover to another disk
- ✅ Filter by name
- ✅ Filters by type, size and date; preview before recovering (image, text or hex dump)
- ✅ File carving by signature (JPEG, PNG, PDF, DOCX…) for FAT/exFAT or a damaged MFT
- ✅ Recover lost partitions (NTFS, FAT32, exFAT by boot sector; not yet run on a VHD)
- ✅ Explicit TRIM warning on SSD

**Advanced (high risk — needs heavy VM/VHD testing)**
- ✅ Move partition (same disk, into free space; verified on a VHD, not yet on a physical disk)
- ✅ Clone partition (same or another disk, into unallocated space; not yet run on a VHD)
- ✅ Convert MBR ↔ GPT without data loss (not the disk Windows boots from; not yet run on a VHD)

## Known limitations

- **Names and folders need NTFS.** FAT/exFAT only have *file carving*; reading their directory entries is not implemented.
- **Lost partitions** are found by their first boot sector only: FAT12/16, ReFS, Linux and other file systems are not
  found, and logical partitions inside an MBR extended partition come back as primary ones.
- Files with an `$ATTRIBUTE_LIST` may appear incomplete; compressed or encrypted (EFS) files are "Unavailable";
  alternate data streams are ignored; parent sequence numbers are not validated, so paths of reused folders may be wrong.
- On SSDs with TRIM, deleted data has usually already been discarded by the controller.
- **Move / Align partition**: same disk only, MBR (without extended partitions) and GPT, destinations aligned to 4 KB, and
  the partition needs a drive letter. Align needs up to 1 MB of free space next to the partition. Boot-sector "hidden
  sectors" are not rewritten, so the app asks for confirmation on Boot, Active, Page File or Crash Dump partitions.
- **Convert FAT32 ↔ NTFS**: NTFS → FAT32 copies the files to another drive, formats and copies them back (needs free
  space there, refuses volumes over 32 GB and files of 4 GB or more; FAT32 loses permissions, compression, encryption,
  streams and hard links). If the power fails after the format starts, the files are in
  `\FreePartition_Convert_<drive>` on the staging drive.
- **Clone partition**: NTFS, FAT32 and exFAT only, to disks with the same sector size, into unallocated space at least as
  large as the partition (resize it afterwards). The copy keeps the volume serial number; on MBR it needs a free primary slot.
- **Convert MBR / GPT**: not the disk Windows boots from (use `mbr2gpt.exe`). MBR → GPT needs 34 sectors free at the start
  and 33 at the end and refuses dynamic disks; GPT → MBR needs at most four partitions, none past 2 TB. Logical partitions
  become ordinary ones, and drive letters may change (the app tries to restore them).
- Carving, lost-partition scan, Clone, Convert and the wipes have not been run on a real disk yet: test them on a VHD first.
- The partition "Type" column only shows Primary / EFI / Other, and the UI is code-behind (no MVVM yet).

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

FreePartition is an independent project and is not affiliated with any company.
