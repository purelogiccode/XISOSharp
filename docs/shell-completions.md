# Shell Completions & Man Page

Tab completion for the `XISOSharp` CLI is shipped for PowerShell, bash, zsh and
fish, together with a `xisosharp(1)` manual page. The files live under
[`docs/completions`](completions/) and [`docs/man`](man/), so they are also
served by this documentation site.

| Shell | File |
|---|---|
| PowerShell 7+ | [completions/xisosharp.ps1](completions/xisosharp.ps1) |
| bash | [completions/xisosharp.bash](completions/xisosharp.bash) |
| zsh | [completions/_xisosharp](completions/_xisosharp) |
| fish | [completions/xisosharp.fish](completions/xisosharp.fish) |
| man page | [man/xisosharp.1](man/xisosharp.1) |

The scripts complete every documented verb and flag. Value flags
(`--format`, `--name-policy`, `--jobs`, …) complete their documented values.
The bash, zsh and PowerShell scripts offer flags before the first positional
argument (fish completes flags in any position).

## PowerShell

Supply the script from your profile:

```powershell
# In $PROFILE
. "$HOME\path\to\xisosharp.ps1"
```

Completion covers both `XISOSharp` and the lowercase `xisosharp` alias installed
by package managers. It requires PowerShell 7 because it uses
`Register-ArgumentCompleter -Native`.

## bash

```bash
# current shell
source /path/to/xisosharp.bash

# persistent (pick one)
sudo cp xisosharp.bash /etc/bash_completion.d/xisosharp
cp xisosharp.bash ~/.local/share/bash-completion/completions/XISOSharp
```

## zsh

```zsh
mkdir -p ~/.zsh/completions
cp _xisosharp ~/.zsh/completions/_xisosharp

# In ~/.zshrc, before compinit:
fpath=(~/.zsh/completions $fpath)
autoload -Uz compinit && compinit
```

## fish

```fish
cp xisosharp.fish ~/.config/fish/completions/XISOSharp.fish
```

The fish script registers completions for both `XISOSharp` and `xisosharp`.

## Man page

The manual page is plain roff and installs into the standard `man1` directory:

```bash
sudo install -Dm644 docs/man/xisosharp.1 /usr/local/share/man/man1/xisosharp.1
man xisosharp
```

or view it without installing:

```bash
man -l docs/man/xisosharp.1
```

## Package managers and containers

The release bundles are also described by generated winget, Scoop and Homebrew
manifests under `packaging/`, and a container image is available from GHCR. See
[Getting Started](getting-started.md#package-managers-and-container) for the install
commands.
