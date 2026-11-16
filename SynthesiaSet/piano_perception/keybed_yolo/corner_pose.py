"""Train and evaluate an ordered four-corner YOLO26n pose model."""

import argparse
from collections import Counter
from copy import copy
import json
from math import ceil, dist
from pathlib import Path
import shutil
import tempfile

import numpy as np
from PIL import Image

from dataset import SPLITS, read_json, sha256_file, validate_sources, write_json
from fit import PINNED_ULTRALYTICS, BudgetedBatches, budgeted_trainer, verify_dataset, yolo
from geometry import CLASSES, CORNER_NAMES, checked_quad
from resource_budget import ResourceBudget

KPT_SHAPE = (4, 3)
# Pose26 uses these in the loss and positive-anchor assignment. A narrow sigma
# starved horizontal gradients while the four points were still near one anchor.
KPT_SIGMAS = (0.5,) * 4
TOLERANCE = 0.25
ERROR_CAP = 4.0
SCORE_FLOOR = 0.001
TRAINING = {
    "workers": 0, "deterministic": True, "amp": False, "plots": False, "nms": False,
    "fliplr": 0.0, "flipud": 0.0, "degrees": 0.0, "translate": 0.0, "scale": 0.0,
    "mosaic": 0.0, "mixup": 0.0, "copy_paste": 0.0, "shear": 0.0, "perspective": 0.0,
    "patience": 0, "conf": SCORE_FLOOR, "max_det": 5,
}


def pose_yaml(root):
    return (
        f"path: {json.dumps(root.resolve().as_posix())}\n"
        "train: images/train\nval: images/val\n"
        f"names: {{0: {CLASSES[0]}}}\n"
        f"kpt_shape: {list(KPT_SHAPE)}\n"
        f"kpt_oks_sigmas: {list(KPT_SIGMAS)}\n"
        f"kpt_names: {{0: {json.dumps(CORNER_NAMES)}}}\n"
    )


def pose_label(reference):
    width, height = reference["width"], reference["height"]
    polygon = checked_quad(reference["corners"], width, height)
    xs, ys = zip(*polygon)
    box = [
        (min(xs) + max(xs)) / (2 * width), (min(ys) + max(ys)) / (2 * height),
        (max(xs) - min(xs)) / width, (max(ys) - min(ys)) / height,
    ]
    keypoints = " ".join(f"{x / width:.8f} {y / height:.8f} 2" for x, y in polygon)
    return "0 " + " ".join(f"{value:.8f}" for value in box) + f" {keypoints}\n"


def prepare(source, output):
    snapshot = read_json(source / "final-snapshot.json")
    source_hash = sha256_file(source / "provenance.json")
    if source_hash != snapshot["packed_provenance_sha256"]:
        raise ValueError("packed provenance checksum differs from the approved freeze")
    accepted = verify_dataset(source)
    if output.exists():
        raise ValueError(f"pose dataset already exists: {output}")
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=output.parent) as staging:
        target = Path(staging) / "dataset"
        rows = []
        for image, _, row in accepted:
            width, height = row["width"], row["height"]
            review = row["review"]
            if review["status"] != "accepted" or review["image_sha256"] != row["image_sha256"]:
                raise ValueError(f"{row['id']}: expected an image-bound positive review")
            (bed,) = [item for item in row["instances"] if item["class"] == CLASSES[0]]
            corners = review["keybed"]["corners"]
            if corners != bed["polygon"]:
                raise ValueError(f"{row['id']}: reviewed and packed corners differ")
            white_keys = sum(item["class"] == CLASSES[1] for item in row["instances"])
            if white_keys < 1:
                raise ValueError(f"{row['id']}: reviewed white-key count is missing")
            reference = {key: row[key] for key in (
                "id", "group", "split", "width", "height", "source_url", "author",
                "license", "license_url", "image_file", "image_sha256", "pixel_sha256",
            )}
            reference.update(corners=corners, white_keys=white_keys)
            label_text = pose_label(reference)
            image_path, label_path = paths(target, reference)
            image_path.parent.mkdir(parents=True, exist_ok=True)
            label_path.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(image, image_path)
            with Image.open(image_path) as opened:
                if opened.mode != "RGB" or opened.size != (width, height):
                    raise ValueError(f"{row['id']}: packed image format or dimensions changed")
            label_path.write_text(label_text, encoding="utf-8")
            reference["label_sha256"] = sha256_file(label_path)
            rows.append(reference)
        counts = Counter(row["split"] for row in rows)
        if counts != snapshot["positive_counts_by_split"]:
            raise ValueError("pose conversion counts differ from the approved freeze")
        validate_sources(rows)
        (target / "corners.yaml").write_text(pose_yaml(output), encoding="utf-8")
        write_json(target / "pose-provenance.json", {
            "schema": 1, "source_provenance_sha256": source_hash,
            "source_snapshot_sha256": sha256_file(source / "final-snapshot.json"),
            "corner_order": CORNER_NAMES, "images": rows,
        })
        target.rename(output)
    return dict(counts)


