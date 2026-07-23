# XISOSharp.Core

A .NET class library for creating, extracting, listing, and rewriting Xbox ISO (XISO) disc images.

C# port of [extract-xiso](https://github.com/XboxDev/extract-xiso) v2.7.1.

## License

MIT

## Installation

```
dotnet add package XISOSharp
```

## API

### Reading XISO images

```csharp
using XISOSharp;

// Extract all files from an XISO image to a directory
XisoReader.DecodeXiso("game.iso", "output_dir", ExtractMode.Extract, out _);

// List all files in an XISO image
XisoReader.DecodeXiso("game.iso", null, ExtractMode.List, out _);

// Rewrite (optimize) an XISO image
XisoReader.DecodeXiso("game.iso", null, ExtractMode.Rewrite, out _);
```

### Creating XISO images

```csharp
using XISOSharp;

// Create an XISO image from a directory
XisoWriter.CreateXiso("source_dir", "output_dir", null, null, out _, "game.iso", null);
```

### Cancellation support

```csharp
var cts = new CancellationTokenSource();
XisoWriter.CreateXiso("source_dir", "output_dir", null, null, out _, "game.iso", null, cts.Token);
XisoReader.DecodeXiso("game.iso", "output_dir", ExtractMode.Extract, out _, llCompat: false, cts.Token);
```

### Async APIs

```csharp
var (result, outPath) = await XisoReader.DecodeXisoAsync("game.iso", "output_dir", ExtractMode.Extract);
var (result, outPath) = await XisoWriter.CreateXisoAsync("source_dir", "output_dir", null, null, "game.iso", null);
```

### Logging

```csharp
// Redirect output to custom TextWriters
Logger.Out = new StreamWriter(logFilePath);
Logger.Error = new StringWriter();

Logger.Quiet = true;           // Suppress non-error output
Logger.RealQuiet = true;       // Suppress all output
Logger.RemoveSystemUpdate = true; // Skip $SystemUpdate folder
Logger.MediaEnable = false;    // Disable .xbe media patching
```

### Types

| Type | Description |
|---|---|
| `ExtractMode` | Enum: `Extract`, `List`, `Rewrite`, `GenerateAvl` |
| `ExtractError` | Error codes |
| `ExtractErrorException` | Exception thrown on extraction errors |
| `ProgressCallback` | Delegate for progress reporting |
| `CreateList` | Descriptor for creation source |
| `DirEntry` | On-disk directory entry |
| `AvlNode` | AVL tree node for filename indexing |
| `AvlTree` | AVL balanced tree with insert, fetch, and traversal methods |
