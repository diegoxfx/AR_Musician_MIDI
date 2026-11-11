"""Build inspectable key-mask drafts and apply explicit image review decisions."""

import argparse
from collections import Counter
from contextlib import contextmanager
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, HTTPServer
import itertools
import json
from math import ceil
import os
from pathlib import Path
import tempfile
from urllib.parse import urlsplit

import cv2
import numpy as np
from PIL import Image, ImageDraw

from dataset import image_path, indexed, read_json, resolve_instances, write_json
from geometry import CLASSES, checked_quad
from propose import quad_from_mask
from tiled_proposals import project, rectify

COLORS = {CLASSES[0]: "#40ffd0", CLASSES[1]: "#ffd65a", CLASSES[2]: "#ff78b4"}


def key_evidence(item, forward, bounds):
    x, y, width, height = bounds
    contour = project(item["polygon"], forward)
    minimum, maximum = contour.min(axis=0), contour.max(axis=0)
    left, top = minimum - [x, y]
    right, bottom = maximum - [x, y]
    if left < -0.025*width or right > 1.025*width or top < -0.3*height or bottom > 1.3*height:
        return None
    white = item["class"] == CLASSES[1]
    if (white and (top > 0.70*height or bottom < 0.67*height) or
            not white and (top > 0.58*height or bottom < 0.18*height or bottom > 0.95*height)):
        return None
    if right - left > 0.14 * width:
        return None
    center = (left + right) / 2
    breadth = right - left
    if white:
        # White masks can wrap around a black key. Measure their x position near
        # the front edge, where two physical white keys are always separated.
        scan_y = min(0.83 * height, (bottom + max(top, 0.59 * height)) / 2) + y
        intersections = []
        for a, b in zip(contour, np.roll(contour, -1, axis=0)):
            if (a[1] <= scan_y < b[1]) or (b[1] <= scan_y < a[1]):
                intersections.append(a[0] + (scan_y - a[1]) * (b[0] - a[0]) / (b[1] - a[1]))
        if len(intersections) >= 2:
            left_front, right_front = min(intersections), max(intersections)
            center = (left_front + right_front) / 2 - x
            breadth = right_front - left_front
    origin = np.floor(minimum).astype(int)
    end = np.ceil(maximum).astype(int) + 1
    mask = np.zeros((end[1]-origin[1], end[0]-origin[0]), dtype=np.uint8)
    cv2.fillPoly(mask, [np.rint(contour - origin).astype(np.int32)], 1)
    return {"item": item, "center": float(center), "width": float(breadth), "mask": mask,
            "origin": origin, "end": end, "area": int(mask.sum()),
            "quality": float(item.get("score", 1) * item.get("mask_shape_iou", 1))}


def shared_pixels(a, b):
    start = np.maximum(a["origin"], b["origin"])
    end = np.minimum(a["end"], b["end"])
    if np.any(start >= end):
        return 0

    def crop(candidate):
        left, top = start - candidate["origin"]
        right, bottom = end - candidate["origin"]
        return candidate["mask"][top:bottom, left:right]

    return int(np.count_nonzero(crop(a) & crop(b)))


def duplicate(a, b):
    if abs(a["center"] - b["center"]) > max(a["width"], b["width"]):
        return False
    shared = shared_pixels(a, b)
    iou = shared / (a["area"] + b["area"] - shared)
    if iou > 0.54:
        return True
    return (a["item"]["class"] == b["item"]["class"] and
            shared > 0.79 * min(a["area"], b["area"]) and
            abs(a["center"] - b["center"]) < 0.46 * min(a["width"], b["width"]))


def overlap_statistics(keys):
    same_class, black_coverage = [], []
    for (first, a), (second, b) in itertools.combinations(enumerate(keys), 2):
        shared = shared_pixels(a, b)
        if not shared:
            continue
        class_a, class_b = a["item"]["class"], b["item"]["class"]
        if class_a == class_b:
            same_class.append((shared / min(a["area"], b["area"]), first, second))
        else:
            black = a if class_a == CLASSES[2] else b
            black_coverage.append((shared / black["area"], first, second))
    return {"worst_same_class": sorted(same_class, reverse=True)[:5],
            "worst_black_covered_by_white": sorted(black_coverage, reverse=True)[:5]}


