"""Propose key masks from overlapping perspective-corrected SAM 3.1 crops."""

import argparse
from collections import Counter
from importlib.metadata import version
from math import ceil, dist
from pathlib import Path
import tempfile
from time import perf_counter

if __name__ == "__main__":
    from resource_budget import constrain_cpu

    constrain_cpu()

import cv2
import numpy as np
from PIL import Image

from dataset import image_path, indexed, read_json, sha256_file, write_json
from geometry import CLASSES, checked_polygon, checked_quad
from propose import KEY_POLYGON_MAX_ERROR_PX, Sam, key_polygon, quad_from_mask


def project(points, transform):
    return cv2.perspectiveTransform(np.asarray([points], dtype=np.float32), transform)[0]


def rectify(image, corners):
    """Keep approximate native resolution and include context around the keybed."""
    width = round(max(dist(corners[0], corners[1]), dist(corners[2], corners[3])))
    height = round(max(dist(corners[0], corners[3]), dist(corners[1], corners[2])))
    if min(width, height) < 12:
        raise ValueError("keybed is too small to rectify")
    x = max(8, round(width * 0.025))
    y = max(8, round(height * 0.18))
    target = [[x, y], [x + width, y], [x + width, y + height], [x, y + height]]
    forward = cv2.getPerspectiveTransform(np.float32(corners), np.float32(target))
    pixels = cv2.warpPerspective(np.asarray(image), forward, (width + 2*x, height + 2*y),
                                 borderMode=cv2.BORDER_REPLICATE)
    return Image.fromarray(pixels), forward, (x, y, width, height)


def tiles(image, bounds, count=None):
    x, _, width, height = bounds
    count = count or min(10, max(2, ceil(width / (height * 1.8))))
    pad = max(8, round(0.23 * width / count))
    for index in range(count):
        left = max(0, round(x + width * index / count) - pad)
        right = min(image.width, round(x + width * (index + 1) / count) + pad)
        yield left, image.crop((left, 0, right, image.height))


BED_PROMPTS = ((CLASSES[0], "piano keys", 2), (CLASSES[0], "piano keyboard keybed", 2))
KEY_PROMPTS = ((CLASSES[1], "white piano keys", 48), (CLASSES[2], "black piano keys", 32))


def propose_image(sam, image, key_threshold, bed_threshold, tile_count, guide):
    candidates = []
    for index, (class_name, prompt, score, mask) in enumerate(
        sam.detections(image, BED_PROMPTS, bed_threshold), start=1
    ):
        item = {"id": f"bed-{index}", "class": class_name, "prompt": prompt, "score": score}
        try:
            item["corners"], item["mask_shape_iou"] = quad_from_mask(mask)
        except ValueError as error:
            item["rejection"] = str(error)
        candidates.append(item)
    valid_beds = [item for item in candidates if "corners" in item]
    if guide:
        bed = {"id": "guide", "class": CLASSES[0], "corners": checked_quad(guide, *image.size),
               "source": "existing guide corners"}
        candidates.append(bed)
    elif valid_beds:
        bed = max(valid_beds, key=lambda item: item["score"] * item["mask_shape_iou"])
    else:
        return {"candidates": candidates, "tile_count": 0}
    rectified, forward, bounds = rectify(image, bed["corners"])
    inverse = np.linalg.inv(forward)
    tile_index = 0
    for tile_index, (left, tile) in enumerate(tiles(rectified, bounds, tile_count), start=1):
        found = sam.detections(tile, KEY_PROMPTS, key_threshold)
        for index, (class_name, prompt, score, mask) in enumerate(found, start=1):
            item = {"id": f"t{tile_index}-{index}", "class": class_name, "tile": tile_index,
                    "prompt": prompt, "score": score}
            try:
                polygon, item["mask_shape_iou"] = key_polygon(mask)
                polygon = project([(x + left, y) for x, y in polygon], inverse).tolist()
                item["polygon"] = checked_polygon(polygon, *image.size)
            except ValueError as error:
                item["rejection"] = str(error)
            candidates.append(item)
    return {"candidates": candidates, "keybed_proposal": bed["id"], "tile_count": tile_index}


def atomic_json(path, value):
    with tempfile.TemporaryDirectory(dir=path.parent) as staging:
        pending = Path(staging) / path.name
        write_json(pending, value)
        pending.replace(path)


def seed_records(records_dir, configuration, seeds):
    settings = {key: value for key, value in configuration.items() if key != "image_sha256"}
    for seed in seeds:
        seed_settings_path = seed / "settings.json"
        seed_settings = read_json(seed_settings_path)
        image_hashes = seed_settings.pop("image_sha256")
        if seed_settings != settings:
            raise ValueError(f"seed proposal settings do not match: {seed}")
        settings_hash = sha256_file(seed_settings_path)
        for key, image_hash in configuration["image_sha256"].items():
            record_path = seed / "records" / f"{key}.json"
            destination = records_dir / record_path.name
            if image_hashes.get(key) != image_hash or destination.exists() or not record_path.is_file():
                continue
            record = read_json(record_path)
            if record["id"] != key or record["image_sha256"] != image_hash:
                raise ValueError(f"{key}: seed proposal record does not match its inventory")
            record["seed_provenance"] = {"settings_sha256": settings_hash, "record_sha256": sha256_file(record_path)}
            atomic_json(destination, record)


