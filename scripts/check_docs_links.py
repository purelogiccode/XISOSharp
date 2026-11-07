#!/usr/bin/env python3
"""Fail on broken local links in the built docs site.

Checks Markdown links/images and HTML href/src attributes. External URLs,
mail links, anchors and template placeholders are ignored; targets are resolved
relative to the containing file (or the site root for /-prefixed paths).
"""

from __future__ import annotations

import re
import sys
from pathlib import Path
from urllib.parse import unquote, urlsplit

MARKDOWN_LINK = re.compile(r"!?\[[^\]]*\]\(([^)\s]+)(?:\s+\"[^\"]*\")?\)")
HTML_LINK = re.compile(r"(?:href|src)\s*=\s*[\"']([^\"']+)[\"']")
SKIP_SCHEMES = ("http:", "https:", "mailto:", "tel:", "javascript:", "data:")


def iter_links(text: str):
    for match in MARKDOWN_LINK.finditer(text):
        yield match.group(1)
    for match in HTML_LINK.finditer(text):
        yield match.group(1)


def resolve(root: Path, source: Path, target: str) -> Path | None:
    split = urlsplit(target)
    if split.scheme:
        if f"{split.scheme}:" in SKIP_SCHEMES:
            return None
        return None
    if target.startswith("#") or target.startswith("//"):
        return None

    path = unquote(split.path)
    if not path:
        return None
    if any(token in path for token in ("{", "}", "<", ">")):
        return None

    candidate = (root / path.lstrip("/")) if path.startswith("/") else (source.parent / path)
    candidate = candidate.resolve()
    try:
        candidate.relative_to(root.resolve())
    except ValueError:
        return None

    if candidate.is_dir():
        for index in ("index.html", "index.md", "README.md"):
            indexed = candidate / index
            if indexed.exists():
                return indexed
        return candidate
    return candidate


def main() -> int:
    root = Path(sys.argv[1] if len(sys.argv) > 1 else "_site").resolve()
    if not root.is_dir():
        print(f"docs-link-check: root not found: {root}", file=sys.stderr)
        return 2

    broken: list[tuple[Path, str]] = []
    checked = 0
    for file in sorted(root.rglob("*")):
        if not file.is_file() or file.suffix.lower() not in {".md", ".html", ".htm"}:
            continue
        try:
            text = file.read_text(encoding="utf-8", errors="replace")
        except OSError as ex:
            print(f"docs-link-check: cannot read {file}: {ex}", file=sys.stderr)
            return 2

        for target in iter_links(text):
            resolved = resolve(root, file, target)
            if resolved is None:
                continue
            checked += 1
            if not resolved.exists():
                broken.append((file.relative_to(root), target))

    if broken:
        print(f"docs-link-check: {len(broken)} broken link(s) in {root}", file=sys.stderr)
        for source, target in broken[:200]:
            print(f"  {source}: {target}", file=sys.stderr)
        if len(broken) > 200:
            print(f"  ... and {len(broken) - 200} more", file=sys.stderr)
        return 1

    print(f"docs-link-check: OK ({checked} local link(s) checked in {root})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
