"""Local, manual PDF-to-text preparation. Does not call external services."""
import argparse
import hashlib
import json
import re
from pathlib import Path

import pdfplumber
import pypdfium2 as pdfium
from PIL import Image, ImageDraw


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--first-page-split", type=float, default=None,
                        help="Verified first-page column boundary in PDF points.")
    parser.add_argument("--candidate-name", default=None, help="Name to remove from the matching draft.")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    pages = []
    matching_pages = []
    with pdfplumber.open(args.source) as pdf:
        for number, page in enumerate(pdf.pages, 1):
            content = page.extract_text(x_tolerance=2, y_tolerance=3) or ""
            if not content.strip():
                raise ValueError(f"Page {number} has no extractable text; manual OCR review is needed.")
            pages.append(content)
            if number == 1 and args.first_page_split is not None:
                split = args.first_page_split
                if not 0 < split < page.width:
                    raise ValueError("Invalid first-page column boundary.")
                # This template has a header at 0–110 and page footer below height–40.
                header = page.crop((0, 0, split * 2, 110)).extract_text() or ""
                left = page.crop((0, 110, split, page.height - 40)).extract_text() or ""
                right = page.crop((split, 110, page.width, page.height - 40)).extract_text() or ""
                matching_pages.append(header + "\n\n### Roller, tekniker och arbetssätt\n\n" + left
                                      + "\n\n### Profil och utvalda uppdrag\n\n" + right)
            else:
                matching_pages.append(page.crop((0, 0, page.width, page.height - 40)).extract_text(
                    x_tolerance=2, y_tolerance=3) or "")
    raw = "# CV - lokal textextraktion\n\nOriginalets läsordning behöver granskas.\n\n" + "\n\n".join(
        f"## Sida {i}\n\n{text}" for i, text in enumerate(pages, 1)
    ) + "\n"
    (args.output / "cv-extracted.md").write_text(raw, encoding="utf-8")
    matching = "\n\n".join(f"## Källsida {i}\n\n{text}" for i, text in enumerate(matching_pages, 1))
    matching = re.sub(r"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}", "[e-post borttagen]", matching)
    matching = re.sub(r"(?<!\w)(?:\+46\s?\(0\)?|\+46|0)7\d[\d\s()-]{6,}\d(?!\w)",
                      "[telefon borttagen]", matching)
    if args.candidate_name:
        matching = matching.replace(args.candidate_name, "Kandidaten")
        first_name = args.candidate_name.split()[0]
        matching = re.sub(r"\b" + re.escape(first_name) + r"\b", "Kandidaten", matching)
    matching = "# CV-underlag för matchning - utkast\n\n" + (
        "Lokalt extraherat från PDF. Behöver granskas mot originalet innan extern analys.\n"
        "Kontaktuppgifter, namn och sidfötter har minimerats. Uppdrag och självskattade\n"
        "kompetensnivåer återges som underlag; texten är ingen oberoende verifiering.\n"
        "Källsidor bevaras för spårbarhet. Ingen information har skickats till OpenAI.\n\n"
    ) + matching + "\n"
    (args.output / "cv-matching-draft.md").write_text(matching, encoding="utf-8")
    document = pdfium.PdfDocument(str(args.source))
    thumbnails = []
    for i in range(len(document)):
        page = document[i]
        bitmap = page.render(scale=1.25)
        rendered = bitmap.to_pil().convert("RGB")
        rendered.save(args.output / f"page-{i+1:02d}.png")
        thumbnail = rendered.copy()
        thumbnail.thumbnail((370, 525))
        cell = Image.new("RGB", (390, 560), "#e9e9e9")
        cell.paste(thumbnail, ((390-thumbnail.width)//2, 25))
        ImageDraw.Draw(cell).text((12, 5), f"Page {i+1}", fill="black")
        thumbnails.append(cell)
        bitmap.close()
        page.close()
    sheet = Image.new("RGB", (390*3, 560*((len(thumbnails)+2)//3)), "white")
    for i, cell in enumerate(thumbnails):
        sheet.paste(cell, ((i%3)*390, (i//3)*560))
    sheet.save(args.output / "pages-overview.png")
    manifest = {
        "sourceSha256": hashlib.sha256(args.source.read_bytes()).hexdigest(),
        "pageCount": len(pages),
        "charactersPerPage": [len(text) for text in pages],
        "reviewedByUser": False,
        "externalAnalysisEnabled": False,
        "matchingDraftSha256": hashlib.sha256(matching.encode("utf-8")).hexdigest(),
        "firstPageColumnBoundary": args.first_page_split,
    }
    (args.output / "preparation.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    # Never print CV text or contact details.
    print(f"Extracted {len(pages)} pages locally; {sum(map(len, pages))} characters. No external calls.")


if __name__ == "__main__":
    main()
