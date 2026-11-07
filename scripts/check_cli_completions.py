#!/usr/bin/env python3
"""Keep the CLI completion files in sync with the CLI parser.

Every flag string literal in XISOSharp.Cli/*.cs must appear in each completion
file. This catches a new flag that works but cannot be tab-completed. The check
is intentionally textual: it does not try to model parser context.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

FLAG = re.compile(r'"(-{1,2}[A-Za-z][A-Za-z0-9-]*)"')

COMPLETION_FILES = (
    "docs/completions/xisosharp.ps1",
    "docs/completions/xisosharp.bash",
    "docs/completions/_xisosharp",
    "docs/completions/xisosharp.fish",
)


def cli_flags(repo: Path) -> set[str]:
    flags: set[str] = set()
    for source in sorted((repo / "XISOSharp.Cli").glob("*.cs")):
        flags.update(FLAG.findall(source.read_text(encoding="utf-8")))
    # Aliases that only appear in composed help text.
    flags.update(
        {
            "--dryrun",
            "--filetime",
            "--get-filetime",
            "--ignore-extra",
            "--output-name",
            "--security-sectors",
            "--sectors",
            "--verify-hashes",
        }
    )
    return flags


def main() -> int:
    repo = Path(__file__).resolve().parent.parent
    flags = cli_flags(repo)
    missing: list[tuple[str, list[str]]] = []
    for relative in COMPLETION_FILES:
        path = repo / relative
        text = path.read_text(encoding="utf-8")
        absent = sorted(flag for flag in flags if flag not in text)
        if absent:
            missing.append((relative, absent))

    if missing:
        print("cli-completions: completion files are missing flags:", file=sys.stderr)
        for relative, absent in missing:
            print(f"  {relative}: {', '.join(absent)}", file=sys.stderr)
        return 1

    print(
        f"cli-completions: OK ({len(flags)} flags present in "
        f"{len(COMPLETION_FILES)} completion files)"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
