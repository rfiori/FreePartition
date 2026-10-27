# FreePartition — notes for Claude

Read [README.md](README.md) first: it has the features, requirements, build commands, project
structure, safety guarantees, roadmap and known limitations. Do not duplicate any of that here.

## Language

**US English for everything in the repository**: identifiers, comments, UI labels and messages,
log messages, tests, docs and commit messages. Never add Portuguese text anywhere.
(The user chats in Portuguese; the code stays in English.)

## Non-obvious decisions — do not "fix" these

- `DiskService` uses direct WMI queries instead of `ASSOCIATORS OF`, which fails with
  "Not found" on some systems. Keep the direct queries.
- The **Safety** section of the README is a list of invariants, not a description. Never weaken
  one (read-only volume access, recovery to another drive only, nothing runs before **Apply**,
  simulation on by default, Windows drive protected, PowerShell built only from validated values).
- A failure to write the operations log must never bring down the operation itself.
- `ICommandExecutor` exists so tests never touch a real disk — keep the executor behind it.

## Code conventions

- Modern C#: `Span<T>`, `ArrayPool<byte>`, `[LibraryImport]` (Core has `AllowUnsafeBlocks`),
  `RandomAccess`, `SafeFileHandle`, records. Nullable enabled, no new warnings.
- Never concatenate free user text into a PowerShell script — validate against a closed set first.

## Current status

The code builds and the tests pass. Still unverified: the MFT scan has never run on a real
volume (test it on a VHD first, see the README), and the WPF UI has never been opened — the app
requires elevation, so it was not launched during development.
