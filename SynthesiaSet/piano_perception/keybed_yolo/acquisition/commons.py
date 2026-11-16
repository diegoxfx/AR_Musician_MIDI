"""Discover licensed Commons photos and prepare locally reviewed source manifests."""

import argparse
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import hashlib
from html.parser import HTMLParser
import io
import json
from pathlib import Path
import re
import time
from urllib.error import HTTPError
from urllib.parse import urlencode, urlsplit
from urllib.request import Request, urlopen

import numpy as np
from PIL import Image, ImageDraw, ImageOps

API = "https://commons.wikimedia.org/w/api.php"
USER_AGENT = "AR-Musician-KeybedPilot/0.2 (https://github.com/MixedRealityETHZ/AR_Musician)"
MAX_BYTES = 16 * 1024 * 1024
ROOTS = (
    "Electronic keyboards", "Digital pianos", "MIDI keyboard controllers",
    "Piano keyboards", "Home keyboards", "Arranger keyboards", "Arranger Workstations",
    "Casiotone", "Stage pianos", "Electric pianos", "Electronic pianos",
    "Synthesizers", "Music workstations", "Upright pianos", "Grand pianos",
)
SKIP_CATEGORY = re.compile(
    r"\b(accordions?|advertisements?|art|cases?|circuits?|diagrams?|documents?|"
    r"drums?|events?|factories|icons?|keytar|keytarists|logos?|manuals?|"
    r"manufacturers|midi bandoneons|musicians|organs?|parts|pedals?|people|"
    r"pianists|piano rolls|piano keys|serial numbers|sounds?|stools|synthesizer modules|"
    r"unidentified|videos?)\b", re.I
)
SKIP_TITLE = re.compile(
    r"(\b(rear|back|bottom|inside|interior|schematic|diagram|disassembly|"
    r"manual|brochure|control panel|logo|serial number|input jacks|output jacks)\b"
    r"|\b(close[ -]?up|details?|icon)\b)", re.I
)
LICENSE = re.compile(r"^(CC BY(?:-SA)? [234]\.\d|CC0|Public domain|PDM)", re.I)
RASTER = (".jpg", ".jpeg", ".png", ".webp")


