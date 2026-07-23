# XISOSharpTester

A WPF desktop application for regression testing the XISOSharp C# implementation against the original C extract-xiso tool. Runs batch comparisons across multiple XISO images and reports pass/fail status with SHA-256 hash verification.

## Features

- **Batch test** multiple XISO files at once
- **Verify** — compares XISO header verification between C# and the C tool
- **List** — compares file listing output
- **Extract** — extracts files with both tools and compares SHA-256 hashes of every file
- **Rewrite** — rewrites with both tools and compares output ISO hashes
- **Round-trip** — creates XISO from extracted files and verifies the output
- **PDF export** — exports detailed test results to PDF via QuestPDF
- **Side-by-side comparison** with the original `extract-xiso.exe` (included)

## Building

Open `CSharp_XISOSharp.sln` in Visual Studio, or run:

```
dotnet build
```

The tester automatically detects `extract-xiso.exe` from the output directory. If the exe is not present, comparison tests against the native tool are skipped and only standalone C# library tests run.

## License

MIT