def paths(root, row):
    split, key = row["split"], row["id"]
    return root / "images" / split / f"{key}.png", root / "labels" / split / f"{key}.txt"


def verify_pose_dataset(root):
    if (root / "corners.yaml").read_text(encoding="utf-8") != pose_yaml(root):
        raise ValueError("pose dataset YAML changed")
    manifest = read_json(root / "pose-provenance.json")
    if manifest["schema"] != 1 or tuple(manifest["corner_order"]) != CORNER_NAMES:
        raise ValueError("unexpected corner pose provenance")
    rows = manifest["images"]
    validate_sources(rows)
    if len({row["pixel_sha256"] for row in rows}) != len(rows):
        raise ValueError("duplicate image in pose dataset")
    for row in rows:
        image, label = paths(root, row)
        if (sha256_file(image) != row["image_sha256"] or sha256_file(label) != row["label_sha256"]
                or label.read_text(encoding="utf-8") != pose_label(row)):
            raise ValueError(f"{row['id']}: pose training bytes differ from provenance")
    for split in SPLITS:
        subset = [paths(root, row) for row in rows if row["split"] == split]
        for kind, index in (("images", 0), ("labels", 1)):
            folder = root / kind / split
            if not subset or set(folder.iterdir()) != {entry[index] for entry in subset}:
                raise ValueError(f"{split}: {kind} do not match the pose manifest")
    return rows


def source_keypoints(keypoints, ratio_pad):
    """Invert letterboxing using both realized scales, with no edge clipping."""
    (scale_y, scale_x), (left, top) = ratio_pad
    keypoints = np.array(keypoints, dtype=float, copy=True)
    keypoints[:, :2] = (keypoints[:, :2] - [left, top]) / [scale_x, scale_y]
    return keypoints


def score_pose(reference, keypoints, confidence):
    case = {
        "id": reference["id"], "group": reference["group"], "object_confidence": confidence,
        "ordered_corners": None, "keypoint_confidence": None, "errors_white_widths": None,
        "max_error_white_widths": None, "strict": False, "rejection": None,
    }
    if keypoints is None:
        case["rejection"] = "no_detection"
        return case
    if (keypoints.shape != KPT_SHAPE or not np.isfinite(keypoints).all()
            or not 0 <= confidence <= 1 or not np.all((0 <= keypoints[:, 2]) & (keypoints[:, 2] <= 1))):
        case["rejection"] = "invalid_pose_output"
        return case
    xy = keypoints[:, :2].tolist()
    case.update(ordered_corners=xy, keypoint_confidence=keypoints[:, 2].tolist())
    expected = reference["corners"]
    white_width = dist(expected[2], expected[3]) / reference["white_keys"]
    errors = [dist(actual, annotated) / white_width for actual, annotated in zip(xy, expected)]
    case["errors_white_widths"] = dict(zip(CORNER_NAMES, errors))
    case["max_error_white_widths"] = max(errors)
    try:
        checked_quad(xy, reference["width"], reference["height"])
    except ValueError as error:
        case["rejection"] = str(error)
    case["strict"] = case["rejection"] is None and max(errors) <= TOLERANCE
    return case


