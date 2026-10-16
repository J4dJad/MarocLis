#!/usr/bin/env python3
"""Builds the in-app help (src/MarocLIS.Desktop/Help/fr) from the user manual (docs/manuel).

Each chapter docs/manuel/NN-slug.md becomes Help/fr/NN-slug.html. Its ```mermaid diagrams are
drawn as PNG pictures in Help/fr/img/ (one file per diagram, named by the hash of its source, so
an unchanged diagram is not drawn again), and each screen's heading carries id="<navigation key>"
so that F1 opens the page at the screen the user is on.

The pages keep the contract of build_placeholders.py: shown by the Windows WebBrowser control
(Internet Explorer engine), so X-UA-Compatible, no CSS variables, no flex gap, no script.

Needs: python3 with the "markdown" package, and the mermaid command line (mmdc) for new or
changed diagrams: MMDC=/path/to/mmdc, MMDC_PUPPETEER=/path/to/puppeteer.json (optional).

Usage: python3 tools/help/build_help.py
"""

import hashlib
import html
import json
import os
import re
import subprocess
import sys
import tempfile
import unicodedata
from pathlib import Path

import markdown

REPO = Path(__file__).resolve().parents[2]
MANUAL = REPO / "docs" / "manuel"
DESKTOP = REPO / "src" / "MarocLIS.Desktop"
HELP = DESKTOP / "Help"
OUT = HELP / "fr"
IMG = OUT / "img"
SHELL = DESKTOP / "Forms" / "MainShellForm.cs"
FRENCH = REPO / "src" / "MarocLIS.Localization" / "Resources" / "fr.json"

# Screens whose heading in the manual does not carry the menu label word for word.
HEADING_OF = {
    "interfaces": "Résultats reçus des automates",
    "cashreview": "Visa des caisses",
    "personnel": "Dossiers du personnel",
    "hrdeadlines": "Échéances RH",
    "leave": "Congés et absences",
    "planning": "Planning des gardes",
    "relations": "Visites et conventions",
    "patientmessaging": "Rappels et enquêtes",
    "contracts": "Contrats automates",
    "accountingsettings": "Comptes et journaux",
    "cashforecast": "Prévision de trésorerie",
    "orders": "Dossiers",
}

# The administration chapter documents the analysers' screen as "Automates"; the analytical
# chapter documents the results they send. F1 on that screen opens the administration page.
CHAPTER_HEADING_OF = {
    ("10-administration.html", "interfaces"): "Automates",
}

MERMAID_CONFIG = {
    "theme": "base",
    "themeVariables": {
        "fontFamily": "Segoe UI, Arial, sans-serif",
        "fontSize": "15px",
        "primaryColor": "#E6EEF4",
        "primaryBorderColor": "#1B5E8C",
        "primaryTextColor": "#1C1E21",
        "lineColor": "#5C616A",
        "secondaryColor": "#FDF4E3",
        "tertiaryColor": "#F0F1F3",
        "edgeLabelBackground": "#FFFFFF",
    },
    "flowchart": {"htmlLabels": True, "curve": "basis"},
}

STYLE = """
body { font-family: "Segoe UI", Tahoma, Arial, sans-serif; font-size: 15px; line-height: 1.55;
       color: #1c1e21; background: #ffffff; margin: 0; padding: 0; }
.page { max-width: 940px; margin: 0 auto; padding: 16px 28px 56px 28px; }
.crumb { font-size: 13px; color: #5c616a; margin-bottom: 4px; }
h1 { font-size: 26px; color: #1b5e8c; border-bottom: 2px solid #1b5e8c; padding-bottom: 6px; margin-top: 8px; }
h2 { font-size: 21px; color: #1b5e8c; margin-top: 36px; border-bottom: 1px solid #d8dade; padding-bottom: 4px; }
h3 { font-size: 17px; color: #1c1e21; margin-top: 26px; }
h4 { font-size: 15px; color: #1c1e21; margin-top: 20px; }
a { color: #1b5e8c; }
code { font-family: Consolas, monospace; font-size: 13px; background: #f0f1f3; padding: 1px 4px; }
pre { background: #f0f1f3; padding: 10px; overflow: auto; }
table { border-collapse: collapse; margin: 12px 0; width: 100%; }
th, td { border: 1px solid #d8dade; padding: 6px 8px; text-align: left; vertical-align: top; font-size: 14px; }
th { background: #e6eef4; }
.diagram { margin: 16px 0 20px 0; text-align: center; }
.diagram img { max-width: 100%; border: 1px solid #d8dade; padding: 8px; background: #ffffff; }
ul.toc li { margin: 4px 0; }
.top { font-size: 13px; margin-top: 32px; }
"""


