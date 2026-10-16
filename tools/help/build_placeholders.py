#!/usr/bin/env python3
"""Writes placeholder help pages for the desktop client (src/MarocLIS.Desktop/Help).

Reads Help/help-map.json (navigation key -> chapter file), the navigation entries of
Forms/MainShellForm.cs (key -> Nav.* label key) and the French resources for the screen names,
then writes Help/fr/<chapter>.html with one <section id="<navigation key>"> per screen, and
Help/fr/index.html linking to every chapter and section.

Contract kept by any replacement generator (the application relies on it):
  * Help/help-map.json maps every navigation key to a chapter file name (no folder);
  * Help/<lang>/<chapter> exists for fr at least (ar/ and en/ fall back to fr file by file);
  * the section of a screen carries id="<navigation key>";
  * Help/<lang>/index.html is the table of contents;
  * pages are read by the Windows WebBrowser control (Internet Explorer engine): keep the
    X-UA-Compatible meta, no CSS variables, no flex gap, no script, no target="_blank";
    images go in Help/<lang>/img/ and are referenced relatively (img/x.png).

Usage: python3 tools/help/build_placeholders.py   (from anywhere; paths are relative to the repo)
"""

import html
import json
import re
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
DESKTOP = REPO / "src" / "MarocLIS.Desktop"
HELP = DESKTOP / "Help"
SHELL = DESKTOP / "Forms" / "MainShellForm.cs"
FRENCH = REPO / "src" / "MarocLIS.Localization" / "Resources" / "fr.json"

CHAPTER_TITLES = {
    "00-prise-en-main.html": "Prise en main",
    "01-accueil-enregistrement.html": "Accueil et enregistrement",
    "02-pre-analytique.html": "Pré-analytique",
    "03-analytique-comptes-rendus.html": "Analytique et comptes rendus",
    "04-facturation.html": "Facturation",
    "05-qualite-equipements.html": "Qualité et équipements",
    "06-stocks-achats.html": "Stocks et achats",
    "07-finance.html": "Finance",
    "08-pilotage-statistiques-relations.html": "Pilotage, statistiques et relations",
    "09-ressources-humaines.html": "Ressources humaines",
    "10-administration.html": "Administration",
}

STYLE = """
body { font-family: "Segoe UI", Tahoma, Arial, sans-serif; font-size: 15px; line-height: 1.5;
       color: #1f2933; background: #ffffff; margin: 0; padding: 0; }
.page { max-width: 900px; margin: 0 auto; padding: 16px 24px 48px 24px; }
h1 { font-size: 26px; color: #0b4f8a; border-bottom: 2px solid #0b4f8a; padding-bottom: 6px; }
h2 { font-size: 20px; color: #0b4f8a; margin-top: 32px; }
a { color: #0b5fa5; }
img { max-width: 100%; border: 1px solid #d0d7de; }
ul.toc li { margin: 4px 0; }
section { margin-bottom: 24px; }
"""


def page(title: str, body: str) -> str:
    return (
        "<!DOCTYPE html>\n<html lang=\"fr\">\n<head>\n<meta charset=\"utf-8\">\n"
        "<meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\">\n"
        f"<title>{html.escape(title)}</title>\n<style>{STYLE}</style>\n</head>\n"
        f"<body>\n<div class=\"page\">\n{body}</div>\n</body>\n</html>\n"
    )


def nested(resources: dict, dotted: str) -> str:
    node = resources
    for part in dotted.split("."):
        if not isinstance(node, dict) or part not in node:
            return dotted
        node = node[part]
    return node if isinstance(node, str) else dotted


def main() -> None:
    mapping = json.loads((HELP / "help-map.json").read_text(encoding="utf-8"))
    labels = dict(re.findall(r'new\("([a-z.]+)", "(Nav\.[A-Za-z]+)"', SHELL.read_text(encoding="utf-8")))
    french = json.loads(FRENCH.read_text(encoding="utf-8-sig"))

    def name(key: str) -> str:
        return nested(french, labels[key]) if key in labels else key

    chapters: dict[str, list[str]] = {}
    for key, chapter in mapping.items():
        chapters.setdefault(chapter, []).append(key)

    out = HELP / "fr"
    out.mkdir(parents=True, exist_ok=True)

    toc = ["<h1>Aide de MarocLIS</h1>\n<ul class=\"toc\">\n"]
    for chapter in sorted(chapters):
        title = CHAPTER_TITLES.get(chapter, chapter)
        sections = "".join(
            f"<section id=\"{key}\">\n<h2>{html.escape(name(key))}</h2>\n"
            f"<p>Aide en cours de rédaction.</p>\n</section>\n"
            for key in chapters[chapter])
        body = (f"<p><a href=\"index.html\">Sommaire</a></p>\n<h1>{html.escape(title)}</h1>\n"
                + sections)
        (out / chapter).write_text(page(title, body), encoding="utf-8", newline="\n")

        links = "".join(f"<li><a href=\"{chapter}#{key}\">{html.escape(name(key))}</a></li>\n"
                        for key in chapters[chapter])
        toc.append(f"<li><a href=\"{chapter}\">{html.escape(title)}</a>\n<ul>\n{links}</ul>\n</li>\n")
    toc.append("</ul>\n")

    (out / "index.html").write_text(page("Aide de MarocLIS", "".join(toc)), encoding="utf-8", newline="\n")


if __name__ == "__main__":
    main()
