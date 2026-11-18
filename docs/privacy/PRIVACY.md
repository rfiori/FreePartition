# Privacy Policy

**English** · [Español](PRIVACY.es.md) · [Português (Brasil)](PRIVACY.pt-BR.md)

**FreePartition** — last updated: November 11, 2026

FreePartition is a free, open-source disk partition manager for Windows. This policy explains
what the app does, and does not do, with your information.

## Summary

FreePartition does not collect, transmit or sell any personal data. It works entirely on your
computer. Its only network feature is an optional check for a new version on GitHub (see
[Network access](#network-access)).

## Information we collect

None. The app has no accounts, no analytics, no telemetry, no advertising and no crash reporting.
Nothing is sent to the author or to any third party.

## Information the app reads on your computer

To do its job, the app reads information locally, and only while you use it:

- **Disk, partition and volume details** (sizes, labels, drive letters, file systems) through
  Windows Management Instrumentation (WMI).
- **Disk health data** (S.M.A.R.T. attributes, NVMe health log, temperature, power-on time).
- **NTFS metadata and file contents** on a volume you choose to scan for deleted files. Volumes
  are opened read-only. File names, paths and previews are shown only in the app window.

This information stays on your machine and is never uploaded.

## Information the app stores

The app writes a single local file, the operations log:

```
Log\operations.log   (next to FreePartition.exe; %LocalAppData%\FreePartition if not writable)
```

It records the actions queued and applied in the app (for example, a format or resize, with
the target drive letter, size and time) and any errors. You can open or delete it at any time
from the Tools menu or in File Explorer. It is never sent anywhere.

Files you choose to recover are copied only to the destination folder you select, on another drive.

## Network access

The only network connection FreePartition makes is the **update check**. When the app starts (and
when you choose **Help > Check for updates**), it asks `api.github.com` for the latest release of
the project. The request carries no personal data, only what any web request carries: your IP
address and the app name `FreePartition-Updater`. GitHub may log it under its own
[privacy statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement).

- You can turn the startup check off in **Help > Check for updates on startup**. The choice is
  saved in `settings.json` next to `FreePartition.exe` (or in `%LocalAppData%\FreePartition`).
- Nothing is downloaded or installed until you click **Update now**. The download comes from the
  project's GitHub releases and is checked against its SHA-256 before it is installed.
- The check is not made in WinPE.

The other external action is opening a link or folder (such as the About window links or the log
location) with your default Windows handler when you click it. Those sites are governed by their
own privacy policies.

## Administrator rights

The app requires administrator rights (UAC) to access raw volumes and run partition commands.
These rights are used only for the disk operations you request.

## Children's privacy

The app collects no data from anyone, including children.

## Changes to this policy

If the app's behavior changes in a way that affects privacy, this document will be updated and
the date above revised. Previous versions are available in the repository history.

## Contact

Questions or concerns: open an issue at
[github.com/rfiori/FreePartition/issues](https://github.com/rfiori/FreePartition/issues).

## Disclaimer

FreePartition is provided under the MIT License, without warranty. See [LICENSE](../../LICENSE).
It is an independent project and is not affiliated with EaseUS.