def summarize(cases):
    if not cases:
        raise ValueError("cannot summarize an empty pose split")
    accepted = [case for case in cases if case["rejection"] is None]
    errors = [case["max_error_white_widths"] for case in accepted]
    total = len(cases)
    capped = (sum(min(error, ERROR_CAP) for error in errors) + (total - len(accepted)) * ERROR_CAP) / total
    return {
        "total": total, "valid_quad": len(accepted), "strict": sum(case["strict"] for case in cases),
        "mean_capped_max_error_white_widths": capped,
        "median_max_error_white_widths_valid": float(np.median(errors)) if errors else None,
        "p90_max_error_white_widths_valid": float(np.percentile(errors, 90)) if errors else None,
        "rejection_reasons": dict(Counter(case["rejection"] for case in cases if case["rejection"])),
    }


def fitness(summary):
    # A gain of one strict image always outranks any capped-error change.
    return summary["strict"] + 0.5 * (1 - summary["mean_capped_max_error_white_widths"] / ERROR_CAP)


def corner_trainer(base, references, history):
    from ultralytics.data import build_yolo_dataset
    from ultralytics.models.yolo.pose import PoseValidator

    class CornerValidator(PoseValidator):
        def init_metrics(self, model):
            super().init_metrics(model)
            self.cases = []

        def update_metrics(self, predictions, batch):
            super().update_metrics(predictions, batch)
            for prediction, filename, ratio_pad in zip(predictions, batch["im_file"], batch["ratio_pad"]):
                reference = references[Path(filename).stem]
                scores = prediction["conf"]
                if len(scores):
                    best = int(scores.argmax())
                    corners = source_keypoints(prediction["keypoints"][best].cpu().numpy(), ratio_pad)
                    case = score_pose(reference, corners, float(scores[best]))
                else:
                    case = score_pose(reference, None, None)
                self.cases.append(case)

        def get_stats(self):
            if {case["id"] for case in self.cases} != references.keys() or len(self.cases) != len(references):
                raise ValueError("validation did not score each approved val image exactly once")
            stats = super().get_stats()
            summary = summarize(self.cases)
            stats.update({
                "fitness": fitness(summary), "corners/strict": summary["strict"],
                "corners/capped_max_error": summary["mean_capped_max_error_white_widths"],
            })
            if self.training:
                history.append({"epoch": len(history) + 1, "fitness": stats["fitness"], **summary})
                write_json(self.save_dir / "corner-selection-history.json", history)
            return stats

    class CornerTrainer(base):
        def build_dataset(self, img_path, mode="train", batch=None):
            if mode == "train":
                return super().build_dataset(img_path, mode, batch)
            return build_yolo_dataset(self.args, img_path, batch, self.data, mode=mode, rect=False, stride=32)

        def get_validator(self):
            return CornerValidator(self.test_loader, save_dir=self.save_dir, args=copy(self.args),
                                   _callbacks=self.callbacks)

    return CornerTrainer


