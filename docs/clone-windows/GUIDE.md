# Moving Windows to another disk: step-by-step guide

**English** · [Español](GUIDE.es.md) · [Português (Brasil)](GUIDE.pt-BR.md)

This guide is for anyone who wants to move an installed Windows to a new disk (for example, from an old SSD or hard
disk to an NVMe M.2 drive) with FreePartition, without reinstalling anything. You do not need to know how disks work:
follow the steps in order and, if something goes wrong, look up the message in [Problems and solutions](#7-problems-and-solutions).

> **Your old disk is only read.** If anything goes wrong, reconnect it and the computer starts as before.
> The **new disk is erased** completely.

How to type a command: press **Windows + X**, choose **Terminal (Admin)** (or **Windows PowerShell (Admin)** /
**Command Prompt (Admin)**), click **Yes**, type the command and press **Enter**.

## Contents

1. [Before you start](#1-before-you-start)
2. [Make the USB drive](#2-make-the-usb-drive)
3. [Start the computer from the USB drive](#3-start-the-computer-from-the-usb-drive)
4. [Copy Windows](#4-copy-windows)
5. [First start from the new disk](#5-first-start-from-the-new-disk)
6. [After the move](#6-after-the-move)
7. [Problems and solutions](#7-problems-and-solutions)
8. [The log: what to send when asking for help](#8-the-log-what-to-send-when-asking-for-help)

## 1. Before you start

Do these checks in your current Windows, before touching the USB drive.

**1.1 Back up** what you cannot lose (documents, photos) to another disk or the cloud.

**1.2 Find out if the computer uses UEFI or BIOS.** Press **Windows + R**, type `msinfo32` and press **Enter**.
Look at the line **BIOS Mode**:

- **UEFI**: you will start the USB drive in UEFI mode.
- **Legacy**: you will start the USB drive in BIOS (legacy) mode.

Write it down; you will need it in step 3.

**1.3 Turn off Fast Startup.** With Fast Startup, "Shut down" does not really shut Windows down: it saves a half-finished
session on the disk, and a copy of it can start with errors. Run (as administrator):

```
powercfg /h off
```

This turns off hibernation and Fast Startup. You can turn them back on after the move (step 6.4).

**1.4 Check the size of the new disk.** The copy keeps the exact size of the Windows partition (C:). The new disk must
hold C: plus about 1 GB (boot and recovery partitions). FreePartition tells you if it does not fit; see
[The destination disk is too small](#the-destination-disk-is-too-small).

**1.5 BitLocker / Device encryption** (*not tested*). If C: is encrypted, WinPE cannot read it. Check:

```
manage-bde -status C:
```

If **Protection Status** is **Protection On** or **Conversion Status** is not **Fully Decrypted**, save your
recovery key and decrypt the drive before the move:

```
manage-bde -protectors -get C:
manage-bde -off C:
```

Decrypting takes a while. Repeat `manage-bde -status C:` until it says **Fully Decrypted**. You can turn BitLocker on
again after the move.

**1.6 Shut down completely.** When you are ready, shut down with this command (it never uses Fast Startup):

```
shutdown /s /t 0
```

## 2. Make the USB drive

You need a USB drive of **4 GB or more**. **Everything on it is erased.**

1. Download the WinPE image:
   Open the **[latest release](https://github.com/rfiori/FreePartition/releases/latest)** and, under **Assets**,
   download **`FreePartition-WinPE.iso`**. FreePartition opens the same page when you click **Clone Windows** in a
   normal Windows.
2. Write it to the USB drive with **one** of these programs:
   - **[Rufus](https://rufus.ie)** (recommended):
     1. Plug in the USB drive and open Rufus.
     2. **Device**: choose the USB drive (check the size and name: it will be erased).
     3. **Boot selection**: click **SELECT** and choose `FreePartition-WinPE.iso`.
     4. **Partition scheme**: **MBR**. **Target system**: **BIOS or UEFI**.
     5. **File system**: **FAT32**.
     6. Click **START**. If Rufus asks how to write the image, keep the recommended option. Wait until it says **READY**.
   - **[Ventoy](https://www.ventoy.net)**: install Ventoy on the USB drive once, then just copy the ISO file to it.
     When it starts, choose `FreePartition-WinPE.iso` in the Ventoy menu.

## 3. Start the computer from the USB drive

1. **Connect the new disk** (inside the computer, or in a USB enclosure).
2. Plug in the USB drive and turn the computer on.
3. Right away, press the **boot menu** key several times. It depends on the manufacturer:

   | Manufacturer | Boot menu key |
   | --- | --- |
   | Dell, Lenovo, Acer, Toshiba | F12 |
   | HP | F9 (or Esc, then F9) |
   | ASUS | F8 (desktops) or Esc (notebooks) |
   | MSI | F11 |
   | Gigabyte | F12 |
   | ASRock | F11 |
   | Samsung, Sony | Esc / F10 |

   If none works, look up "boot menu key" plus your model, or enter the BIOS/UEFI setup (usually **Del** or **F2**) and
   put the USB drive first in the boot order.
4. Choose the USB drive in the mode you wrote down in step 1.2:
   - **UEFI**: the entry that starts with **UEFI:** (for example *UEFI: SanDisk*).
   - **Legacy**: the entry **without** "UEFI".
5. Wait. WinPE starts (a black Command Prompt window appears) and **FreePartition opens by itself**. It can take a
   minute.

## 4. Copy Windows

1. In FreePartition, click **Clone Windows** on the toolbar (or **Tools > Clone Windows to another disk...**).
2. **Windows to copy**: choose your Windows. The letter in WinPE is usually **not C:**; check the size and the disk.
3. **Copy to**: choose the **new** disk. Check the number, the model and the size: **this disk will be erased**.
4. Read the summary below the lists. If a red box appears, see [Problems and solutions](#7-problems-and-solutions).
5. Tick **I understand that the destination disk will be erased** and click **Clone Windows**.
6. Wait. Each step shows **OK**, **Skipped** or **Failed**. Copying Windows takes from a few minutes to over an hour,
   depending on the size and the speed of the disks. **Do not turn the computer off.**

| Status | Meaning |
| --- | --- |
| **OK** | The step worked. |
| **Skipped** | The step was not needed (for example, the old disk has no recovery partition) or could not be done but is not essential. Read the warning at the end. |
| **Failed** | The copy stopped. The message says why. The old disk was not changed; fix the cause and run the clone again. |

At the end, a message says that Windows was copied, sometimes with **warnings**. Write them down (or take a picture)
and look them up in [Problems and solutions](#7-problems-and-solutions).

**Before restarting, save the log**: in WinPE it is kept in memory and lost when the computer restarts. See
[step 8](#8-the-log-what-to-send-when-asking-for-help).

## 5. First start from the new disk

1. Close FreePartition. In the black window, type `wpeutil shutdown` and press **Enter** to turn the computer off.
2. **Disconnect the old disk** for this first start (or, at least, choose the new disk in the boot menu). With both
   disks connected, the computer may start the old Windows, or the new one may get a different drive letter.
3. Remove the USB drive and turn the computer on. If it does not start, use the boot menu (step 3) and choose
   **Windows Boot Manager** of the new disk.
4. Windows starts as before: same programs, files and settings.
5. Check that it really started from the new disk: press **Windows + X**, choose **Disk Management** and see that
   **C:** is on the new disk.

## 6. After the move

### 6.1 Check the recovery partition

Run as administrator:

```
reagentc /info
```

If **Windows RE status** is **Enabled**, it is fine. If it is **Disabled**, run:

```
reagentc /enable
```

To test it: **Start > Power**, hold **Shift** and click **Restart**. The blue *Choose an option* screen must appear
(click **Continue** to go back to Windows). If `reagentc /enable` fails, see
[The recovery partition was not registered](#the-recovery-partition-was-copied-but-could-not-be-registered).

### 6.2 Use the extra space of the new disk

The Windows partition keeps its old size; the rest of the new disk is unallocated (free).

If the free space comes **right after C:**, just select C: in FreePartition, click **Resize partition**, make it fill
the free space and click **Apply**.

If the **recovery partition sits between C: and the free space** (the usual case), it has to be moved to the end
first (*not tested*):

1. Turn off the recovery for a moment (as administrator): `reagentc /disable`
2. In FreePartition, select the recovery partition and click **Show partition** (it needs a letter to be moved).
3. Click **Move partition** and move it to the end of the disk.
4. Select C:, click **Resize partition** and make it fill the free space.
5. Click **Apply** and wait.
6. Select the recovery partition again and click **Hide partition** (removes the letter), then **Apply**.
7. Turn the recovery back on: `reagentc /enable`, then check it with `reagentc /info` (step 6.1).

### 6.3 Reconnect the old disk (optional)

Use the new disk for a few days. When you are sure everything is fine, reconnect the old disk if you want to reuse it,
and only then format it. Until you format it, it is your backup.

### 6.4 Turn back on what you turned off

- Hibernation and Fast Startup (if you want them): `powercfg /h on`
- BitLocker (if you used it): **Settings > Privacy & security > Device encryption**, or
  **Control Panel > BitLocker Drive Encryption**.

## 7. Problems and solutions

### The recovery partition was copied, but could not be registered

**Message:** *The recovery partition was copied, but it could not be registered in the copied Windows
(reagentc.exe ...). After starting from the new disk, open a Command Prompt as administrator and run: reagentc /enable*

**Older versions showed:** *The recovery partition was not copied: The term 'reagentc.exe' is not recognized...*,
with the recovery step **Skipped**.

**Cause:** the files were copied, but the copied Windows was not told where its recovery partition is (WinPE has no
`reagentc.exe`). Windows works normally; only *Reset this PC* and the repair options are off.

**Solution:** after starting from the new disk, run as administrator:

```
reagentc /enable
reagentc /info
```

If `reagentc /enable` says the Windows RE image was not found, give the recovery partition a temporary letter and point
Windows to it:

```
diskpart
list disk
select disk 0
list partition
select partition 4
assign letter=R
exit
```

Replace `0` with the number of the new disk and `4` with the **Recovery** partition (about 500 MB to 1 GB). Then:

```
reagentc /setreimage /path R:\Recovery\WindowsRE /target C:\Windows
reagentc /enable
```

And remove the letter:

```
diskpart
select disk 0
select partition 4
remove letter=R
exit
```

Test it with **Shift + Restart** (step 6.1).

### The recovery partition was not copied

**Message:** *The recovery partition was not copied: ... Windows starts without it; the "Reset this PC" and repair
options will not work until it is recreated.*

**Cause:** creating or copying the recovery partition failed. The rest of the move is fine.

**Solution:** Windows works. If you need *Reset this PC*, keep the old disk (it still has the recovery partition) or
create recovery media: **Control Panel > Recovery > Create a recovery drive**.

### bcdboot could not write the boot files

**Message:** *bcdboot could not write the boot files (...). The boot files of the source were copied and its boot store
was repointed to the new disk instead.*

**Cause:** the `bcdboot` of WinPE refused the copied Windows. This happens with old Windows versions (such as Windows 7)
copied from a recent WinPE.

**Solution:** nothing to do: FreePartition used the old disk's boot files instead. Test the start from the new disk
(step 5). If it does not start, see [The computer does not start from the new disk](#the-computer-does-not-start-from-the-new-disk).

### The computer does not start from the new disk

**Symptoms:** *No bootable device*, *Operating system not found*, a black screen, or the computer goes straight to the
BIOS setup.

Check, in this order:

1. **Mode:** a Windows that was **UEFI** (step 1.2) needs the computer in UEFI mode; **Legacy** needs BIOS/CSM mode.
   Look in the BIOS setup for *Boot Mode*, *CSM* or *Legacy support*.
2. **Boot order:** in the boot menu, choose **Windows Boot Manager** (UEFI) or the new disk's name (BIOS). If there are
   two *Windows Boot Manager* entries, try the one with the new disk's name.
3. **Rewrite the boot files.** Start from the USB drive again, close FreePartition and, in the black window, type:

   ```
   diskpart
   list disk
   select disk 1
   list partition
   select partition 1
   assign letter=S
   list volume
   exit
   ```

   - `select disk 1`: the number of the **new** disk.
   - `select partition 1`: the **System** partition (about 100 MB on UEFI, *System Reserved* on BIOS).
   - In `list volume`, note the letter of the copied Windows (the large NTFS volume of the new disk, for example **E**).

   Then, for **UEFI**:

   ```
   bcdboot E:\Windows /s S: /f UEFI
   ```

   For **BIOS (Legacy)**, also mark the partition as active:

   ```
   diskpart
   select disk 1
   select partition 1
   active
   exit
   bcdboot E:\Windows /s S: /f BIOS
   ```

   Type `wpeutil reboot` to restart. To open FreePartition again without restarting, type `X:\FreePartition\FreePartition.exe`.
4. If nothing works, reconnect the old disk: it was not changed and starts as before. Then run the clone again.

### The new Windows shows disk errors, a disk check, or an old session

**Cause:** Windows was not shut down completely (Fast Startup or hibernation was on).

**Solution:** start from the old disk, do steps 1.3 and 1.6 (turn off Fast Startup, shut down with `shutdown /s /t 0`)
and run the clone again.

### Windows does not appear in "Windows to copy"

**Message:** *No installed Windows was found. A Windows partition needs a drive letter to be found...*

**Cause:** in WinPE, the Windows partition got no drive letter, or it is encrypted (BitLocker).

**Solution:** in the main window, select the Windows partition (the large NTFS one), click **Show partition** (gives
it a letter) and **Apply**, click **Refresh** and open **Clone Windows** again. If it is encrypted, see
step 1.5.

### There is only one disk / the new disk does not appear

**Message:** *There is only one disk. Connect the destination disk, then refresh the disks.*

- Check the cable or the USB enclosure, then click **Refresh**.
- **NVMe disk invisible in WinPE** (*not tested*): many notebooks (Intel 11th generation and newer, Dell, HP, Lenovo)
  use **Intel RST / VMD**, and WinPE has no driver for it. In the BIOS setup, change **SATA Operation** (or *Storage
  mode*) from **RAID** / **Intel RST** to **AHCI**, or turn off **VMD Controller**. Do it **only while you use the
  USB drive** and **change it back before starting Windows**: otherwise Windows stops with
  *INACCESSIBLE_BOOT_DEVICE*.

### The destination disk is too small

**Message:** *Disk N is too small: the copy needs X and the disk has Y. Shrink the Windows partition in the main window
first (Resize), apply it, then open this window again.*

**Solution:** first free space on C: (delete or move files to another disk). Then, in FreePartition, select C:,
click **Resize partition**, choose a size smaller than the new disk minus about 2 GB, and click **Apply**. Open **Clone Windows** again.

### There are pending operations

**Message:** *There are pending operations. Apply or undo them first...*

**Solution:** in the main window, click **Apply** (to do them) or **Undo** until the list is empty.

### The USB drive does not appear in the boot menu, or it does not start

- Write it again with Rufus using the options of step 2 (MBR, BIOS or UEFI, FAT32).
- Try another USB port (on desktops, a port on the back of the computer).
- In the BIOS setup, turn on **USB boot** and turn off **Fast Boot**.
- **Ventoy with Secure Boot:** the first time, Ventoy shows a blue *Verification failed* screen; follow Ventoy's
  *Enroll key* steps, or use Rufus instead.

### FreePartition does not open in WinPE

In the black window, type:

```
X:\FreePartition\FreePartition.exe
```

If it still does not open, take a picture of the message and report it (step 8).

### It asks for the BitLocker recovery key (*not tested*)

Type the key you saved in step 1.5 (it is also at <https://account.microsoft.com/devices/recoverykey> if your
Microsoft account is used on the computer).

### Canceled or interrupted

**Message:** *Canceled. The destination disk was erased and is incomplete; run the clone again before using it.*

**Solution:** the old disk was not changed. Run the clone again.

## 8. The log: what to send when asking for help

Everything the clone does is written in the window's log box and in `operations.log`.

- **In WinPE the log is lost when the computer restarts.** Before restarting, copy it to the USB drive or another disk.
  In the black window (replace `D:` with the letter of the USB drive):

  ```
  copy X:\FreePartition\Log\operations.log D:\
  ```

  Or click inside the log box, press **Ctrl + A**, **Ctrl + C** and paste it into a file on another disk.
- In a normal Windows: **Tools > Open log folder**.

When you [open an issue](https://github.com/rfiori/FreePartition/issues), include the log, the messages or a picture of
the screen, the source and destination disks (model, size, SATA or NVMe) and the mode (UEFI or Legacy).
