# What's New in 1.4.4 (since 1.4.3)

A failed or cancelled extraction no longer leaves a truncated destination file
behind. `ExtractFile`/`CopyOutFile` now delete the partial output they wrote, so a
disk that fills up mid-copy, a torn image, or a cancelled run cannot leave a file
that later looks complete to a resume probe, a drag-and-drop preparation, or a
batch script. Custom destination stores opt in through the new
`IFilesystem.DeleteFile` member; its default implementation keeps the old no-op
behavior, so existing implementations still compile and run unchanged.

The whole solution is now clean under JetBrains InspectCode - 0 errors and 0
warnings across all projects - and `dotnet build` reports 0 warnings. Dead
members uncovered by the inspection were removed from the Tester.

Targets remain `net8.0` / `net9.0` / `net10.0`. Full suite green on all three:
**1760 passed / 1 opt-in skip (1761) on net8.0/net9.0** and **1821 passed / 1
opt-in skip (1822) on net10.0**.

## Highlights

- **No partial files after a failed extraction**: truncated copies, mid-write
  I/O failures, and cancellation all remove the destination the call created.
  Cleanup is best-effort: a delete failure is swallowed so it never hides the
  extraction error.
- **Custom destinations can clean up too**: `IFilesystem.DeleteFile` defaults to
  a no-op, `LocalFilesystem` deletes from disk (missing path is a no-op), and
  `MemoryFilesystem` drops the stored snapshot.
- **Static-analysis clean**: JetBrains InspectCode reports 0 errors and 0
  warnings for the entire solution, and the regular build stays warning-free.

## Library

- `ExtractFile`/`CopyOutFile` track success and delete the partial destination in
  a `finally` block when the copy did not complete.
- `IFilesystem.DeleteFile(string path)` - new member with a default no-op body;
  called only for cleanup after a failed extraction.
- `LocalFilesystem.DeleteFile` resolves the path like every other member and
  treats a missing file as a no-op; `MemoryFilesystem.DeleteFile` removes the
  committed snapshot.
- `Logger.Out`/`Logger.Error` accept `null` (documented behavior) through
  `[AllowNull]` and store `TextWriter.Null`, so the getters never return `null`.
- Inspection-driven cleanup elsewhere: redundant default arguments, redundant
  casts/assignments, dead null guards, and unused members. No output, format, or
  on-disk changes.

## Tester

- Removed the unused `LogEntries` collection and `LogEntry` model (the UI binds
  `LogText`), plus `PerFileResult.FilePath` and the unused `TestProgress` fields.

## Tests

- `ExtractRobustnessTests` asserts no destination survives a truncated copy, a
  mid-write failure, or a custom-filesystem write failure.
- `XisoFilesystemTests` covers `DeleteFile` on `LocalFilesystem`, including the
  missing-file no-op.
- Suite: **net8.0/net9.0 - 1761 tests (1760 passed, 1 opt-in skip)**;
  **net10.0 - 1822 tests (1821 passed, 1 opt-in skip, adds the GUI tests)**.
