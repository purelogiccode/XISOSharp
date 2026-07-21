# ExtractXiso.Core

A .NET class library for creating, extracting, listing, and rewriting Xbox ISO (XISO) disc images.

C# port of [extract-xiso](https://github.com/XboxDev/extract-xiso) v2.7.1.

## License

MIT

## Installation

```
dotnet add package ExtractXiso
```

## API

### Reading XISO images

```csharp
using ExtractXiso;

// Extract all files from an XISO image to a directory
XisoReader.DecodeXiso("game.iso", "output_dir", ExtractMode.Extract, out _);

// List all files in an XISO image
XisoReader.DecodeXiso("game.iso", null, ExtractMode.List, out _);

// Rewrite (optimize) an XISO image
XisoReader.DecodeXiso("game.iso", null, ExtractMode.Rewrite, out _);
```

### Creating XISO images

```csharp
using ExtractXiso;

// Create an XISO image from a directory
XisoWriter.CreateXiso("source_dir", "output_dir", null, null, out _, "game.iso", null);
```

### Logging

```csharp
Logger.Quiet = true;           // Suppress non-error output
Logger.RealQuiet = true;       // Suppress all output
Logger.RemoveSystemUpdate = true; // Skip $SystemUpdate folder
Logger.MediaEnable = false;    // Disable .xbe media patching
```

### Types

| Type | Description |
|---|---|
| `ExtractMode` | Enum: `Extract`, `List`, `Rewrite` |
| `ExtractError` | Error codes |
| `ExtractErrorException` | Exception thrown on extraction errors |
| `ProgressCallback` | Delegate for progress reporting |
| `CreateList` | Descriptor for creation source |
| `DirEntry` | On-disk directory entry |
| `BoyerMoore` | Boyer-Moore pattern search (xbe patching) |
| `AvlTree` | AVL tree for filename indexing |