def black_key_warnings(keys, white_keys, pitch):
    if len(white_keys) < 9 or pitch <= 0:
        return []
    white_centers = np.array([key["center"] for _, key in white_keys])
    if np.any(np.abs(np.diff(white_centers) / pitch - 1) > 0.38):
        return []
    seams = (white_centers[1:] + white_centers[:-1]) / 2
    occupied = {}
    warnings = []
    for slot, key in enumerate(keys):
        if key["item"]["class"] != CLASSES[2]:
            continue
        distance = np.abs(seams - key["center"])
        seam = int(distance.argmin())
        if distance[seam] > 0.52 * pitch:
            warnings.append(f"Check black candidate {slot} against the white-key seams")
            continue
        if seam in occupied:
            warnings.append(f"Black candidates {occupied[seam]} and {slot} occupy the same white-key interval")
        occupied[seam] = slot
    observed = set(occupied)
    phases = []
    pattern = (True, True, False, True, True, True, False)
    for phase in range(len(pattern)):
        expected = {seam for seam in range(len(seams)) if pattern[(phase + seam) % len(pattern)]}
        phases.append((len(expected ^ observed), expected))
    best, alternative = sorted(phases, key=lambda hypothesis: hypothesis[0])[:2]
    errors, expected = best
    if errors and errors <= 0.20 * len(seams) and alternative[0] >= errors + 2:
        for seam in sorted(expected - observed):
            first, last = white_keys[seam][0], white_keys[seam+1][0]
            warnings.append(f"Inspect the potential black key between white candidates {first} and {last}")
        for seam in sorted(observed - expected):
            warnings.append(f"Inspect black candidate {occupied[seam]} against the adjacent key pattern")
    return warnings


def assemble_candidates(candidates, forward, bounds):
    raw = [key_evidence(item, forward, bounds) for item in candidates
           if item["class"] in CLASSES[1:] and "polygon" in item and "rejection" not in item]
    good = []
    duplicates = 0
    for key in sorted((key for key in raw if key is not None), key=lambda key: -key["quality"]):
        if any(duplicate(key, prior) for prior in good):
            duplicates += 1
            continue
        good.append(key)
    whites = sorted(key["width"] for key in good if key["item"]["class"] == CLASSES[1])
    width = float(np.median(whites)) if whites else 0
    filtered = [key for key in good if 0.25*width <= key["width"] <= 1.9*width] if width else good
    filtered.sort(key=lambda key: key["center"])
    keys = [{"slot": index, "class": key["item"]["class"], "polygon": key["item"]["polygon"],
             "proposal": key["item"]["id"], "proposal_score": key["item"].get("score")}
            for index, key in enumerate(filtered)]
    white_keys = [(index, key) for index, key in enumerate(filtered) if key["item"]["class"] == CLASSES[1]]
    warnings = []
    pitch = float(np.median(np.diff([key["center"] for _, key in white_keys]))) if len(white_keys) > 2 else width
    if pitch > 0:
        for (first, a), (second, b) in itertools.pairwise(white_keys):
            gap = (b["center"] - a["center"]) / pitch
            if not 0.63 <= gap <= 1.48:
                warnings.append(f"White candidates {first}–{second} are {gap:.2f} typical spacings apart")
        if white_keys[0][1]["center"] > 0.95*pitch or bounds[2] - white_keys[-1][1]["center"] > 0.95*pitch:
            warnings.append("Check the first and last white key at the keybed ends")
        warnings.extend(black_key_warnings(filtered, white_keys, pitch))
    counts = Counter(key["class"] for key in keys)
    for class_name in CLASSES[1:]:
        if not counts[class_name]:
            warnings.append(f"No usable {class_name.replace('_', ' ')} candidate")
    overlap = overlap_statistics(filtered)
    for fraction, a, b in overlap["worst_black_covered_by_white"]:
        if fraction > 0.12:
            warnings.append(f"Candidates {a} and {b} overlap {fraction:.0%} of the black mask")
    for fraction, a, b in overlap["worst_same_class"]:
        if fraction > 0.16:
            warnings.append(f"Candidates {a} and {b} overlap {fraction:.0%} of the smaller same-class mask")
    return keys, {"counts": dict(counts), "deduplicated": duplicates, "geometry_filtered": len(raw)-len(filtered)-duplicates,
                  "overlap": overlap, "warnings": warnings}