class PlainText(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.parts = []

    def handle_data(self, data):
        self.parts.append(data)


def plain(html):
    parser = PlainText()
    parser.feed(html)
    return " ".join("".join(parser.parts).split())


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write_json(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    pending = path.with_suffix(path.suffix + ".tmp")
    pending.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    pending.replace(path)


def fetch(url, retries=3):
    for attempt in range(retries):
        try:
            request = Request(url, headers={"User-Agent": USER_AGENT})
            with urlopen(request, timeout=22) as response:
                data = response.read(MAX_BYTES + 1)
            if not 0 < len(data) <= MAX_BYTES:
                raise ValueError(f"unexpected response size {len(data)}")
            return data
        except OSError as error:
            if isinstance(error, HTTPError) and error.code not in {429, 500, 502, 503, 504}:
                raise
            if attempt == retries - 1:
                raise
            retry = error.headers.get("Retry-After") if isinstance(error, HTTPError) else None
            time.sleep(min(30, int(retry)) if retry and retry.isdigit() else 2 ** attempt)


def category_pages(root, category):
    key = hashlib.sha256(category.encode()).hexdigest()[:16]
    cache = root / "api" / f"{key}.json"
    if cache.exists():
        return read_json(cache)["pages"]
    params = {
        "action": "query", "format": "json", "formatversion": 2, "maxlag": 5,
        "generator": "categorymembers", "gcmtitle": f"Category:{category}",
        "gcmtype": "file|subcat", "gcmlimit": 40,
        "prop": "imageinfo", "iiprop": "url|size|extmetadata", "iiurlwidth": 960,
        "iiextmetadatafilter": "LicenseShortName|LicenseUrl|Artist|DateTimeOriginal|ImageDescription|Credit",
    }
    pages = []
    continuation = {}
    while True:
        result = json.loads(fetch(f"{API}?{urlencode(params | continuation)}"))
        if "error" in result:
            raise ValueError(result["error"])
        pages.extend(result.get("query", {}).get("pages", []))
        continuation = result.get("continue")
        if not continuation:
            break
    write_json(cache, {"category": category, "pages": pages})
    return pages


def clean_url(url):
    return urlsplit(url)._replace(query="", fragment="").geturl()


def candidate(page, category):
    title = page["title"]
    versions = page.get("imageinfo", [])
    if not title.lower().endswith(RASTER) or SKIP_TITLE.search(title) or not versions:
        return
    info = versions[0]
    if not info.get("url"):
        return
    if info["width"] < 400 or info["height"] < 80:
        return
    metadata = info.get("extmetadata", {})
    def value(key):
        return metadata.get(key, {}).get("value", "")
    license_name = plain(value("LicenseShortName"))
    if not LICENSE.match(license_name):
        return
    source_url = info["descriptionurl"]
    author = plain(value("Artist"))
    license_url = value("LicenseUrl")
    if not license_url and license_name.lower().startswith(("public domain", "pdm")):
        license_url = source_url
    if not author or not license_url:
        return
    if license_url.startswith("//"):
        license_url = "https:" + license_url
    original = clean_url(info["url"])
    thumb = clean_url(info.get("thumburl", original))
    return {
        "id": f"wc-{page['pageid']}", "title": title.removeprefix("File:"),
        "source_url": source_url, "original_url": original, "thumbnail_url": thumb,
        "author": author, "license": license_name, "license_url": license_url,
        "capture_date": plain(value("DateTimeOriginal")),
        "description": plain(value("ImageDescription"))[:2000],
        "credit": plain(value("Credit"))[:500],
        "original_width": info["width"], "original_height": info["height"],
        "original_bytes": info["size"], "categories": [category],
    }


def discover(root, roots, depth, limit):
    output = root / "candidates.json"
    prior = read_json(output) if output.exists() else {}
    rows = {row["id"]: row for row in prior.get("images", [])}
    categories = [(category, 0) for category in roots]
    seen = set()
    errors = {}
    with ThreadPoolExecutor(max_workers=3) as executor:
        while categories and len(seen) < limit:
            batch = []
            while categories and len(batch) < 3 and len(seen) < limit:
                category, level = categories.pop(0)
                if category not in seen:
                    seen.add(category)
                    batch.append((category, level, executor.submit(category_pages, root, category)))
            for category, level, future in batch:
                try:
                    pages = future.result()
                except (OSError, ValueError) as error:
                    errors[category] = str(error)
                    continue
                for page in pages:
                    if page["ns"] == 14 and level < depth:
                        subcategory = page["title"].removeprefix("Category:")
                        if not SKIP_CATEGORY.search(subcategory):
                            categories.append((subcategory, level + 1))
                    elif page["ns"] == 6:
                        row = candidate(page, category)
                        if row and row["id"] in rows:
                            existing = rows[row["id"]]["categories"]
                            if category not in existing:
                                existing.append(category)
                        elif row:
                            rows[row["id"]] = row
                print(f"{category}: {len(rows)} candidates total", flush=True)
            write_json(output, {"images": list(rows.values()), "errors": errors})


def image_from_bytes(data):
    with Image.open(io.BytesIO(data)) as opened:
        return ImageOps.exif_transpose(opened).convert("RGB")


def thumbnail(root, row):
    preview = root / "previews" / f"{row['id']}.jpg"
    if preview.exists():
        return row["id"], None
    thumb = row["thumbnail_url"]
    original = row["original_url"]
    urls = [thumb]
    if original != thumb and row["original_bytes"] <= 4 * 1024 * 1024:
        urls.append(original)
    for url in urls:
        try:
            data = fetch(url)
            image = image_from_bytes(data)
            image.thumbnail((480, 320), Image.Resampling.LANCZOS)
            staging = preview.with_suffix(".tmp")
            image.save(staging, format="JPEG", quality=86)
            staging.replace(preview)
            return row["id"], None
        except (OSError, ValueError) as error:
            problem = str(error)
    return row["id"], problem


def previews(root, start, stop, limit):
    rows = read_json(root / "candidates.json")["images"][start:stop]
    (root / "previews").mkdir(exist_ok=True)
    log = root / "preview-failures.json"
    failures = read_json(log) if log.exists() else {}
    pending = [row for row in rows if not (root / "previews" / f"{row['id']}.jpg").exists()]
    with ThreadPoolExecutor(max_workers=4) as executor:
        for count, (key, problem) in enumerate(
            executor.map(lambda row: thumbnail(root, row), pending[:limit]), 1
        ):
            if problem:
                failures[key] = problem
            else:
                failures.pop(key, None)
            if count % 50 == 0:
                print(f"{count} attempts, {len(failures)} failures", flush=True)
    write_json(log, failures)


def sheets(root, start, stop):
    rows = read_json(root / "candidates.json")["images"]
    grid = root / "sheets"
    grid.mkdir(exist_ok=True)
    columns, per_sheet = 5, 30
    width, height = 300, 181
    for offset in range(start, min(len(rows), stop), per_sheet):
        canvas = Image.new("RGB", (columns * width, per_sheet // columns * height), "#171b20")
        draw = ImageDraw.Draw(canvas)
        for index, row in enumerate(rows[offset:offset + per_sheet], offset + 1):
            cell = index - offset - 1
            left, top = cell % columns * width, cell // columns * height
            path = root / "previews" / f"{row['id']}.jpg"
            if path.exists():
                with Image.open(path) as source:
                    source.thumbnail((width - 6, height - 30), Image.Resampling.LANCZOS)
                    canvas.paste(source, (left + (width - source.width) // 2, top))
            draw.text((left + 5, top + height - 27), f"{index} | {row['title'][:45]}", fill="white")
        canvas.save(grid / f"{offset + 1:04}.jpg", quality=89)


def phash(image):
    grayscale = np.asarray(image.convert("L").resize((32, 32), Image.Resampling.LANCZOS), dtype=float)
    positions = np.arange(32) + 0.5
    basis = np.cos(np.pi / 32 * np.arange(8)[:, None] * positions)
    low = (basis @ grayscale @ basis.T).ravel()[1:]
    return int.from_bytes(np.packbits(low > np.median(low)).tobytes(), "big")


def full_photo(root, row):
    thumb = row["thumbnail_url"]
    original = row["original_url"]
    high_resolution = thumb
    if row["original_width"] > 1280:
        high_resolution = thumb.replace("/960px-", "/1280px-")
    urls = [original] if row["original_bytes"] <= 1_000_000 else [high_resolution]
    if original not in urls and row["original_bytes"] <= 8 * 1024 * 1024:
        urls.append(original)
    if thumb not in urls:
        urls.append(thumb)
    for url in urls:
        try:
            data = fetch(url)
            image = image_from_bytes(data)
            break
        except (OSError, ValueError) as error:
            problem = str(error)
    else:
        raise ValueError(problem)
    raw_hash = hashlib.sha256(data).hexdigest()
    suffix = Path(urlsplit(url).path).suffix.lower()
    file = root / "photos" / f"{row['id']}{suffix}"
    file.write_bytes(data)
    return {
        **row, "image_file": file.relative_to(root).as_posix(), "retrieval_url": url,
        "source_sha256": raw_hash, "width": image.width, "height": image.height,
        "perceptual_hash": f"{phash(image):016x}",
        "mirrored_perceptual_hash": f"{phash(ImageOps.mirror(image)):016x}",
        "pixel_sha256": hashlib.sha256(f"{image.width}x{image.height}\0".encode() + image.tobytes()).hexdigest(),
    }


def selection(root, decision_file):
    rows = read_json(root / "candidates.json")["images"]
    decisions = []
    seen = set()
    for line in decision_file.read_text(encoding="utf-8-sig").splitlines():
        line = line.partition("#")[0].strip()
        if not line:
            continue
        fields = line.split()
        if len(fields) not in {2, 3} or fields[1] not in {"full", "challenge"}:
            raise ValueError(f"expected index, full/challenge, optional group: {line}")
        index = int(fields[0])
        if not 1 <= index <= len(rows) or index in seen:
            raise ValueError(f"invalid or duplicate index: {index}")
        seen.add(index)
        row = rows[index - 1]
        artist = re.split(r"\s*\((?:talk|discussion)\)", row["author"], maxsplit=1, flags=re.I)[0]
        author = re.sub(r"\W+", "-", artist.casefold()).strip("-")
        group = fields[2] if len(fields) == 3 else f"commons-author-{author}"
        decisions.append({**row, "candidate_index": index, "photo_review": fields[1], "group": group})
    return decisions


def collect(root, decision_file):
    decisions = selection(root, decision_file)
    photos = root / "photos"
    photos.mkdir(exist_ok=True)
    inventory = root / "downloaded.json"
    prior = read_json(inventory) if inventory.exists() else {}
    cached = {row["id"]: row for row in prior.get("images", [])}

    def get(row):
        earlier = cached.get(row["id"])
        if earlier:
            path = root / earlier["image_file"]
            if path.exists() and hashlib.sha256(path.read_bytes()).hexdigest() == earlier["source_sha256"]:
                return earlier | row, None
        try:
            return full_photo(root, row), None
        except (OSError, ValueError) as error:
            return row, str(error)

    acquired, failures = [], {}
    with ThreadPoolExecutor(max_workers=4) as executor:
        for row, error in executor.map(get, decisions):
            if error:
                failures[row["id"]] = error
            else:
                acquired.append(row)
    write_json(inventory, {"images": acquired, "errors": failures})
    print(f"Downloaded {len(acquired)} reviewed candidates, {len(failures)} failures")


def publish(root, near_distance, suffix, base_manifest):
    source = read_json(root / "downloaded.json")
    base = read_json(base_manifest)["images"] if base_manifest else []
    base_ids = {row["id"] for row in base}
    candidates = sorted(
        source["images"],
        key=lambda row: (row["id"] not in base_ids, row["photo_review"] != "full", -row["width"], row["id"]),
    )
    kept, duplicates, near = [], [], []
    for row in candidates:
        exact = next((other for other in kept if row["pixel_sha256"] == other["pixel_sha256"]), None)
        if exact:
            duplicates.append({"id": row["id"], "duplicate_of": exact["id"], "type": "exact"})
            continue
        hash_int = int(row["perceptual_hash"], 16)
        related = []
        for other in kept:
            distance = min(
                (hash_int ^ int(other[key], 16)).bit_count()
                for key in ("perceptual_hash", "mirrored_perceptual_hash")
            )
            if distance <= near_distance:
                related.append((distance, other))
        if related:
            distance, other = min(related, key=lambda pair: pair[0])
            near.append({"id": row["id"], "other": other["id"], "phash_distance": distance})
        kept.append(row)
    write_json(root / f"duplicate-review{suffix}.json", {"exact": duplicates, "suspected": near})
    blocked = {pair["id"] for pair in near}
    kept = [row for row in kept if row["id"] not in blocked]
    groups = sorted(
        {row["group"] for row in kept if row["photo_review"] == "full"},
        key=lambda group: hashlib.sha256(group.encode()).digest(),
    )
    count = max(1, round(len(groups) * 0.12)) if len(groups) >= 3 else 0
    split = {group: "test" if index < count else "val" if index < 2 * count else "train"
             for index, group in enumerate(groups)}
    split.update({row["group"]: row["split"] for row in base})
    full = [{**row, "split": split[row["group"]]} for row in kept if row["photo_review"] == "full"]
    by_id = {row["id"]: row for row in full}
    for row in base:
        updated = by_id.get(row["id"], {})
        if any(updated.get(field) != row[field] for field in ("group", "split", "source_sha256")):
            raise ValueError(f"cannot extend the base manifest because {row['id']} changed")
    challenge = [row for row in kept if row["photo_review"] == "challenge"]
    write_json(root / f"manifest{suffix}.json", {
        "schema": 1, "collected_utc": datetime.now(timezone.utc).isoformat(), "images": full,
        "source_review": "Visible keyboard extent and photo authenticity checked; instance labels pending.",
    })
    write_json(root / f"challenge-manifest{suffix}.json", {"schema": 1, "images": challenge})
    print(f"{len(full)} complete, {len(challenge)} challenge, {len(duplicates)} exact, "
          f"{len(near)} held for perceptual duplicate review")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path)
    actions = parser.add_subparsers(dest="action", required=True)
    discovery = actions.add_parser("discover")
    discovery.add_argument("--roots", nargs="+", default=ROOTS)
    discovery.add_argument("--depth", type=int, default=1)
    discovery.add_argument("--limit", type=int, default=120)
    preview = actions.add_parser("previews")
    preview.add_argument("--start", type=int, default=0)
    preview.add_argument("--stop", type=int)
    preview.add_argument("--limit", type=int, default=1000)
    sheet = actions.add_parser("sheets")
    sheet.add_argument("--start", type=int, default=0)
    sheet.add_argument("--stop", type=int, default=10000)
    download = actions.add_parser("collect")
    download.add_argument("decisions", type=Path)
    manifest = actions.add_parser("publish")
    manifest.add_argument("--near-distance", type=int, default=5)
    manifest.add_argument("--version", help="suffix for a separate manifest snapshot")
    manifest.add_argument("--base-manifest", type=Path, help="preserve the source and split of each base photo")
    args = parser.parse_args()
    args.root.mkdir(parents=True, exist_ok=True)
    if args.action == "discover":
        discover(args.root, args.roots, args.depth, args.limit)
    elif args.action == "previews":
        previews(args.root, args.start, args.stop, args.limit)
    elif args.action == "sheets":
        sheets(args.root, args.start, args.stop)
    elif args.action == "collect":
        collect(args.root, args.decisions)
    else:
        if args.version and not re.fullmatch(r"[a-z0-9][a-z0-9-]*", args.version):
            parser.error("version must contain only lowercase letters, numbers, and hyphens")
        if not 0 <= args.near_distance <= 63:
            parser.error("near-distance must be between 0 and 63")
        suffix = f"-{args.version}" if args.version else ""
        publish(args.root, args.near_distance, suffix, args.base_manifest)


if __name__ == "__main__":
    main()