def train(dataset, weights, output, device, size, epochs, batch, seed):
    if output.exists():
        raise ValueError(f"training output already exists: {output}")
    budget = ResourceBudget(output.parent / f"{output.name}-resources.jsonl")
    provenance = dataset / "pose-provenance.json"
    code_hash = sha256_file(Path(__file__))
    dataset_hash, initial_hash = sha256_file(provenance), sha256_file(weights)
    yaml_hash = sha256_file(dataset / "corners.yaml")
    rows = verify_pose_dataset(dataset)
    references = {row["id"]: row for row in rows if row["split"] == "val"}
    budget.configure_torch("cpu" if device == "cpu" else f"cuda:{device}" if device.isdigit() else device)
    budget.torch.set_num_threads(min(4, len(budget.cpu["affinity"])))
    for split in ("train", "val"):
        (dataset / "labels" / f"{split}.cache").unlink(missing_ok=True)

    batches, history = BudgetedBatches(budget), []
    with batches.phase():
        model = yolo(output.parent)(str(weights), task="pose")
        head = model.model.model[-1]
        if type(head).__name__ != "Pose26" or model.model.yaml.get("scale") != "n":
            raise ValueError("expected a YOLO26n pose checkpoint")
        initial_shape = list(head.kpt_shape)
        for event in ("on_train_batch_start", "on_val_batch_start"):
            model.add_callback(event, batches.begin)
        for event in ("on_pretrain_routine_end", "on_train_batch_end", "on_train_epoch_end"):
            model.add_callback(event, batches.end)
        model.add_callback("on_val_batch_end", batches.end_validation_batch)
        trainer = corner_trainer(model._smart_load("trainer"), references, history)
        model.train(
            trainer=budgeted_trainer(trainer, batches), data=str(dataset / "corners.yaml"),
            project=str(output.parent), name=output.name, device=device, imgsz=size, epochs=epochs,
            batch=batch, seed=seed, **TRAINING,
        )
    head = model.model.model[-1]
    if tuple(head.kpt_shape) != KPT_SHAPE or head.nc != 1 or not model.model.end2end:
        raise ValueError("trained model lacks the required four-keypoint one-to-one head")
    if (dataset_hash != sha256_file(provenance) or initial_hash != sha256_file(weights)
            or yaml_hash != sha256_file(dataset / "corners.yaml")):
        raise ValueError("dataset metadata or warm start changed during training")
    best = max(history, key=lambda item: (item["fitness"], item["epoch"]))
    write_json(output / "training-provenance.json", {
        "ultralytics": PINNED_ULTRALYTICS, "initial_checkpoint_sha256": initial_hash,
        "initial_keypoint_shape": initial_shape, "source_code_sha256": code_hash,
        "dataset_yaml_sha256": yaml_hash,
        "dataset_provenance_sha256": dataset_hash, "verified_images_by_split": dict(Counter(
            row["split"] for row in rows)),
        "seed": seed, "size": size, "epochs_requested": epochs, "batch_requested": batch,
        "training": TRAINING, "keypoint_shape": KPT_SHAPE, "corner_order": CORNER_NAMES,
        "keypoint_oks_sigmas": KPT_SIGMAS, "training_split": "train", "selection_split": "val",
        "checkpoint_selection": "Maximize strict passes, then minimize mean capped max-corner error",
        "best_validation": best, "best_sha256": sha256_file(output / "weights" / "best.pt"),
        "last_sha256": sha256_file(output / "weights" / "last.pt"),
        "resource_policy": {"cpu": budget.cpu, "gpu_duty": budget.duty, "gpu_target": budget.gpu_target},
    })


def pose_input(image, size):
    """Use the training validator's full-frame, fixed-square letterbox convention."""
    import cv2

    width, height = image.size
    scale = size / max(width, height)
    resized_width, resized_height = min(size, ceil(width * scale)), min(size, ceil(height * scale))
    left, top = (size - resized_width) // 2, (size - resized_height) // 2
    target = np.full((size, size, 3), 114, dtype=np.uint8)
    target[top:top + resized_height, left:left + resized_width] = cv2.resize(
        np.asarray(image), (resized_width, resized_height), interpolation=cv2.INTER_LINEAR,
    )
    pixels = np.ascontiguousarray(target.transpose(2, 0, 1)[None], dtype=np.float32) / 255
    return pixels, ((resized_height / height, resized_width / width), (left, top))


