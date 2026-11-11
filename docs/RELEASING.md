# Publishing a release

For maintainers. Users only need the [Download](../README.md#download) section of the README.

## Version and tag

- Use `vMAJOR.MINOR.PATCH` for the tag, with a lowercase `v` and no fourth number (for example `v0.1.0`).
- A tag with a hyphen (for example `v0.1.0-beta.1`) is published as a **pre-release**.
- Before tagging, set `<Version>` in [`Directory.Build.props`](../Directory.Build.props) to the same number without the
  `v`. The About window shows it. The workflow uses the tag name as the version of the zip files.

## What the workflow does

Pushing a tag that starts with `v` runs [the release workflow](../.github/workflows/release.yml):

1. Runs the tests. A failure blocks the release.
2. Publishes two single-file builds for `win-x64`:
   - `FreePartition-<version>-win-x64.zip`: small, needs the .NET Desktop Runtime.
   - `FreePartition-<version>-win-x64-self-contained.zip`: larger, runs without installing .NET.
3. Writes `SHA256SUMS.txt` with the hash of both zips.
4. Creates the GitHub release (or updates it, if one already exists for the tag) with the zips and `SHA256SUMS.txt`,
   and generates the notes from the commits.

The workflow does **not** build the WinPE image: it needs the Windows ADK, which is not on the GitHub runner.

## Steps

1. Update `<Version>` in `Directory.Build.props`, commit and push.
2. Create and push the tag:

   ```bash
   git tag v0.1.0
   git push origin v0.1.0
   ```

3. Wait for the workflow to finish (**Actions** tab) and check that the release has the two zips and `SHA256SUMS.txt`.
4. Build the WinPE image on your machine, in PowerShell **as administrator** (needs the Windows ADK and the WinPE
   add-on, see [tools/winpe/README.md](../tools/winpe/README.md)):

   ```powershell
   .\tools\winpe\build-winpe.ps1
   ```

   The image is `tools\winpe\out\FreePartition-WinPE.iso`.
5. Attach the ISO to the release: **Releases**, open the release, **Edit**, drag the file to **Attach binaries** and
   save. A release file can be up to 2 GiB, so the image fits.
   - Keep the name `FreePartition-WinPE.iso`. The link
     `https://github.com/rfiori/FreePartition/releases/latest/download/FreePartition-WinPE.iso` then always points to the
     latest image.
   - Optional: put the hash of the ISO in the release notes. Do not upload a file called `SHA256SUMS.txt`, it would
     replace the one made by the workflow.

     ```powershell
     (Get-FileHash tools\winpe\out\FreePartition-WinPE.iso -Algorithm SHA256).Hash.ToLower()
     ```

6. Edit the release notes (the workflow fills them with the list of commits). Suggested text:

   ```markdown
   ## FreePartition 0.1.0

   First stable release. All operations were tested on real disks.

   **Highlights**
   - Recover deleted files (NTFS scan, carving by signature, preview and thumbnails)
   - Create, resize, move, merge, split, format and clone partitions
   - Clone Windows to another disk from WinPE, with boot files and recovery partition
   - Read-only S.M.A.R.T. and NVMe health reports

   **Downloads**
   - `FreePartition-0.1.0-win-x64-self-contained.zip`: runs without installing anything (recommended)
   - `FreePartition-0.1.0-win-x64.zip`: smaller, needs the .NET Desktop Runtime 10
   - `FreePartition-WinPE.iso`: bootable image to clone Windows (see the
     [step-by-step guide](https://github.com/rfiori/FreePartition/blob/main/docs/clone-windows/GUIDE.md))

   Verify the zips with `SHA256SUMS.txt`. The builds are not code-signed, so Windows SmartScreen may warn on first run.
   ```

## Checklist

- [ ] `<Version>` updated and the tests pass (`dotnet test`)
- [ ] Tag `vMAJOR.MINOR.PATCH` pushed and the workflow is green
- [ ] Two zips and `SHA256SUMS.txt` are on the release
- [ ] ISO built from the same commit and attached as `FreePartition-WinPE.iso`
- [ ] Release notes edited
