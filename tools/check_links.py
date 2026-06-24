#!/usr/bin/env python3
"""check_links.py - CI gate: relative links in tracked markdown + site HTML resolve.

Checks, across all git-tracked *.md files AND the tracked public-site
*.html under site/ (adversarial fuzzer corpora excluded - see
EXCLUDE_PREFIXES):
  * markdown inline links  [text](target)  and HTML  href= / src= /
    srcset= attributes
  * the target file/directory exists (resolved relative to the source
    file; a leading "/" resolves from the repo root)
  * a #fragment into a .md file matches a real heading, using GitHub's
    anchor algorithm (lowercase; backticks/bold/link markup stripped;
    punctuation dropped; spaces to hyphens; duplicate headings get
    -1/-2/... suffixes)
  * a #fragment into a .html file matches a real id=/name= anchor

Markdown links inside fenced code blocks and inline code spans are
ignored - they are examples, not navigation; HTML is scanned line for
line (href/src/srcset only). Only site/ HTML is gated, not the SPA shell
(wwwroot, whose modules/bundles resolve at publish) or test harnesses.
External URLs (http/https/mailto) are out of scope: this gate keeps the
docs + site internal cross-link graph sound (docs/design/README.md - "if
you change behaviour a doc covers, update the doc in the same change").

Stdlib only. Exit 0 = clean, 1 = problems (annotated inline when
GITHUB_ACTIONS is set). Run directly or via `make check-links`.
"""

from __future__ import annotations

import os
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

# Adversarial markdown fixtures for the renderer fuzzer / differential
# tester: they intentionally carry javascript:/vbscript:/dangling
# relative link targets to exercise the renderer's sanitization (F4), so
# they are not part of the docs' navigable cross-link graph.
EXCLUDE_PREFIXES = ("tools/markdown/corpus/",)

MD_LINK = re.compile(
    r"\[(?:[^\]\[]|\[[^\]]*\])*\]"  # [text], one nesting level deep
    r"\(([^)\s]+)(?:\s+\"[^\"]*\")?\)"  # (target "optional title")
)
HTML_ATTR = re.compile(r"""(?:href|src)\s*=\s*["']([^"']+)["']""")
HTML_SRCSET = re.compile(r"""srcset\s*=\s*["']([^"']+)["']""")
HTML_ANCHOR = re.compile(r"""(?:id|name)\s*=\s*["']([^"']+)["']""")
HEADING = re.compile(r"^(#{1,6})\s+(.*?)\s*#*\s*$")
FENCE = re.compile(r"^\s*(```|~~~)")

# Only the public site is gated. The SPA shell (wwwroot) references modules and
# the publish-time app.js/app.css bundle that do not exist at rest; the test
# harness HTML is not navigation. Both would false-positive, so scope to site/.
HTML_PREFIXES = ("site/",)


def _tracked(pattern: str) -> list[str]:
    out = subprocess.run(
        ["git", "ls-files", "-z", pattern],
        cwd=ROOT,
        capture_output=True,
        check=True,
    ).stdout
    return [p.decode() for p in out.split(b"\0") if p]


def tracked_markdown() -> list[Path]:
    return [
        ROOT / rel
        for rel in _tracked("*.md")
        if not rel.startswith(EXCLUDE_PREFIXES)
    ]


def tracked_html() -> list[Path]:
    return [
        ROOT / rel
        for rel in _tracked("*.html")
        if rel.startswith(HTML_PREFIXES)
    ]


def mask_fences(text: str) -> list[str]:
    """Lines with fenced code blocks blanked (1:1 for line numbers)."""
    lines: list[str] = []
    in_fence = False
    for line in text.splitlines():
        if FENCE.match(line):
            in_fence = not in_fence
            lines.append("")
            continue
        lines.append("" if in_fence else line)
    return lines


def strip_inline_code(line: str) -> str:
    """Drop inline `code` spans - example links there aren't navigation.

    Only used for link extraction; heading anchors must KEEP code-span
    text (GitHub strips just the backticks there).
    """
    parts = line.split("`")
    return "`".join(
        p if i % 2 == 0 else "" for i, p in enumerate(parts)
    )


