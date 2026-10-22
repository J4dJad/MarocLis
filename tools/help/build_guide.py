#!/usr/bin/env python3
"""Builds docs/guide-installation.html from docs/guide-installation.md.

One self-contained page: the style of the in-app help (STYLE of build_help.py), a clickable table
of contents built from every h2 and h3, and a print style. Same contract as the help pages, so it
also opens in the Windows WebBrowser control (Internet Explorer engine): X-UA-Compatible, no CSS
variables, no flex gap, no script, no external resource.

Needs: python3 with the "markdown" package (python3 -m pip install markdown).

Usage: python3 tools/help/build_guide.py
"""

import html
import re
import sys
from pathlib import Path

import markdown

sys.path.insert(0, str(Path(__file__).resolve().parent))
from build_help import STYLE  # noqa: E402  (same look as the in-app help)

REPO = Path(__file__).resolve().parents[2]
SOURCE = REPO / "docs" / "guide-installation.md"
TARGET = REPO / "docs" / "guide-installation.html"

EXTRA_STYLE = """
.lead { font-size: 13px; color: #5c616a; margin: 0 0 16px 0; }
.toc { background: #f7f8fa; border: 1px solid #d8dade; padding: 12px 20px 14px 20px; margin: 18px 0 28px 0; }
.toc h2 { margin-top: 4px; border-bottom: none; font-size: 18px; }
.toc ol { margin: 6px 0 0 0; padding-left: 22px; }
.toc ol ol { margin: 2px 0 6px 0; padding-left: 20px; list-style-type: none; }
.toc li { margin: 3px 0; }
.toc ol ol li { font-size: 14px; margin: 1px 0; }
.toc a { text-decoration: none; }
.toc a:hover { text-decoration: underline; }
pre { border: 1px solid #d8dade; white-space: pre-wrap; word-wrap: break-word; }
pre code { background: transparent; padding: 0; font-size: 13px; }
tr:nth-child(even) td { background: #fafbfc; }
blockquote { margin: 12px 0; padding: 8px 14px; border-left: 4px solid #1b5e8c; background: #e6eef4; }
li { margin: 2px 0; }
@media print {
  body { font-size: 11pt; }
  .page { max-width: none; padding: 0; }
  a { color: #1c1e21; text-decoration: none; }
  .top { display: none; }
  .toc { page-break-after: always; border: none; background: #ffffff; padding: 0; }
  h2 { page-break-before: always; }
  .toc h2 { page-break-before: auto; }
  h2, h3, h4 { page-break-after: avoid; }
  pre, tr, blockquote { page-break-inside: avoid; }
  th { background: #e6eef4; }
  tr:nth-child(even) td { background: #ffffff; }
}
"""


def render(md_text: str) -> tuple[str, str, list]:
    md = markdown.Markdown(
        extensions=["tables", "fenced_code", "sane_lists", "toc"],
        extension_configs={"toc": {"toc_depth": "1-3"}},
    )
    body = md.convert(md_text)
    tokens = md.toc_tokens
    title = tokens[0]["name"] if tokens and tokens[0]["level"] == 1 else "Guide d'installation"
    sections = tokens[0]["children"] if tokens and tokens[0]["level"] == 1 else tokens
    return title, body, sections


def toc_html(sections: list) -> str:
    def items(entries: list) -> str:
        out = []
        for entry in entries:
            sub = [c for c in entry["children"] if c["level"] <= 3]
            inner = f"\n<ol>\n{items(sub)}</ol>\n" if sub else ""
            # The names come back HTML-escaped from the toc extension.
            out.append(f'<li><a href="#{entry["id"]}">{entry["name"]}</a>{inner}</li>\n')
        return "".join(out)

    return f'<div class="toc" id="sommaire">\n<h2>Sommaire</h2>\n<ol>\n{items(sections)}</ol>\n</div>\n'


def build() -> None:
    title, body, sections = render(SOURCE.read_text(encoding="utf-8"))
    # The toc entries are numbered in the headings themselves ("1. Vue d'ensemble"): the list keeps
    # them, so drop the list's own numbering.
    toc = toc_html(sections).replace("<ol>", '<ol style="list-style-type: none; padding-left: 4px;">', 1)

    # Contents after the title and its introduction, before the first section.
    first_h2 = body.find("<h2")
    body = body[:first_h2] + toc + body[first_h2:]

    # A way back to the contents before each section but the first.
    back = '<p class="top"><a href="#sommaire">Retour au sommaire</a></p>\n'
    parts = re.split(r'(?=<h2 id=")', body)
    body = parts[0] + "".join((back if i > 0 else "") + p for i, p in enumerate(parts[1:])) + back

    page = (
        "<!DOCTYPE html>\n<html lang=\"fr\">\n<head>\n<meta charset=\"utf-8\">\n"
        "<meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\">\n"
        "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n"
        f"<title>{html.escape(html.unescape(title))}</title>\n<style>{STYLE}{EXTRA_STYLE}</style>\n</head>\n"
        "<body>\n<div class=\"page\">\n"
        "<div class=\"crumb\">MarocLIS · Documentation</div>\n"
        f"{body}</div>\n</body>\n</html>\n"
    )
    TARGET.write_text(page, encoding="utf-8", newline="\n")
    print(f"{TARGET.relative_to(REPO)}: {len(page) // 1024} Ko, {len(sections)} sections")


if __name__ == "__main__":
    build()
