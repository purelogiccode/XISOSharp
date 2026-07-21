# ExtractXiso CLI

Command-line tool for creating, extracting, listing, and rewriting Xbox ISO (XISO) disc images.

## License

MIT

## Usage

```
extract-xiso [options] [-[lrx]] <file1.xiso> [file2.xiso] ...
extract-xiso [options] -c <dir> [name] [-c <dir> [name]] ...
```

### Modes (mutually exclusive)

| Flag | Description |
|---|---|
| `-c <dir> [name]` | Create xiso from file(s) starting in `<dir>` |
| `-l` | List files in xiso(s) |
| `-r` | Rewrite xiso(s) as optimized xiso(s) |
| `-x` | Extract xiso(s) (the default mode) |

### Options

| Flag | Description |
|---|---|
| `-d <directory>` | In extract mode, expand xiso in `<directory>`. In rewrite mode, rewrite xiso in `<directory>` |
| `-D` | In rewrite mode, delete old xiso after processing |
| `-h` | Print help text and exit |
| `-m` | Disable automatic `.xbe` media enable patching (not recommended) |
| `-q` | Quiet (suppress all non-error output) |
| `-Q` | Silent (suppress all output) |
| `-s` | Skip `$SystemUpdate` folder |
| `-v` | Print version information and exit |
