# Resume Instructions — ZARSharp (ZArchive-0.1.2 → pure C# port)

> STATUS 2026-09-03 (later same day): **IMPLEMENTED.** `ZARSharp/` library,
> 22 tests in `XISOSharp.Tests/ZArchiveSharpTests.cs` (all green), both-way
> interop with `zarchive.exe` verified, README + TODO2 updated. FOLLOW-UP DONE:
> `rebuild` from `.zar` sidecar implemented in `XisoRedump` (+7 tests in
> `XisoZarRebuildTests.cs`, CLI + docs wired). Only known issue: 17 pre-existing
> `XisoChecksumTests` fail on machines without OS SHA3 support
> (`PlatformNotSupportedException`, unrelated to ZARSharp).
> Original research notes preserved below.
>
> Written at end of session: 2026-09-03. Only **research** was done — no ZARSharp code exists yet.
> Next session: implement the plan below. Everything needed is captured here so no re-exploration is required.

## Objective

1. Create a new class library project **`ZARSharp`** in solution `CSharp_XISOSharp.sln`, sources under
   `C:\Users\HomePC\Dropbox\source\repos\CSharp_XISOSharp\ZARSharp\`.
2. Fully convert the reference **`References\ZArchive-0.1.2`** (a **C/C++** library by Exzap — NOT Rust) to pure C#.
3. Standing user rule (same as the LZ4 port): **pure C#, ZERO external NuGet packages** ("Keep port, no K4os").
   This means zstd must be implemented in-repo too (see plan step 5).

## Key research findings (from reading all reference sources)

### Format constants & layout (zarchivecommon.h)
- `COMPRESSED_BLOCK_SIZE = 64 * 1024`, `ENTRIES_PER_OFFSETRECORD = 16`. **All integers big-endian on disk.**
- `CompressionOffsetRecord` = 40 bytes: `u64 baseOffset` + `u16 size[16]` (stores compressedSize − 1).
- `FileDirectoryEntry` = 16 bytes = 4 × u32 BE:
  - `nameOffsetAndTypeFlag`: MSB 0x80000000 = file, low 31 bits = name-table offset.
  - File: `fileOffsetLow`, `fileSizeLow`, `fileOffsetAndSizeHigh`
    (upper 16 bits = size extension, lower 16 bits = offset extension).
    `GetFileSize() = fileSizeLow | ((u64)(high & 0xFFFF0000) << 16)`;
    `GetFileOffset() = fileOffsetLow | ((u64)(high & 0xFFFF) << 32)`.
  - Directory: `nodeStartIndex`, `count`, `_reserved` — **same 3×u32 layout** as file record (serializer skips type check).
- `Footer` = 144 bytes, field order: six `OffsetInfo {u64 offset; u64 size}` in order
  **sectionCompressedData, sectionOffsetRecords, sectionNames, sectionFileTree, sectionMetaDirectory, sectionMetaData**;
  then `u8 integrityHash[32]`; then `u64 totalSize`; then `u32 version` = `0x61bf3a01`; then `u32 magic` = `0x169f52d6`
  (**magic/version at the END**). `IsWithinValidRange(fileSize) = (offset + size) <= fileSize`.
- Path helpers: `GetNextPathNode` (skips leading `/` or `\`, node = up to next slash), `SplitFilenameFromPath`
  (scan back to last slash), `CompareNodeNameBool` (case-fold A–Z only), `CompareNodeName`
  (on char mismatch returns `(int)(u8)c2 - (int)(u8)c1` — i.e. **ascending** sort; shorter string returns +1).
- Paths are **Windows-1252** encoded, case-insensitive. Per-file size limit 2^48−1.

### Writer (zarchivewriter.cpp)
- API: ctor with callbacks `NewOutputFile(partIndex)` + `WriteOutputData(data, len)`; `StartNewFile(path)`,
  `AppendData(data, size)`, `MakeDir(path, recursive=false)`, `Finalize()`.
- Node names deduped by **exact-case** `Dictionary<string,uint>`; tree of `PathNode {isFile, nameIndex, subnodes, fileOffset, fileSize, nodeStartIndex}`.
- `StoreBlock`: `ZSTD_compress(level 6)` each 64 KiB block; **if compressed size ≥ 64 KiB → store raw**.
  Offset-record size entry = `outputSize − 1` (u16; 65536 fits as 65535).
- `AppendData`: buffers into 64 KiB blocks; block-aligned input bypasses the buffer.
- `Finalize`: deactivate current file → pad write buffer to full block (zeros) → pad output to 8-byte alignment →
  write sections in order: **offsetRecords** (serialized in place, BE), **nameTable** (per name: 1-byte len prefix if
  < 0x80, else 2 bytes `[len&0x7F | 0x80, len>>7]`; names cut off at 0x7FFF chars), **fileTree** (BFS from root;
  root = index 0, `currentIndex` starts at 1; sort each dir's subnodes with `CompareNodeName` ascending;
  files get `nodeStartIndex = 0xFFFFFFFF`; root entry uses nameOffset `0x7FFFFFFF`; directories store
  `count` + `nodeStartIndex`), **meta sections** (both empty: offset = current, size 0), **footer**.
- Integrity hash: streaming **SHA-256 over every output byte** (`IncrementalHash` in C#), then the footer is hashed
  with `integrityHash` zeroed, then final footer written with the real hash.

### Reader (zarchivereader.cpp)
- `OpenFromFile` validation chain (returns null on any failure — mirror with `TryOpen`/null, no exceptions):
  fileSize > 144; magic; version; `totalSize == fileSize`; all six sections in valid range;
  offsetRecords.size ≤ 0xFFFFFFFF; names.size ≤ 0x7FFFFFFF; fileTree.size ≤ 0xFFFFFFFF;
  offsetRecords & fileTree counts = size/elemsize (must be whole, non-empty); `fileTree[0]` must be a directory;
  root name (via GetName) must be empty.
- **LRU cache**: 4 MiB = 64 × 64 KiB blocks, doubly-linked list + `Dictionary<ulong, CacheBlock>`;
  `GetCachedBlock` → hit: mark MRU; miss: recycle LRU, `LoadBlock`.
- `LoadBlock`: `recordIndex = blockIndex / 16`, `subIndex = blockIndex % 16`;
  `offset = baseOffset + Σ(size[i] + 1) for i < subIndex`; `compressedSize = size[subIndex] + 1`;
  bounds check `offset + compressedSize <= compressedDataSize`; if `compressedSize == 65536` → **raw block** read
  directly; else zstd-decompress, must yield exactly 65536.
- `LookUp(path, allowFile, allowDirectory)`: walk tree, **linear scan** per dir with `CompareNodeNameBool`;
  returns u32 node handle or `0xFFFFFFFF` (`InvalidNode`).
- `ReadFromFile(node, offset, len, buffer)`: clamp `len` to file size; per-64 KiB block via cache;
  takes a mutex in C++ → use C# `lock`.
- `GetName(nameTable, nameOffset)`: **0.1.2 QUIRK** — in the extended-2-byte-length branch it reads
  `nameTable[nameOffset]` (the FIRST byte, again) instead of `nameTable[nameOffset + 1]`
  (`nameLength |= (u16)nameTable[nameOffset] << 7`). **Port as-is for byte parity** (names ≥ 0x80 chars are broken
  upstream in 0.1.2; writer truncates at 0x7FFF and writes 2-byte header). Note it in docs.
  Guard quirk: extended branch checks `nameOffset + 1 >= nameTable.size()` (correct) then advances by 2.
- `GetDirEntry(node, index)`: name/isFile/isDirectory/size (size only for files).

### CLI (main.cpp) → port as static tool methods
- Directory → pack: recursive iterator; per directory `MakeDir(path, false)`, per file `StartNewFile(path)` +
  64 KiB `AppendData` loop; relative paths with `/` separators; refuse existing output (−11); delete incomplete
  output on failure; default output `<stem>.zar`; prints "Adding X".
- Archive → extract: recursive; default output `<stem>_extracted`; error codes −1..−16 → use exceptions in C#.

### Environment facts verified this session
- **`References\ZArchive-0.1.2\zarchive.exe` RUNS** (statically linked; prints usage) → real interop golden tests
  possible **both directions** (exe-created .zar read by ZARSharp; ZARSharp-created .zar extracted by exe).
- Solution has **no ZstdSharp.Port / no zstd at all**; existing `XISOSharp/XisoZarchive.cs` writes **raw blocks**
  (valid per spec) and was deliberately left that way — do not confuse it with this task.
- Conventions: class libs use `<TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>`,
  `<LangVersion>14</LangVersion>`, ImplicitUsings + Nullable enable. Tests live in `XISOSharp.Tests`
  (xunit 2.9.3, net10.0, `dotnet test XISOSharp.Tests/XISOSharp.Tests.csproj`). Strong naming only on XISOSharp
  (skip for ZARSharp). Commit style: lowercase prefixes like `chore:`/`refactor:` or plain "Update documentation".

## Implementation plan (execute in order)

1. **Project**: `ZARSharp/ZARSharp.csproj` (Sdk Microsoft.NET.Sdk, net8/9/10 triple, LangVersion 14, no PackageReferences,
   RootNamespace `ZARSharp`, GenerateDocumentationFile true) + `dotnet sln add`.
2. **`ZARSharp/ZArchiveCommon.cs`**: constants; `CompressionOffsetRecord`, `FileDirectoryEntry`, `Footer` structs with
   `WriteTo(Span<byte>)` / `ReadFrom(ReadOnlySpan<byte>)` big-endian helpers; **pure-C# Windows-1252 codec**
   (~40-line static table; do NOT use System.Text.Encoding.CodePages — external package); path-node parser;
   `CompareNodeName`/`CompareNodeNameBool`.
3. **`ZARSharp/ZArchiveWriter.cs`**: faithful port (callbacks + `Stream` convenience ctor; `IncrementalHash`
   SHA-256; StoreBlock/AppendData/Finalize/Write* exactly as C++).
4. **`ZARSharp/ZArchiveReader.cs`**: faithful port (TryOpen/OpenFromFile returning null on invalid; LRU cache;
   lock for thread safety; `ReadFromFile(node, offset, Span<byte>)`).
5. **`ZARSharp/Zstd/` pure-C# zstd codec** — the big piece:
   - **Decoder (full standard frames, RFC 8878)**: magic `28 B5 2F FD`; skippable frames `0x184D2A50–5F` (skip
     4-byte LE size); frame header descriptor (FCS flag bits 7-6: 0→0 (1 if single-segment), 1→2 (+256 value),
     2→4, 3→8 bytes; bit5 single-segment; bit2 content checksum; dict-ID flag bits 1-0 → 0/1/2/4 bytes);
     window descriptor (exp = byte>>3, mantissa = byte&7, windowLog = 10+exp); blocks: 3-byte LE header
     (bit0 last, bits1-2 type 0=Raw/1=RLE/2=Compressed, bits3-23 size); literals section (Raw/RLE/Compressed/
     Treeless; size formats per spec; 1- and 4-stream Huffman; 4-stream jump table = 3×2-byte LE, streams 1–3
     each regenerate `(regen+3)/4` bytes, stream 4 the remainder); Huffman weights: headerByte ≥ 128 → **direct
     nibble-packed**, count = headerByte − 127; else FSE-compressed weights (accuracy log ≤ 6); implied last
     weight: `total = 1<<maxBits`, `rest = total − Σ(1<<(w−1))`, `lastWeight = highbit(rest)+1`, must be power of 2;
     canonical table build: symbols sorted weight DESC then symbol ASC, `nbBits = maxBits + 1 − w`,
     `length = 1 << (maxBits − w)`, fill decode table sequentially, decode = read maxBits MSB-first, entry gives
     (symbol, nbBits); sequences section: nbSeq encoding (b0<128; else ((b0−128)<<8)+b1; else b1+(b2<<8)+0x7F00),
     symbol compression modes byte (bits 6-7 LL, 4-5 OF, 2-3 ML; 0=Predefined/1=RLE/2=FSE/3=Repeat),
     predefined distributions — LL 36 syms `[4,3,2,2,2,2,2,2,2,2,2,2,2,1,1,1,2,2,2,2,2,2,2,2,2,2,2,2,2,2,2,2,2,2,2,2]`,
     OF 32 syms `[1,1,1,1,1,1,2,2,2,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1]`,
     ML 53 syms `[1×53 then 2×` — **ML default = 53 entries: first 53 are all 1? NO: ML default distribution is
     `[1]*53` followed by nothing — verify exact ML array from RFC 8878 during implementation** (36 = LL, 32 = OF,
     53 = ML; ML = `[1,1,...(53 ones)]`? The known array: ML default has 53 symbols, values 1 for first 53? It is
     `[1]*53`? — safest: copy the exact arrays from RFC 8878 / zstd `ZSTD_defaultNLiteralsLengths` etc. during
     implementation, listed in zstd source as `LL_defaultNorm`, `OF_defaultNorm`, `ML_defaultNorm`);
     FSE table building (accuracy log byte, low 5 bits, valid 5–9; normalized counts with "less than 2" low-prob
     modes; RLE single-symbol tables); **backward bitstream** decoding for FSE and sequences; sequence execution
     (LL/ML baselines+extra-bits tables, OF codes with repeat-offsets 1–3 rules); XXH64 checksum verify when
     checksum flag set; multi-frame concatenation support.
   - **Encoder**: emit valid frames using Raw/RLE blocks only (store mode) with correct frame header
     (single-segment + FCS for known sizes) — **byte-exact zstd level-6 compression is NOT required for format
     validity**; any real zstd decodes it; zarchive.exe reads it. Mark level-6 compressor parity as an optional
     follow-up TODO.
6. **`ZARSharp/ZArchiveTool.cs`**: main.cpp port (`Pack(inputDir, outputZar)`, `Extract(inputZar, outputDir)`),
   exceptions instead of exit codes, refuse-overwrite + delete-incomplete-output semantics preserved.
7. **Tests** `XISOSharp.Tests/ZArchiveSharpTests.cs`: format round-trip (writer→reader), big-endian hex-vector
   footer/entry checks, LRU cache behavior, case-insensitive LookUp, MakeDir recursive, name-table edge cases,
   long-name (≥0x80) **quirk experiment against zarchive.exe** (confirm upstream behavior, match it),
   interop: pack dir with exe → read/extract with ZARSharp; pack dir with ZARSharp → extract with exe;
   integrity-hash corruption detection; empty file, empty dirs, 64KiB-aligned and unaligned sizes, >4 blocks file.
8. **Wrap-up**: full `dotnet build CSharp_XISOSharp.sln` + `dotnet test` (702 + new = green, 0 warnings);
   update `README.md` comparison matrix (ZArchive row) and `TODO2.md` ZAR items (ZARSharp makes the
   "ZAR reader/decompressor" and "rebuild from .zar" prerequisites done — rebuild wiring may remain);
   consider wiring `--zar` CLI mode to ZARSharp later (out of scope unless asked).

## Where the previous session left off (context)

- CISO v2 (LZ4) writer + split CSO output/input + `checksum` `.cso` auto-detect via `CisoBlockDevice` — **complete,
  702/702 tests, 0 warnings** (see `docs/compression.md`, `docs/cli.md`). Those changes are committed in the same
  commit as this file.
- `TODO.md` / `TODO2.md` are **intentionally untracked** (`.git/info/exclude`) — local only; they were updated with
  current status (702 tests; ZARSharp plan pending).