def github_anchor(heading: str) -> str:
    text = re.sub(r"\[([^\]]*)\]\([^)]*\)", r"\1", heading)  # [t](u)->t
    text = text.replace("`", "").replace("**", "").replace("*", "")
    text = re.sub(r"[^\w\s-]", "", text.strip().lower())
    return text.replace(" ", "-")


_anchor_cache: dict[Path, set[str]] = {}


def anchors_of(path: Path) -> set[str]:
    if path not in _anchor_cache:
        seen: dict[str, int] = {}
        result: set[str] = set()
        for line in mask_fences(path.read_text(encoding="utf-8")):
            m = HEADING.match(line)
            if not m:
                continue
            anchor = github_anchor(m.group(2))
            n = seen.get(anchor, 0)
            seen[anchor] = n + 1
            result.add(anchor if n == 0 else f"{anchor}-{n}")
        _anchor_cache[path] = result
    return _anchor_cache[path]


_html_anchor_cache: dict[Path, set[str]] = {}


def html_anchors_of(path: Path) -> set[str]:
    """The explicit anchor targets in an HTML file: id= and name= values."""
    if path not in _html_anchor_cache:
        text = path.read_text(encoding="utf-8")
        _html_anchor_cache[path] = set(HTML_ANCHOR.findall(text))
    return _html_anchor_cache[path]


def targets_in(line: str, is_html: bool = False) -> list[str]:
    # HTML is navigation markup, not prose: take only href/src/srcset, and skip
    # the markdown-specific masking (a `[x](y)` or backtick in script/style text
    # is not a link).
    if not is_html:
        line = strip_inline_code(line)
    found = [] if is_html else [m.group(1) for m in MD_LINK.finditer(line)]
    found += [m.group(1) for m in HTML_ATTR.finditer(line)]
    for m in HTML_SRCSET.finditer(line):
        found += [
            entry.strip().split()[0]
            for entry in m.group(1).split(",")
            if entry.strip()
        ]
    return found


def check_source(src: Path, lines: list[str], errors: list[tuple[Path, int, str]]) -> int:
    """Validate every relative link in one source file; append problems. Returns
    the link count. .md targets resolve fragments against headings, .html targets
    against id=/name= anchors."""
    is_html = src.suffix in (".html", ".htm")
    total = 0
    for lineno, line in enumerate(lines, 1):
        for target in targets_in(line, is_html=is_html):
            if target.startswith(
                ("http://", "https://", "mailto:", "data:", "tel:")
            ):
                continue
            total += 1
            if target.startswith("#"):
                dest, frag = src, target[1:]
            else:
                rel, _, frag = target.partition("#")
                base = ROOT if rel.startswith("/") else src.parent
                dest = (base / rel.lstrip("/")).resolve()
                if not dest.exists():
                    errors.append((src, lineno, f"missing file: {target}"))
                    continue
            if not (frag and dest.is_file()):
                continue
            if dest.suffix == ".md" and frag not in anchors_of(dest):
                errors.append((src, lineno, f"bad anchor: {target}"))
            elif dest.suffix in (".html", ".htm") and frag not in html_anchors_of(dest):
                errors.append((src, lineno, f"bad anchor: {target}"))
    return total


def main() -> int:
    errors: list[tuple[Path, int, str]] = []
    total = 0

    for md in tracked_markdown():
        if not md.is_file():
            continue  # tracked but deleted in the worktree (pending git rm)
        total += check_source(md, mask_fences(md.read_text(encoding="utf-8")), errors)
    for html in tracked_html():
        if not html.is_file():
            continue
        total += check_source(html, html.read_text(encoding="utf-8").splitlines(), errors)

    print(
        f"check_links: {total} relative links across tracked markdown + site HTML"
    )
    if errors:
        on_actions = os.environ.get("GITHUB_ACTIONS") == "true"
        for path, lineno, msg in errors:
            rel_path = path.relative_to(ROOT)
            if on_actions:
                print(f"::error file={rel_path},line={lineno}::{msg}")
            print(f"  {rel_path}:{lineno}: {msg}")
        print(f"check_links: {len(errors)} problem(s)")
        return 1
    print("check_links: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
