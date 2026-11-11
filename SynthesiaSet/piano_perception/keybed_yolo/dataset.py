"""Acquire a small source inventory, then build a dataset from reviewed labels."""

import argparse
from collections import Counter
import hashlib
import io
import json
from pathlib import Path
import re
import shutil
import tempfile
from urllib.parse import urlsplit
from urllib.request import Request, urlopen

from PIL import Image, ImageDraw, ImageOps
import numpy as np

from geometry import CLASSES, CORNER_NAMES, checked_polygon, checked_quad, segment_rows

SPLITS = ("train", "val", "test")
MAX_SOURCE_BYTES = 16 * 1024 * 1024
ID = re.compile(r"[a-z0-9][a-z0-9_-]*\Z")
USER_AGENT = "AR-Musician-KeybedPilot/0.1 (https://github.com/MixedRealityETHZ/AR_Musician)"


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def sha256_file(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def indexed(rows):
    result = {}
    for row in rows:
        key = row["id"]
        if not isinstance(key, str) or not ID.fullmatch(key) or key in result:
            raise ValueError(f"invalid or duplicate id {key!r}")
        result[key] = row
    return result


def validate_sources(rows):
    rows = indexed(rows)
    groups = {}
    for key, row in rows.items():
        for field in ("group", "split", "source_url", "author", "license", "license_url"):
            if not isinstance(row.get(field), str) or not row[field].strip():
                raise ValueError(f"{key}: missing {field}")
        group, split = row["group"], row["split"]
        if split not in SPLITS or groups.setdefault(group, split) != split:
            raise ValueError(f"{key}: group {group!r} assigned to inconsistent splits")
        if bool(row.get("image_url")) == bool(row.get("image_file")):
            raise ValueError(f"{key}: supply exactly one of image_url or image_file")
        if row.get("image_url") and urlsplit(row["image_url"]).scheme != "https":
            raise ValueError(f"{key}: image URLs must use HTTPS")
    if not rows:
        raise ValueError("source manifest is empty")
    return rows


def read_source(row, manifest_dir):
    if row.get("image_url"):
        request = Request(row["image_url"], headers={"User-Agent": USER_AGENT})
        with urlopen(request, timeout=30) as response:
            if urlsplit(response.url).scheme != "https":
                raise ValueError("image redirect must use HTTPS")
            data = response.read(MAX_SOURCE_BYTES + 1)
    else:
        with (manifest_dir / row["image_file"]).open("rb") as source:
            data = source.read(MAX_SOURCE_BYTES + 1)
    if not 0 < len(data) <= MAX_SOURCE_BYTES:
        raise ValueError("empty image or source exceeds the 16 MiB pilot limit")
    if row.get("source_sha256") and sha256(data) != row["source_sha256"]:
        raise ValueError("source SHA-256 changed")
    return data


def acquire(manifest, workspace, max_edge):
    sources = validate_sources(read_json(manifest)["images"])
    if not 320 <= max_edge <= 2048:
        raise ValueError("max edge must be between 320 and 2048")
    if workspace.exists():
        raise ValueError(f"inventory already exists: {workspace}. Use a fresh workspace for a new manifest.")
    workspace.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=workspace.parent) as staging:
        target = Path(staging) / "inventory"
        images = target / "images"
        images.mkdir(parents=True)
        records, duplicates, seen = [], [], {}
        for key, source in sources.items():
            try:
                original = read_source(source, manifest.parent)
                with Image.open(io.BytesIO(original)) as opened:
                    image = ImageOps.exif_transpose(opened).convert("RGB")
                image.thumbnail((max_edge, max_edge), Image.Resampling.LANCZOS)
                width, height = image.size
                if min(width, height) < 32:
                    raise ValueError("image is smaller than 32 px on one axis")
                pixel_hash = sha256(f"{width}x{height}\0".encode() + image.tobytes())
                record = {**source, "source_sha256": sha256(original), "pixel_sha256": pixel_hash,
                          "width": width, "height": height}
                previous = seen.get(pixel_hash)
                if previous is not None:
                    if previous["split"] != source["split"] or previous["group"] != source["group"]:
                        raise ValueError(f"duplicate pixels of {previous['id']} cross a group or split")
                    duplicates.append({**record, "duplicate_of": previous["id"]})
                    continue
                image_path = images / f"{key}.png"
                image.save(image_path)
                record["image_sha256"] = sha256(image_path.read_bytes())
                seen[pixel_hash] = record
                records.append(record)
            except (OSError, ValueError) as error:
                raise ValueError(f"{key}: {error}") from error
        write_json(target / "inventory.json", {"schema": 1, "max_edge": max_edge, "images": records,
                                                 "duplicates": duplicates})
        write_json(target / "reviews.json", {"schema": 1, "images": [
            {"id": row["id"], "image_sha256": row["image_sha256"], "status": "pending",
             "reviewed_by": "", "reason": ""} for row in records
        ]})
        target.rename(workspace)
    print(f"Acquired {len(records)} unique images, {len(duplicates)} duplicates")


