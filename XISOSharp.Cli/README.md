# XISOSharp.Cli

Command-line tool for creating, extracting, listing, and rewriting Xbox ISO (XISO) disc images.

This project is a direct conversion of the [extract-xiso](https://github.com/XboxDev/extract-xiso) CLI tool (v2.7.1) from C to C#. It provides the same interface and produces byte-identical output for all operations.

## Usage

```
extract-xiso [options] [-[lrx]] <file1.xiso> [file2.xiso] ...
extract-xiso [options] -c <dir> [name] [-c <dir> [name]] ...
```

### Modes (mutually exclusive)

| Flag | Description |
|---|---|
| `-c <dir> [name]` | Create xiso from file(s) starting in `<dir>` |
| `--copy-out <iso> <path> <dest>` | Copy a file or directory out of an xiso |
| `-i <file> [path]` | Show volume info and directory entry metadata |
| `-l` | List files in xiso(s) |
| `--md5 <file> [path]` | Compute MD5 hash of file(s) in xiso |
| `-r` | Rewrite xiso(s) as optimized xiso(s) |
| `--sha256 <file> [path]` | Compute SHA-256 hash of file(s) in xiso |
| `-t` | List all files recursively with sizes (tree) |
| `-V <file1.xiso> ...` | Deep-audit xiso(s): validate header, tree, sectors |
| `-x` | Extract xiso(s) (the default mode) |

### Options

| Flag | Description |
|---|---|
| `-d <directory>` | In extract mode, expand xiso in `<directory>`. In rewrite mode, rewrite xiso in `<directory>` |
| `-D` | In rewrite mode, delete old xiso after processing |
| `-h` | Print help text and exit |
| `-m` | Disable automatic `.xbe` media enable patching |
| `-o <filename>` | In rewrite mode, set custom output filename (default: original name with `.iso` extension) |
| `-q` | Quiet (suppress all non-error output) |
| `-Q` | Silent (suppress all output) |
| `-s` | Skip `$SystemUpdate` folder |
| `-v` | Print version information and exit |

### New Features (beyond extract-xiso)

These commands are not present in the original C tool:

- **`-t`** — Tree listing with file sizes and totals
- **`-i`** — Volume metadata and directory entry inspection
- **`-V`** — Deep integrity audit (header, tree, sector bounds, cycle detection)
- **`-o`** — Custom output filename for rewrite mode
- **`--copy-out`** — Selective file/directory extraction
- **`--md5` / `--sha256`** — Per-file hash computation

## License

MIT