def page(title: str, body: str, crumb: str = "") -> str:
    crumb_html = f'<div class="crumb">{crumb}</div>\n' if crumb else ""
    return (
        "<!DOCTYPE html>\n<html lang=\"fr\">\n<head>\n<meta charset=\"utf-8\">\n"
        "<meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\">\n"
        f"<title>{html.escape(title)}</title>\n<style>{STYLE}</style>\n</head>\n"
        f"<body>\n<div class=\"page\">\n{crumb_html}{body}</div>\n</body>\n</html>\n"
    )


def norm(text: str) -> str:
    text = unicodedata.normalize("NFKD", text).encode("ascii", "ignore").decode().lower()
    return re.sub(r"[^a-z0-9]+", " ", text).strip()


def nested(resources: dict, dotted: str) -> str:
    node = resources
    for part in dotted.split("."):
        if not isinstance(node, dict) or part not in node:
            return dotted
        node = node[part]
    return node if isinstance(node, str) else dotted


def render_diagram(source: str) -> str | None:
    """Draws one diagram; returns the image file name, or None when it cannot be drawn."""
    digest = hashlib.sha1(source.encode("utf-8")).hexdigest()[:16]
    name = f"diagramme-{digest}.png"
    target = IMG / name
    if target.exists():
        return name

    mmdc = os.environ.get("MMDC")
    if not mmdc:
        print(f"  ! diagram {digest} not drawn: set MMDC", file=sys.stderr)
        return None

    IMG.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        src = Path(tmp) / "d.mmd"
        cfg = Path(tmp) / "c.json"
        src.write_text(source, encoding="utf-8")
        cfg.write_text(json.dumps(MERMAID_CONFIG), encoding="utf-8")
        command = [mmdc, "-i", str(src), "-o", str(target), "-c", str(cfg), "-b", "white", "-s", "2", "-w", "900"]
        if os.environ.get("MMDC_PUPPETEER"):
            command += ["-p", os.environ["MMDC_PUPPETEER"]]
        result = subprocess.run(command, capture_output=True, text=True)
        if result.returncode != 0 or not target.exists():
            print(f"  ! diagram {digest} failed: {result.stderr.strip()[:300]}", file=sys.stderr)
            return None
    shrink(target)
    return name


def shrink(target: Path) -> None:
    """Diagrams use a handful of flat colours: a 64-colour palette divides the file size by three."""
    try:
        from PIL import Image
    except ImportError:
        return
    picture = Image.open(target).convert("RGB")
    picture.quantize(colors=64, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE).save(target, optimize=True)


def convert(md_text: str, failures: list) -> str:
    def diagram(match: re.Match) -> str:
        source = match.group(1).strip() + "\n"
        name = render_diagram(source)
        if name is None:
            failures.append(source)
            return "\n```\n" + source + "```\n"
        return f'\n<div class="diagram"><img src="img/{name}" alt="Diagramme"></div>\n'

    md_text = re.sub(r"```mermaid\s*\n(.*?)```", diagram, md_text, flags=re.S)
    return markdown.markdown(md_text, extensions=["tables", "fenced_code", "sane_lists"])


