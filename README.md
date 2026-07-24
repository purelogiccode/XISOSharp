# XISOSharp

A **pure C#** implementation of [extract-xiso](https://github.com/XboxDev/extract-xiso) v2.7.1 — the tool for creating, extracting, listing, and rewriting Xbox ISO (XISO) disc images.

This project is a **direct rewrite** of the original C codebase into idiomatic, managed C#. No native dependencies, no P/Invoke — just .NET.

## Projects

| Project | Description |
|---|---|
| [XISOSharp.Core](XISOSharp.Core/) | Core class library with the complete XISO read/write engine |
| [XISOSharp.Cli](XISOSharp.Cli/) | CLI tool compatible with the original extract-xiso |
| [XISOSharp.Tests](XISOSharp.Tests/) | Unit tests for the core library |
| [XISOSharpTester](XISOSharpTester/) | WPF GUI for batch regression testing against the C tool |

## Features

- **Create** XISO images from a directory
- **Extract** XISO contents to a directory
- **List** files inside an XISO
- **Tree** — recursive file listing with sizes and totals
- **Rewrite** an XISO to optimize its filesystem layout
- **Info** — display volume metadata and directory entry details
- **Copy-out** — extract individual files or directories without full unpack
- **Hash** — compute MD5 or SHA-256 hashes of files within an XISO
- Supports **GLOBAL**, **XGD2**, **XGD3**, and **XGD1** disc formats
- Automatic `.xbe` media-enable patching
- Async APIs for non-blocking I/O
- Strong-named assembly
- Targets .NET 8, .NET 9, and .NET 10

## NuGet Package

The core library is available as the **`XISOSharp`** NuGet package.

### Install

```
dotnet add package XISOSharp
```

Or via the NuGet Package Manager:

```
Install-Package XISOSharp
```

### Package Manager UI

Search for `XISOSharp` in the NuGet Package Manager in Visual Studio and install it.

## Usage

### Extract an XISO

```csharp
using XISOSharp;

int result = XisoReader.Extract("game.iso", "output_directory");
```

### List contents of an XISO

```csharp
using XISOSharp;

int result = XisoReader.List("game.iso");
```

### Create an XISO from a directory

```csharp
using XISOSharp;

int result = XisoWriter.CreateXiso("source_directory", "output.iso");
```

### Rewrite an XISO (optimize layout)

```csharp
using XISOSharp;

int result = XisoReader.Rewrite("game.iso", outputDirectory: null, deleteOriginal: false);
```

### Async create

```csharp
using XISOSharp;

var (result, outputPath) = await XisoWriter.CreateXisoAsync("source_directory", "output.iso");
```

## Build

Open `CSharp_XISOSharp.sln` in Visual Studio or run:

```
dotnet build
```

## Requirements

- .NET 8 SDK or later

## License

MIT