def image_path(workspace, row):
    path = workspace / "images" / f"{row['id']}.png"
    if sha256(path.read_bytes()) != row["image_sha256"]:
        raise ValueError(f"{row['id']}: normalized image checksum changed")
    return path


def annotate(image, quad, color="#28e3aa"):
    draw = ImageDraw.Draw(image)
    draw.line([*quad, quad[0]], width=3, fill=color)
    for (x, y), name in zip(quad, CORNER_NAMES):
        draw.ellipse((x - 4, y - 4, x + 4, y + 4), fill=color)
        draw.text((x + 5, max(0, y - 12)), name, fill=color, stroke_fill="black", stroke_width=1)


def candidate_for(proposal, review, class_name):
    proposal_id = review.get("proposal")
    if not proposal_id:
        return {}
    if proposal is None:
        raise ValueError("proposal does not match the image")
    for candidate in proposal["candidates"]:
        if candidate["id"] == proposal_id and candidate["class"] == class_name:
            return candidate
    raise ValueError(f"unknown {class_name} proposal {proposal_id!r}")


def check_duplicate_polygons(keys):
    """Reject near-identical key instances without penalizing overlap with the keybed."""
    raster = []
    for key in keys:
        polygon = key["polygon"]
        xs, ys = zip(*polygon)
        left, top = int(min(xs)), int(min(ys))
        right, bottom = int(max(xs)) + 1, int(max(ys)) + 1
        mask = Image.new("1", (right - left, bottom - top))
        ImageDraw.Draw(mask).polygon([(x - left, y - top) for x, y in polygon], fill=1)
        pixels = np.asarray(mask)
        for prior, bounds, other in raster:
            l2, t2, r2, b2 = bounds
            x0, y0, x1, y1 = max(left, l2), max(top, t2), min(right, r2), min(bottom, b2)
            if x0 >= x1 or y0 >= y1:
                continue
            intersection = np.count_nonzero(
                pixels[y0-top:y1-top, x0-left:x1-left] & other[y0-t2:y1-t2, x0-l2:x1-l2]
            )
            if intersection > 0.8 * (np.count_nonzero(pixels) + np.count_nonzero(other) - intersection):
                raise ValueError(f"slots {prior} and {key['slot']} have duplicate polygons")
        raster.append((key["slot"], (left, top, right, bottom), pixels))


def resolve_instances(row, review, proposal):
    if review["status"] != "accepted":
        if "keybed" in review or "keys" in review:
            raise ValueError("only accepted images may contain instance labels")
        if review["status"] == "negative" and review.get("contains_visible_target") is not False:
            raise ValueError("negative images require explicit confirmation of no visible target")
        return []
    width, height = row["width"], row["height"]
    bed = review.get("keybed", {})
    candidate = candidate_for(proposal, bed, CLASSES[0])
    corners = checked_quad(bed.get("corners", candidate.get("corners")), width, height)
    count, whites = review.get("expected_key_count"), review.get("expected_white_count")
    if type(count) is not int or type(whites) is not int or not 0 < whites < count <= 128:
        raise ValueError("record complete visible key count and white-key count")
    keys = []
    for key in review.get("keys", []):
        class_name = key.get("class")
        if class_name not in CLASSES[1:] or type(key.get("slot")) is not int:
            raise ValueError("each key needs white_key or black_key and its integer physical slot")
        candidate = candidate_for(proposal, key, class_name)
        polygon = checked_polygon(key.get("polygon", candidate.get("polygon")), width, height)
        keys.append({"slot": key["slot"], "class": class_name, "polygon": polygon})
    keys.sort(key=lambda key: key["slot"])
    if [key["slot"] for key in keys] != list(range(count)) or sum(key["class"] == "white_key" for key in keys) != whites:
        raise ValueError("key slots and white/black count do not cover the complete reviewed keyboard")
    check_duplicate_polygons(keys)
    return [{"class": CLASSES[0], "polygon": corners}, *keys]


