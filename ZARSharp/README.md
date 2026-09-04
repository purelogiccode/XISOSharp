# ZARSharp

Pure-C# port of the [ZArchive 0.1.2](https://github.com/unknownbrackets/ZArchive)
library: directory-tree archives with per-block zstd compression. No native
dependencies, BCL only; trimmable and AOT-compatible (`net8.0`/`net9.0`/`net10.0`).

## Layout

```csharp
// Pack a directory (compresses each 64 KiB block with zstd level 6 by default).
ZArchiveTool.Pack(@"C:\game", @"C:\game.zar");

// Extract it back.
ZArchiveTool.Extract(@"C:\game.zar", @"C:\game_out");
```

```csharp
// Low-level writer/reader with an explicit compressor choice.
using var output = File.Create("game.zar");
using var writer = new ZArchiveWriter(output); // default: ZstdCompressor level 6
writer.StartNewFile("readme.txt");
writer.AppendData("hello"u8);
writer.Finalize();

using var reader = ZArchiveReader.TryOpen("game.zar");
```

```csharp
// Standalone single-shot zstd frames (RFC 8878), levels 1-6.
var compressor = new ZstdCompressor(ZstdCompressionOptions.FromLevel(3));
byte[] frame = compressor.CompressBlock(data); // always a valid frame
byte[] back = ZstdCompressor.DecompressFrame(frame, maxSize: data.Length);
```

Blocks that do not compress smaller are stored raw (same rule as upstream
`StoreBlock`); the raw-only `ZarRawCompressor` stays available for
tests and benchmarks.

## Limits

- Single-shot 64 KiB blocks, levels 1-6 (fast/double-fast/greedy/lazy
  strategies; level 6 uses hash-chain lazy, not binary-tree lazy).
- No zstd dictionaries, no legacy frames, no multithreading, no streaming API.
- Decoder caps (configurable via `ZstdDecoderOptions`): 512 MiB window,
  512 MiB frame content. Output is valid interoperable zstd, not
  byte-identical to libzstd.

## License

MIT — see [LICENSE](../LICENSE).