def run(workspace, checkpoint, output, device, edge, key_threshold, bed_threshold, tile_count,
        selected, guide_path, resume, stop_file, budget=None, seeds=()):
    import torch

    inventory = indexed(read_json(workspace / "inventory.json")["images"])
    if selected and not selected <= inventory.keys():
        raise ValueError("selected image id is not in the inventory")
    inventory = {key: row for key, row in inventory.items() if not selected or key in selected}
    guides = indexed(read_json(guide_path)["images"]) if guide_path else {}
    checkpoint_files = sorted((*checkpoint.glob("*.json"), *checkpoint.glob("*.safetensors")))
    configuration = {
        "schema": 1,
        "torch": torch.__version__,
        "transformers": version("transformers"),
        "checkpoint": str(checkpoint),
        "checkpoint_sha256": {path.name: sha256_file(path) for path in checkpoint_files},
        "guide_sha256": sha256_file(guide_path) if guide_path else None,
        "input_edge": edge,
        "device": device,
        "key_threshold": key_threshold,
        "bed_threshold": bed_threshold,
        "mask_threshold": 0.5,
        "key_polygon_max_error_px": KEY_POLYGON_MAX_ERROR_PX,
        "bed_prompts": [list(prompt) for prompt in BED_PROMPTS],
        "key_prompts": [list(prompt) for prompt in KEY_PROMPTS],
        "requested_tiles": tile_count,
        "image_sha256": {key: row["image_sha256"] for key, row in inventory.items()},
    }
    settings = output / "settings.json"
    if output.exists() and not resume:
        raise ValueError(f"proposals already exist: {output}")
    if output.exists() and (not settings.is_file() or read_json(settings) != configuration):
        raise ValueError(f"resume settings differ from the saved proposal run: {output}")
    records_dir = output / "records"
    records_dir.mkdir(parents=True, exist_ok=True)
    if not settings.exists():
        atomic_json(settings, configuration)
    complete_index = output / "index.json"
    if complete_index.exists() and read_json(complete_index).get("complete"):
        print(f"All {len(inventory)} image proposals are already present", flush=True)
        return
    seed_records(records_dir, configuration, seeds)
    sam = None
    has_missing = any(not (records_dir / f"{key}.json").is_file() for key in inventory)
    if has_missing and (stop_file is None or not stop_file.exists()):
        sam = Sam(checkpoint, device, edge, budget)
    records = []
    with torch.inference_mode():
        for key, row in inventory.items():
            record_path = records_dir / f"{key}.json"
            if record_path.exists():
                record = read_json(record_path)
                if record["image_sha256"] != row["image_sha256"]:
                    raise ValueError(f"{key}: saved proposal does not match the image checksum")
                records.append(record)
                continue
            if sam is None or stop_file is not None and stop_file.exists():
                break
            start = perf_counter()
            guide = guides.get(key)
            if guide and guide["image_sha256"] != row["image_sha256"]:
                raise ValueError(f"{key}: guide does not match the image checksum")
            corners = guide.get("keybed", {}).get("corners", guide.get("corners")) if guide else None
            with Image.open(image_path(workspace, row)) as original:
                image = original.convert("RGB")
            result = propose_image(sam, image, key_threshold, bed_threshold, tile_count, corners)
            counts = Counter(item["class"] for item in result["candidates"] if "rejection" not in item)
            elapsed = perf_counter() - start
            record = {"id": key, "image_sha256": row["image_sha256"], "proposal_seconds": round(elapsed, 3), **result}
            if corners:
                record["crop_guide"] = guide
            atomic_json(record_path, record)
            records.append(record)
            print(f"{key}: {dict(counts)}, {result['tile_count']} tiles, {elapsed:.2f}s", flush=True)
    if records:
        atomic_json(complete_index, {**configuration, "complete": len(records) == len(inventory), "images": records})
    print(f"Saved {len(records)}/{len(inventory)} image proposals", flush=True)


def main():
    from resource_budget import ResourceBudget

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("workspace", type=Path)
    parser.add_argument("checkpoint", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--guides", type=Path, help="Optional image-hash-bound corner corrections")
    parser.add_argument("--image-id", action="append")
    parser.add_argument("--resume", action="store_true", help="Continue a saved run with the same settings")
    parser.add_argument("--seed", action="append", type=Path, default=[],
                        help="Reuse exact-image records from a prior run with identical model settings")
    parser.add_argument("--stop-file", type=Path, help="Pause between images when this path exists")
    parser.add_argument("--budget-log", type=Path, help="Save resource-budget telemetry as JSON lines")
    parser.add_argument("--device", default="cuda")
    parser.add_argument("--edge", choices=(672, 1008), type=int, default=672)
    parser.add_argument("--key-threshold", type=float, default=0.25)
    parser.add_argument("--bed-threshold", type=float, default=0.35)
    parser.add_argument("--tiles", type=int)
    args = parser.parse_args()
    if not args.checkpoint.is_dir():
        parser.error("checkpoint must be an existing local directory")
    if not 0 < args.key_threshold < 1 or not 0 < args.bed_threshold < 1:
        parser.error("thresholds must be between 0 and 1")
    if args.tiles is not None and not 1 <= args.tiles <= 16:
        parser.error("tiles must be between 1 and 16")
    budget = ResourceBudget(log_path=args.budget_log)
    budget.configure_torch(args.device)
    cv2.setNumThreads(2)
    run(args.workspace, args.checkpoint.resolve(), args.output or args.workspace / "proposals",
        args.device, args.edge, args.key_threshold, args.bed_threshold, args.tiles,
        set(args.image_id or ()), args.guides, args.resume, args.stop_file, budget, args.seed)


if __name__ == "__main__":
    main()