def reviewed_labels(workspace):
    inventory = read_json(workspace / "inventory.json")
    records = validate_sources(inventory["images"])
    reviews = indexed(read_json(workspace / "reviews.json")["images"])
    if records.keys() != reviews.keys():
        raise ValueError("review ids must match the inventory")
    proposal_file = workspace / "proposals" / "index.json"
    proposals = indexed(read_json(proposal_file)["images"]) if proposal_file.exists() else {}
    for key, row in records.items():
        review = reviews[key]
        path = image_path(workspace, row)
        if review["image_sha256"] != row["image_sha256"]:
            raise ValueError(f"{key}: review does not match the image checksum")
        status = review.get("status")
        reviewer = review.get("reviewed_by")
        if status not in ("accepted", "negative", "rejected") or not isinstance(reviewer, str) or not reviewer.strip():
            raise ValueError(f"{key}: review is pending")
        reason = review.get("reason")
        if not isinstance(reason, str) or not reason.strip():
            raise ValueError(f"{key}: review must record a reason")
        proposal = proposals.get(key)
        if proposal is not None and proposal["image_sha256"] != row["image_sha256"]:
            raise ValueError(f"{key}: proposal does not match the image checksum")
        try:
            instances = resolve_instances(row, review, proposal)
        except ValueError as error:
            raise ValueError(f"{key}: {error}") from error
        yield row, review, path, instances


def dataset_yaml(destination):
    # Quoted JSON strings are valid YAML scalars, including Windows paths with spaces.
    return (f"path: {json.dumps(destination.resolve().as_posix())}\n"
            "train: images/train\nval: images/val\ntest: images/test\n"
            "names: {0: playable_keybed, 1: white_key, 2: black_key}\n")


def pack(workspace, destination):
    labels = list(reviewed_labels(workspace))
    positives = Counter(row["split"] for row, _, _, instances in labels if instances)
    if not all(positives[split] for split in SPLITS):
        raise ValueError("need at least one reviewed positive image in each of train, val, and test")
    if destination.exists():
        raise ValueError(f"dataset already exists: {destination}")
    destination.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=destination.parent) as staging:
        target = Path(staging) / "dataset"
        provenance = []
        for row, review, path, instances in labels:
            key, split = row["id"], row["split"]
            provenance.append({**row, "review": review, "instances": instances})
            if review["status"] == "rejected":
                continue
            images, label_dir = target / "images" / split, target / "labels" / split
            images.mkdir(parents=True, exist_ok=True)
            label_dir.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(path, images / path.name)
            label = segment_rows(instances, row["width"], row["height"])
            (label_dir / f"{key}.txt").write_text(label, encoding="utf-8")
            if instances:
                with Image.open(path) as original:
                    overlay = original.copy()
                draw = ImageDraw.Draw(overlay)
                for instance in instances[1:]:
                    polygon = instance["polygon"]
                    color = "#f6b955" if instance["class"] == "white_key" else "#ff7aa8"
                    draw.line([*polygon, polygon[0]], width=2, fill=color)
                    draw.text(polygon[0], str(instance["slot"]), fill=color, stroke_fill="black", stroke_width=1)
                annotate(overlay, instances[0]["polygon"])
                (target / "reviewed").mkdir(exist_ok=True)
                overlay.save(target / "reviewed" / path.name)
        shutil.copyfile(workspace / "inventory.json", target / "inventory.json")
        proposal_file = workspace / "proposals" / "index.json"
        if proposal_file.exists():
            shutil.copyfile(proposal_file, target / "proposals.json")
        write_json(target / "provenance.json", {"schema": 1, "images": provenance})
        (target / "keybed.yaml").write_text(dataset_yaml(destination), encoding="utf-8")
        target.rename(destination)
    print(f"Packed reviewed positives by split: {dict(positives)}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    download = commands.add_parser("acquire")
    download.add_argument("manifest", type=Path)
    download.add_argument("workspace", type=Path)
    download.add_argument("--max-edge", type=int, default=1024)
    build = commands.add_parser("pack")
    build.add_argument("workspace", type=Path)
    build.add_argument("destination", type=Path)
    args = parser.parse_args()
    if args.command == "acquire":
        acquire(args.manifest, args.workspace, args.max_edge)
    else:
        pack(args.workspace, args.destination)


if __name__ == "__main__":
    main()
