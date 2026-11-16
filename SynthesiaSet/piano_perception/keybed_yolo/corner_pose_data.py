"""Add independently approved corner-only training annotations to a pose pack."""

import argparse
from collections import Counter
from pathlib import Path
import shutil
import tempfile

from PIL import Image

from corner_pose import paths, pose_label, pose_yaml, verify_pose_dataset
from dataset import read_json, sha256, sha256_file, validate_sources, write_json
from resource_budget import ResourceBudget

LABEL_SCOPE = "playable_keybed_corners_only"


def approved_reference(row):
    if row["split"] != "train":
        raise ValueError(f"{row['id']}: the approved additions ledger must contain only training photos")
    review = row["review"]
    if (review.get("status") != "accepted" or review.get("label_scope") != LABEL_SCOPE
            or not isinstance(review.get("reviewed_by"), str) or not review["reviewed_by"].strip()
            or not isinstance(review.get("reason"), str) or not review["reason"].strip()):
        raise ValueError(f"{row['id']}: independent review approval is missing")
    if row.get("labelled_by") and row["labelled_by"] == review["reviewed_by"]:
        raise ValueError(f"{row['id']}: label author cannot approve their own annotation")
    image = Path(row["image"])
    if not image.is_absolute() or image.suffix.lower() != ".png" or not image.is_file():
        raise ValueError(f"{row['id']}: expected an absolute reviewed PNG path")
    if sha256_file(image) != row["image_sha256"]:
        raise ValueError(f"{row['id']}: reviewed image checksum differs")
    with Image.open(image) as source:
        if source.format != "PNG" or source.mode != "RGB" or source.size != (row["width"], row["height"]):
            raise ValueError(f"{row['id']}: PNG format or dimensions differ from the corner review")
        # Match the dimension-prefixed RGB digest in the canonical acquisition metadata.
        pixel_hash = sha256(f"{source.width}x{source.height}\0".encode() + source.tobytes())
    reference = {key: row[key] for key in (
        "id", "group", "split", "source_url", "author", "license", "license_url",
        "width", "height", "image_sha256", "corners",
    )}
    reference.update(image_file=str(image), pixel_sha256=pixel_hash, review=review)
    if "expected_white_count" in row:
        if type(row["expected_white_count"]) is not int or row["expected_white_count"] < 1:
            raise ValueError(f"{row['id']}: expected_white_count must be a positive integer")
        reference["white_keys"] = row["expected_white_count"]
    return image, reference


def augment(base, approvals, output):
    if output.exists():
        raise ValueError(f"extended pose dataset already exists: {output}")
    ledger_hash = sha256_file(approvals)
    ledger = read_json(approvals)
    if ledger.get("schema") != 1 or not ledger.get("images"):
        raise ValueError("expected a nonempty schema 1 corner training approvals ledger")
    if any(row["split"] != "train" for row in ledger["images"]):
        raise ValueError("the approved additions ledger must contain only training photos")
    base_manifest = base / "pose-provenance.json"
    base_hash = sha256_file(base_manifest)
    base_metadata = read_json(base_manifest)
    canonical = verify_pose_dataset(base)
    added = [approved_reference(row) for row in ledger["images"]]
    rows = canonical + [reference for _, reference in added]
    validate_sources(rows)
    for field in ("pixel_sha256", "image_sha256"):
        if len({row[field] for row in rows}) != len(rows):
            raise ValueError(f"duplicate {field} between approved datasets")
    base_files = [(paths(base, row)[0], row) for row in canonical]
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=output.parent) as staging:
        target = Path(staging) / "dataset"
        for image, row in base_files + added:
            image_path, label = paths(target, row)
            image_path.parent.mkdir(exist_ok=True, parents=True)
            label.parent.mkdir(exist_ok=True, parents=True)
            shutil.copyfile(image, image_path)
            if sha256_file(image_path) != row["image_sha256"]:
                raise ValueError(f"{row['id']}: an image changed during pose packing")
            label.write_text(pose_label(row), encoding="utf-8")
            row["label_sha256"] = sha256_file(label)
        if sha256_file(approvals) != ledger_hash or sha256_file(base_manifest) != base_hash:
            raise ValueError("an approved input manifest changed while packing")
        (target / "corners.yaml").write_text(pose_yaml(output), encoding="utf-8")
        write_json(target / "pose-provenance.json", {
            **base_metadata, "images": rows, "base_pose_provenance_sha256": base_hash,
            "extra_training_approvals_sha256": ledger_hash,
            "extra_training_image_count": len(added),
        })
        target.rename(output)
    return dict(Counter(row["split"] for row in rows))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("base", type=Path, help="verified original pose dataset")
    parser.add_argument("approvals", type=Path, help="approved training-only corner annotations")
    parser.add_argument("output", type=Path, help="new combined dataset directory")
    args = parser.parse_args()
    output = args.output.resolve()
    ResourceBudget(output.parent / f"{output.name}-resources.jsonl")
    print(augment(args.base.resolve(), args.approvals.resolve(), output))


if __name__ == "__main__":
    main()