def main() -> None:
    mapping = json.loads((HELP / "help-map.json").read_text(encoding="utf-8"))
    labels = dict(re.findall(r'new\("([a-z.]+)", "(Nav\.[A-Za-z]+)"', SHELL.read_text(encoding="utf-8")))
    french = json.loads(FRENCH.read_text(encoding="utf-8-sig"))
    OUT.mkdir(parents=True, exist_ok=True)

    chapters = {}
    for key, file in mapping.items():
        chapters.setdefault(file, []).append(key)

    toc = []
    used_images = set()
    problems = []

    for file in sorted(chapters):
        source = MANUAL / file.replace(".html", ".md")
        if not source.exists():
            problems.append(f"{file}: no manual chapter {source.name}")
            continue

        failures = []
        body = convert(source.read_text(encoding="utf-8"), failures)
        problems += [f"{file}: diagram not drawn" for _ in failures]
        used_images.update(re.findall(r'src="img/([^"]+)"', body))

        # The chapter title: the manual's first heading is "## Title"; it becomes the page's h1.
        title_match = re.search(r"<h2>(.*?)</h2>", body)
        title = re.sub("<[^>]+>", "", title_match.group(1)) if title_match else file
        if title_match:
            body = body.replace(title_match.group(0), f"<h1>{title_match.group(1)}</h1>", 1)

        headings = list(re.finditer(r"<h3>(.*?)</h3>", body))
        anchors = {}
        for key in chapters[file]:
            wanted = CHAPTER_HEADING_OF.get((file, key)) or HEADING_OF.get(key) or nested(french, labels.get(key, ""))
            hit = next((h for h in headings if norm(re.sub("<[^>]+>", "", h.group(1))) == norm(wanted)), None)
            hit = hit or next((h for h in headings if norm(wanted) in norm(re.sub("<[^>]+>", "", h.group(1)))), None)
            if hit is None:
                problems.append(f"{file}: no heading for '{key}' ({wanted}); anchored at the top")
                anchors.setdefault("__top__", []).append(key)
            else:
                anchors.setdefault(hit.start(), []).append(key)

        # Insert the ids from the end so earlier offsets stay valid. One element per extra key.
        for position in sorted((p for p in anchors if p != "__top__"), reverse=True):
            keys = anchors[position]
            marker = "".join(f'<a id="{k}"></a>' for k in keys[1:])
            body = body[:position] + marker + f'<h3 id="{keys[0]}">' + body[position + 4:]
        if "__top__" in anchors:
            body = "".join(f'<a id="{k}"></a>' for k in anchors["__top__"]) + body

        crumb = '<a href="index.html">Sommaire</a>'
        (OUT / file).write_text(page(title, body + '<p class="top"><a href="index.html">Retour au sommaire</a></p>\n', crumb),
                                encoding="utf-8")

        screens = "".join(
            f'<li><a href="{file}#{k}">{html.escape(nested(french, labels.get(k, k)))}</a></li>'
            for k in chapters[file])
        toc.append(f'<li><a href="{file}"><b>{html.escape(title)}</b></a><ul>{screens}</ul></li>')
        print(f"{file}: {len(re.findall('diagram', body))} diagram(s)")

    index = ("<h1>Aide de MarocLIS</h1>\n<p>Chaque écran a sa page d'aide : appuyez sur <b>F1</b> "
             "ou cliquez <b>Aide</b> dans le bandeau pour ouvrir celle de l'écran affiché.</p>\n"
             f'<ul class="toc">{"".join(toc)}</ul>\n')
    (OUT / "index.html").write_text(page("Aide de MarocLIS", index), encoding="utf-8")

    for stale in IMG.glob("*.png") if IMG.exists() else []:
        if stale.name not in used_images:
            stale.unlink()

    for problem in problems:
        print("  ! " + problem, file=sys.stderr)


if __name__ == "__main__":
    main()