def visual_tile(original, forward, keys, bounds, left, right, filled=False):
    region = (left, 0, right, original.height)
    overlay = original.crop(region)
    draw = ImageDraw.Draw(overlay)
    x, y, width, height = bounds
    draw.rectangle((x-left, y, x+width-left, y+height), outline=COLORS[CLASSES[0]], width=2)
    for key in keys:
        if not filled:
            continue
        polygon = project(key["polygon"], forward)
        if polygon[:, 0].max() < left or polygon[:, 0].min() >= right:
            continue
        points = [(float(px) - left, float(py)) for px, py in polygon]
        paint = ImageDraw.Draw(overlay, "RGBA")
        color = (255, 215, 90, 104) if key["class"] == CLASSES[1] else (255, 120, 180, 104)
        paint.polygon(points, fill=color)
    for key in keys:
        polygon = project(key["polygon"], forward)
        if polygon[:, 0].max() < left or polygon[:, 0].min() >= right:
            continue
        points = [(float(px) - left, float(py)) for px, py in polygon]
        color = COLORS[key["class"]]
        draw.line([*points, points[0]], fill=color, width=1)
        x_text = sum(point[0] for point in points) / len(points)
        y_text = max(point[1] for point in points) - 12
        draw.text((x_text, y_text), str(key["slot"]), fill=color, stroke_width=1, stroke_fill="black")
    return original.crop(region), overlay


def save_visuals(image, row, keys, corners, output, filled=False):
    original, forward, bounds = rectify(image, corners)
    x, _, width, _ = bounds
    stride = ceil(width / ceil(width / 420))
    regions = [(max(0, x0-14), min(original.width, x0+stride+14))
               for x0 in range(x, x+width, stride)]
    panels = [visual_tile(original, forward, keys, bounds, left, right, filled) for left, right in regions]
    panel_width = max(plain.width for plain, _ in panels)
    banner = 28
    sheet = Image.new("RGB", (panel_width*2, banner+len(panels)*original.height), "#21252a")
    ImageDraw.Draw(sheet).text((9, 7), f"{row['id']}  |  original / proposed masks  |  review required", fill="white")
    for index, (plain, overlay) in enumerate(panels):
        top = banner+index*original.height
        sheet.paste(plain, (0, top))
        sheet.paste(overlay, (panel_width, top))
    sheet.save(output)


def load_proposals(proposals_dir, image_ids):
    """Read complete per-image records while a proposal run is still active."""
    index_path = proposals_dir / "index.json"
    records_dir = proposals_dir / "records"
    if records_dir.is_dir():
        records = []
        for key in image_ids:
            path = records_dir / f"{key}.json"
            if path.is_file():
                record = read_json(path)
                if record["id"] != key:
                    raise ValueError(f"{key}: saved proposal file id does not match its name")
                records.append(record)
        return indexed(records)
    if index_path.is_file():
        return indexed(read_json(index_path)["images"])
    return {}


def white_mask_corners(keys, width, height):
    white = [item["polygon"] for item in keys if item["class"] == CLASSES[1]]
    if len(white) < 8:
        return None
    vertices = np.concatenate([np.asarray(polygon, dtype=np.float32) for polygon in white])
    hull = cv2.convexHull(vertices)
    mask = np.zeros((height, width), dtype=np.uint8)
    cv2.fillConvexPoly(mask, np.rint(hull).astype(np.int32), 1)
    try:
        corners, _ = quad_from_mask(mask.astype(bool, copy=False))
    except ValueError:
        return None
    return corners


