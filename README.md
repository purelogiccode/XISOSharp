# CSharp-ExtractXiso

A C# port of the [extract-xiso](https://github.com/XboxDev/extract-xiso) tool v2.7.1 by in `<in@fishtank.com>`.

Extract-XISO is a command-line tool for creating, extracting, listing, and rewriting Xbox ISO (XISO) disc images.

## License

MIT

## Projects

| Project | Description |
|---|---|
| [ExtractXiso.Core](ExtractXiso.Core/) | Class library — available as the [ExtractXiso](https://www.nuget.org/) NuGet package |
| [ExtractXiso.Cli](ExtractXiso.Cli/) | CLI tool (`extract-xiso`) |
| [ExtractXiso.Tests](ExtractXiso.Tests/) | Unit tests (xUnit) |

## Building

```bash
dotnet build CSharp_ExtractXiso.sln --configuration Release
```

## Testing

```bash
dotnet test ExtractXiso.Tests/ExtractXiso.Tests.csproj --configuration Release
```
