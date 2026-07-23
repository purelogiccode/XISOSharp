# CSharp_XISOSharp

A pure C# implementation of [extract-xiso](https://github.com/XboxDev/extract-xiso) v2.7.1, the tool for creating, extracting, listing, and rewriting Xbox ISO (XISO) disc images.

## Projects

| Project | Description |
|---|---|
| [XISOSharp.Core](XISOSharp.Core/README.md) | Core class library with the complete XISO read/write engine |
| [XISOSharp.Cli](XISOSharp.Cli/README.md) | CLI tool compatible with the original extract-xiso |
| [XISOSharp.Tests](XISOSharp.Tests/README.md) | Unit tests for the core library |
| [XISOSharpTester](XISOSharpTester/README.md) | WPF GUI for batch regression testing against the C tool |

## Build

Open `CSharp_XISOSharp.sln` in Visual Studio or run:

```
dotnet build
```

## License

MIT