def prepare(workspace, proposals_dir, output, filled=False, white_corners=False):
    rows = indexed(read_json(workspace / "inventory.json")["images"])
    proposals = load_proposals(proposals_dir, rows)
    if not proposals:
        raise ValueError(f"no proposal records are available in {proposals_dir}")
    if not proposals.keys() <= rows.keys():
        raise ValueError("proposal ids must be in the inventory")
    if output.exists():
        raise ValueError(f"review drafts already exist: {output}")
    output.parent.mkdir(parents=True, exist_ok=True)
    drafts = []
    with tempfile.TemporaryDirectory(dir=output.parent) as staging:
        target = Path(staging) / "drafts"
        target.mkdir()
        for key, row in rows.items():
            proposal = proposals.get(key)
            draft = {"id": key, "image_sha256": row["image_sha256"], "split": row["split"], "status": "pending"}
            if proposal is None:
                drafts.append({**draft, "diagnostics": {"warnings": ["No SAM proposals for this image"]}})
                continue
            if proposal["image_sha256"] != row["image_sha256"]:
                raise ValueError(f"{key}: proposal does not match the image checksum")
            beds = [item for item in proposal["candidates"] if "corners" in item and "rejection" not in item]
            if not beds:
                drafts.append({**draft, "diagnostics": {"warnings": ["No usable keybed quadrilateral"]}})
                continue
            bed = next((item for item in beds if item["id"] == proposal.get("keybed_proposal")), None)
            if bed is None:
                bed = max(beds, key=lambda item: item["score"] * item["mask_shape_iou"])
            with Image.open(image_path(workspace, row)) as source:
                image = source.convert("RGB")
            corners = checked_quad(bed["corners"], *image.size)
            _, forward, bounds = rectify(image, corners)
            keys, diagnostics = assemble_candidates(proposal["candidates"], forward, bounds)
            proposed_bed = {"corners": corners, "proposal": bed["id"]}
            if white_corners and bed["id"] != "guide":
                refined = white_mask_corners(keys, *image.size)
                if refined:
                    proposed_bed.update(corners=refined, method="fitted to visible white-key masks",
                                        initial_corners=corners)
                    _, refined_forward, refined_bounds = rectify(image, refined)
                    revised_keys, revised_diagnostics = assemble_candidates(
                        proposal["candidates"], refined_forward, refined_bounds
                    )
                    if {key["proposal"] for key in revised_keys} == {key["proposal"] for key in keys}:
                        revised_diagnostics["initial_warnings"] = diagnostics["warnings"]
                        keys, diagnostics = revised_keys, revised_diagnostics
            draft.update(keybed=proposed_bed, keys=keys, diagnostics=diagnostics)
            corners = proposed_bed["corners"]
            save_visuals(image, row, keys, corners, target / f"{key}.png", filled)
            drafts.append(draft)
            print(f"{key}: {diagnostics}", flush=True)
        write_json(target / "drafts.json", {"schema": 1, "images": drafts})
        target.rename(output)


@contextmanager
def review_lock(workspace):
    with (workspace / ".reviews.lock").open("a+b") as lock:
        if os.name == "nt":
            import msvcrt

            lock.seek(0)
            msvcrt.locking(lock.fileno(), msvcrt.LK_LOCK, 1)
            try:
                yield
            finally:
                lock.seek(0)
                msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)
        else:
            import fcntl

            fcntl.flock(lock.fileno(), fcntl.LOCK_EX)
            try:
                yield
            finally:
                fcntl.flock(lock.fileno(), fcntl.LOCK_UN)


def apply_decisions(workspace, decisions):
    """Validate explicit final decisions, then atomically save the review batch."""
    with review_lock(workspace):
        write_decisions(workspace, decisions)


def write_decisions(workspace, decisions):
    rows = indexed(read_json(workspace / "inventory.json")["images"])
    review_path = workspace / "reviews.json"
    review_file = read_json(review_path)
    reviews = indexed(review_file["images"])
    decisions = indexed(decisions)
    proposals = load_proposals(workspace / "proposals", decisions)
    for key, decision in decisions.items():
        if key not in rows or key not in reviews:
            raise ValueError(f"unknown image {key}")
        row = rows[key]
        image_path(workspace, row)
        if decision.get("image_sha256") != row["image_sha256"]:
            raise ValueError(f"{key}: decision does not match the image checksum")
        if decision.get("split", row["split"]) != row["split"]:
            raise ValueError(f"{key}: decision split differs from the source inventory; regenerate the draft")
        if decision.get("status") not in ("accepted", "rejected", "negative"):
            raise ValueError(f"{key}: submit an explicit accepted, rejected, or negative decision")
        if not all(isinstance(decision.get(field), str) and decision[field].strip()
                   for field in ("reviewed_by", "reason")):
            raise ValueError(f"{key}: review must name the reviewer and give a reason")
        proposal = proposals.get(key)
        if proposal and proposal["image_sha256"] != row["image_sha256"]:
            raise ValueError(f"{key}: proposal does not match the image checksum")
        resolve_instances(row, decision, proposal)
        reviews[key] = decision
    with tempfile.TemporaryDirectory(dir=workspace) as staging:
        target = Path(staging) / "reviews.json"
        write_json(target, {**review_file, "images": list(reviews.values())})
        target.replace(review_path)


def order_edited_keys(workspace, row, decision):
    with Image.open(image_path(workspace, row)) as source:
        corners = checked_quad(decision["keybed"]["corners"], *source.size)
        _, transform, bounds = rectify(source.convert("RGB"), corners)
    positions = []
    for key in decision["keys"]:
        evidence = key_evidence(key, transform, bounds)
        if evidence is None:
            center = float(project(key["polygon"], transform)[:, 0].mean()) - bounds[0]
        else:
            center = evidence["center"]
        positions.append((center, key))
    positions.sort(key=lambda pair: pair[0])
    for slot, (_, key) in enumerate(positions):
        key["slot"] = slot
    decision["keys"] = [key for _, key in positions]