def top_pose(rows, ratio_pad):
    if rows.ndim != 3 or rows.shape[0] != 1 or rows.shape[2] != 18:
        raise ValueError(f"expected [1,N,18] end-to-end pose rows, got {rows.shape}")
    if not np.isfinite(rows).all():
        raise ValueError("nonfinite pose model output")
    if len(rows[0]) == 0:
        return None, None
    top = rows[0, rows[0, :, 4].argmax()]
    if top[5] != 0:
        raise ValueError("expected the playable_keybed class")
    if top[4] <= SCORE_FLOOR:
        return None, float(top[4])
    return source_keypoints(top[6:].reshape(KPT_SHAPE), ratio_pad), float(top[4])


def load_pose(weights, config):
    model = yolo(config)(str(weights), task="pose").model
    head = model.model[-1]
    if (type(head).__name__ != "Pose26" or tuple(head.kpt_shape) != KPT_SHAPE or head.nc != 1
            or not model.end2end):
        raise ValueError("expected a one-to-one Pose26 head with four keypoints and one class")
    return model.eval()


def infer_image(model, image, size):
    import torch

    pixels, ratio_pad = pose_input(image, size)
    with torch.inference_mode():
        prediction, _ = model(torch.from_numpy(pixels))
    return top_pose(prediction.numpy(), ratio_pad)


def evaluate(dataset, weights, output, size):
    if output.exists():
        raise ValueError(f"evaluation output already exists: {output}")
    budget = ResourceBudget(output.parent / f"{output.name}-resources.jsonl")
    budget.configure_torch("cpu")
    budget.torch.set_num_threads(min(4, len(budget.cpu["affinity"])))
    data = verify_pose_dataset(dataset)
    model = load_pose(weights, output.parent)
    cases = []
    for reference in data:
        if reference["split"] != "val":
            continue
        image_path, _ = paths(dataset, reference)
        with Image.open(image_path) as image:
            corners, confidence = infer_image(model, image.convert("RGB"), size)
        cases.append(score_pose(reference, corners, confidence))
    report = {
        "split": "val", "scope": "Development validation photos also used for checkpoint selection.",
        "size": size, "corner_order": CORNER_NAMES, "score_floor": SCORE_FLOOR,
        "geometry": "Ordered source-pixel distances divided by reviewed front edge length per white key.",
        "strict_max_error_white_widths": TOLERANCE, "weights_sha256": sha256_file(weights),
        "dataset_provenance_sha256": sha256_file(dataset / "pose-provenance.json"),
        "source_code_sha256": sha256_file(Path(__file__)),
        "summary": summarize(cases), "cases": cases,
    }
    output.mkdir(parents=True)
    write_json(output / "evaluation.json", report)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    subcommands = parser.add_subparsers(dest="command", required=True)
    pack = subcommands.add_parser("prepare")
    pack.add_argument("source", type=Path)
    pack.add_argument("output", type=Path)
    for command in ("train", "evaluate"):
        run = subcommands.add_parser(command)
        run.add_argument("dataset", type=Path)
        run.add_argument("weights", type=Path)
        run.add_argument("output", type=Path)
        run.add_argument("--size", type=int, default=960)
        if command == "train":
            run.add_argument("--device", default="0")
            run.add_argument("--epochs", type=int, default=40)
            run.add_argument("--batch", type=int, default=4)
            run.add_argument("--seed", type=int, default=42)
    args = parser.parse_args()
    if args.command == "prepare":
        output = args.output.resolve()
        ResourceBudget(output.parent / f"{output.name}-resources.jsonl")
        print(prepare(args.source.resolve(), args.output.resolve()))
        return
    if args.size < 32 or args.size % 32:
        parser.error("size must be a positive multiple of 32")
    dataset, weights, output = (path.resolve() for path in (args.dataset, args.weights, args.output))
    if args.command == "train":
        if min(args.batch, args.epochs) < 1:
            parser.error("batch and epochs must be positive")
        train(dataset, weights, output, args.device, args.size, args.epochs, args.batch, args.seed)
    else:
        print(evaluate(dataset, weights, output, args.size)["summary"])


if __name__ == "__main__":
    main()