def serve(workspace, drafts_dir, port):
    rows = indexed(read_json(workspace / "inventory.json")["images"])
    drafts = indexed(read_json(drafts_dir / "drafts.json")["images"])
    if rows.keys() != drafts.keys() or any(
        rows[key]["image_sha256"] != drafts[key]["image_sha256"] for key in rows
    ):
        raise ValueError("review drafts do not match the image inventory")
    page = Path(__file__).with_name("review.html").read_bytes()

    class ReviewRequest(BaseHTTPRequestHandler):
        def log_message(self, *_args):
            pass

        def respond(self, body, content_type="application/json", code=HTTPStatus.OK):
            self.send_response(code)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(body)

        def json(self, value, code=HTTPStatus.OK):
            self.respond(json.dumps(value).encode(), code=code)

        def do_GET(self):
            route = urlsplit(self.path).path
            try:
                if route == "/":
                    self.respond(page, "text/html; charset=utf-8")
                    return
                if route == "/inventory":
                    reviews = indexed(read_json(workspace / "reviews.json")["images"])
                    self.json([{"id": key, "split": row["split"], "status": reviews[key]["status"],
                                "source_url": row["source_url"]} for key, row in rows.items()])
                    return
                prefix, _, key = route.removeprefix("/").partition("/")
                if key in rows:
                    if prefix == "record":
                        reviews = indexed(read_json(workspace / "reviews.json")["images"])
                        self.json({"draft": drafts[key], "review": reviews[key]})
                        return
                    if prefix == "image":
                        self.respond(image_path(workspace, rows[key]).read_bytes(), "image/png")
                        return
                    if prefix == "sheet":
                        sheet = drafts_dir / f"{key}.png"
                        if sheet.is_file():
                            self.respond(sheet.read_bytes(), "image/png")
                            return
            except (OSError, ValueError, KeyError) as error:
                self.json({"error": str(error)}, HTTPStatus.BAD_REQUEST)
                return
            self.json({"error": "Unknown review route"}, HTTPStatus.NOT_FOUND)

        def do_POST(self):
            if self.path != "/decision":
                self.json({"error": "Unknown review route"}, HTTPStatus.NOT_FOUND)
                return
            if self.headers.get_content_type() != "application/json":
                self.json({"error": "Expected a JSON review decision"}, HTTPStatus.BAD_REQUEST)
                return
            try:
                size = int(self.headers.get("Content-Length", 0))
                if not 0 < size <= 2 * 1024 * 1024:
                    raise ValueError("invalid review decision size")
                decision = json.loads(self.rfile.read(size))
                row = rows[decision["id"]]
                if decision["status"] == "accepted":
                    order_edited_keys(workspace, row, decision)
                apply_decisions(workspace, [decision])
                self.json({"status": decision["status"]})
            except (OSError, ValueError, KeyError, TypeError, cv2.error) as error:
                self.json({"error": str(error)}, HTTPStatus.BAD_REQUEST)

    with HTTPServer(("127.0.0.1", port), ReviewRequest) as server:
        print(f"Review editor: http://127.0.0.1:{server.server_port}/", flush=True)
        server.serve_forever()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    draft = commands.add_parser("prepare")
    draft.add_argument("workspace", type=Path)
    draft.add_argument("output", type=Path)
    draft.add_argument("--proposals", type=Path)
    draft.add_argument("--filled", action="store_true", help="Show translucent mask fills in comparison sheets")
    draft.add_argument("--white-corners", action="store_true", help="Offer a keybed fit to the observed white masks")
    apply = commands.add_parser("apply")
    apply.add_argument("workspace", type=Path)
    apply.add_argument("decisions", type=Path)
    editor = commands.add_parser("edit")
    editor.add_argument("workspace", type=Path)
    editor.add_argument("drafts", type=Path)
    editor.add_argument("--port", type=int, default=8765)
    args = parser.parse_args()
    if args.command == "prepare":
        prepare(args.workspace, args.proposals or args.workspace / "proposals", args.output, args.filled, args.white_corners)
    elif args.command == "apply":
        apply_decisions(args.workspace, read_json(args.decisions)["images"])
    else:
        serve(args.workspace, args.drafts, args.port)


if __name__ == "__main__":
    main()
