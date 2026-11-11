"""Fine-tune a local nano segmentation checkpoint and verify its fixed ONNX export."""

import argparse
from collections import Counter
import os
from pathlib import Path
import shutil
import tempfile

import numpy as np
from PIL import Image

from dataset import SPLITS, dataset_yaml, read_json, sha256_file, validate_sources, write_json
from geometry import CLASSES, CORNER_NAMES, segment_rows

PINNED_ULTRALYTICS = "8.4.210"
SEGMENTATION_TRAINING = {"overlap_mask": False}


def verify_dataset(root):
    if (root / "keybed.yaml").read_text(encoding="utf-8") != dataset_yaml(root):
        raise ValueError("dataset YAML differs from the local packed split configuration")
    rows = read_json(root / "provenance.json")["images"]
    validate_sources(rows)
    accepted = []
    seen = set()
    for row in rows:
        if row["pixel_sha256"] in seen:
            raise ValueError(f"{row['id']}: duplicate image in packed dataset")
        seen.add(row["pixel_sha256"])
        status = row["review"]["status"]
        if status == "rejected":
            continue
        key, split = row["id"], row["split"]
        image = root / "images" / split / f"{key}.png"
        label = root / "labels" / split / f"{key}.txt"
        if sha256_file(image) != row["image_sha256"]:
            raise ValueError(f"{key}: packed image checksum changed")
        if status not in ("accepted", "negative"):
            raise ValueError(f"{key}: unreviewed label")
        instances = row["instances"]
        if (status == "accepted") != bool(instances):
            raise ValueError(f"{key}: missing or unexpected reviewed instances")
        expected = segment_rows(instances, row["width"], row["height"])
        if label.read_text(encoding="utf-8") != expected:
            raise ValueError(f"{key}: packed label differs from reviewed provenance")
        accepted.append((image, label, row))
    for folder, index in (("images", 0), ("labels", 1)):
        included = {entry[index] for entry in accepted}
        directory = root / folder
        caches = {directory / f"{split}.cache" for split in SPLITS} if folder == "labels" else set()
        actual = set()
        for path in directory.rglob("*"):
            if path.is_symlink() or path.is_junction():
                raise ValueError(f"{folder}: packed contents cannot include links")
            if path.is_file() and path not in caches:
                actual.add(path)
        if included != actual:
            raise ValueError(f"{folder}: packed contents differ from reviewed provenance")
    return accepted


def yolo(config_root):
    os.environ["YOLO_CONFIG_DIR"] = str(config_root / "ultralytics-settings")
    os.environ["YOLO_AUTOINSTALL"] = "false"
    os.environ["YOLO_OFFLINE"] = "true"
    import ultralytics

    if ultralytics.__version__ != PINNED_ULTRALYTICS:
        raise ValueError(f"expected ultralytics {PINNED_ULTRALYTICS}, got {ultralytics.__version__}")
    integrations = ("sync", "hub", "comet", "clearml", "dvc", "mlflow", "neptune", "raytune", "wandb")
    ultralytics.settings.update({name: False for name in integrations if name in ultralytics.settings})
    return ultralytics.YOLO


def train(dataset, weights, output, device, size, epochs, batch, seed):
    verify_dataset(dataset)
    if output.exists():
        raise ValueError(f"training output already exists: {output}")
    # Ultralytics checks cache paths and total size, not our reviewed content hashes.
    for split in SPLITS:
        (dataset / "labels" / f"{split}.cache").unlink(missing_ok=True)
    model = yolo(output.parent)(str(weights), task="segment")
    model.train(
        data=str(dataset / "keybed.yaml"), project=str(output.parent), name=output.name,
        device=device, imgsz=size, epochs=epochs, batch=batch, seed=seed, workers=0, deterministic=True,
        # Keep the complete-keybed target visible; photos supply real geometric diversity.
        fliplr=0.0, flipud=0.0, degrees=0.0, translate=0.0, scale=0.0, mosaic=0.0, mixup=0.0,
        copy_paste=0.0, shear=0.0, perspective=0.0, amp=False, plots=False,
        **SEGMENTATION_TRAINING,
    )
    write_json(output / "training-provenance.json", {
        "ultralytics": PINNED_ULTRALYTICS, "initial_checkpoint_sha256": sha256_file(weights),
        "dataset_provenance_sha256": sha256_file(dataset / "provenance.json"),
        "seed": seed, "size": size, "epochs_requested": epochs, "segmentation": SEGMENTATION_TRAINING,
    })


def letterbox(image, height, width):
    import cv2

    source_width, source_height = image.size
    scale = min(width / source_width, height / source_height)
    new_width, new_height = round(source_width * scale), round(source_height * scale)
    left, top = (width - new_width) // 2, (height - new_height) // 2
    canvas = np.full((height, width, 3), 114, dtype=np.uint8)
    canvas[top:top + new_height, left:left + new_width] = cv2.resize(
        np.asarray(image), (new_width, new_height), interpolation=cv2.INTER_LINEAR
    )
    pixels = np.ascontiguousarray(canvas.transpose(2, 0, 1)[None], dtype=np.float32) / 255
    return pixels, scale, left, top


def describe_output(outputs):
    detections = [item for item in outputs if item.ndim == 3]
    prototypes = [item for item in outputs if item.ndim == 4]
    if len(outputs) != 2 or len(detections) != 1 or len(prototypes) != 1:
        raise ValueError(f"expected detections and mask prototypes, got {[item.shape for item in outputs]}")
    detection, prototype = detections[0], prototypes[0]
    mask_count = prototype.shape[1]
    if detection.shape[0] != 1 or prototype.shape[0] != 1:
        raise ValueError("expected batch 1 for detections and mask prototypes")
    if detection.shape[2] == 6 + mask_count:
        return "end_to_end_rows_xyxy_score_class_mask_coefficients"
    if detection.shape[1] == 4 + len(CLASSES) + mask_count:
        return "raw_channels_xywh_class_scores_mask_coefficients"
    raise ValueError(f"unexpected detection tensor shape {detection.shape} for {mask_count} mask coefficients")


def check_test_inputs(session, input_name, data, height, width):
    records = []
    layout = None
    for image_path, _, row in data:
        if row["split"] != "test":
            continue
        with Image.open(image_path) as source:
            image = source.convert("RGB")
        pixels, _, _, _ = letterbox(image, height, width)
        outputs = session.run(None, {input_name: pixels})
        if not all(np.isfinite(output).all() for output in outputs):
            raise ValueError(f"{row['id']}: ONNX returned nonfinite values")
        layout = describe_output(outputs)
        records.append({"id": row["id"], "group": row["group"], "finite": True})
    if not records:
        raise ValueError("no held-out reviewed test images")
    return layout, records


def export(dataset, weights, output, height, width, raw_head):
    import onnx
    import onnxruntime as ort

    data = verify_dataset(dataset)
    if output.exists():
        raise ValueError(f"export output already exists: {output}")
    output.parent.mkdir(parents=True, exist_ok=True)
    model_factory = yolo(output.parent)
    with tempfile.TemporaryDirectory(dir=output.parent) as staging:
        temp = Path(staging)
        target = temp / "export"
        target.mkdir()
        model_path = shutil.copyfile(weights, temp / "keybed.pt")
        model = model_factory(str(model_path), task="segment")
        if tuple(model.names.values()) != CLASSES:
            raise ValueError(f"expected fine-tuned keybed classes {CLASSES}")
        path = Path(model.export(
            format="onnx", imgsz=(height, width), batch=1, device="cpu", dynamic=False, simplify=False,
            half=False, opset=17, nms=None if raw_head else False,
        ))
        onnx.checker.check_model(path)
        graph = onnx.load(path)
        operators = Counter(node.op_type for node in graph.graph.node)
        unsupported = {"NonMaxSuppression", "Loop", "Scan", "If"} & operators.keys()
        if unsupported:
            raise ValueError(f"export includes unexpected graph operators: {sorted(unsupported)}")
        session = ort.InferenceSession(str(path), providers=["CPUExecutionProvider"])
        (input_spec,) = session.get_inputs()
        if input_spec.shape != [1, 3, height, width] or input_spec.type != "tensor(float)":
            raise ValueError(f"export did not have the expected fixed RGB float32 input: {input_spec.shape}")
        layout, records = check_test_inputs(session, input_spec.name, data, height, width)
        shutil.copyfile(path, target / "keybed.onnx")
        write_json(target / "export-report.json", {
            "ultralytics": PINNED_ULTRALYTICS, "onnxruntime": ort.__version__,
            "weights_sha256": sha256_file(weights), "onnx_sha256": sha256_file(path),
            "input": {"name": input_spec.name, "shape": input_spec.shape,
                      "preprocess": "RGB float32 CHW in [0,1], centered letterbox padding value 114"},
            "outputs": [{"name": item.name, "shape": item.shape} for item in session.get_outputs()],
            "output_layout": layout, "classes": CLASSES, "derived_corner_order": CORNER_NAMES,
            "opset": {item.domain or "onnx": item.version for item in graph.opset_import},
            "operators": dict(sorted(operators.items())), "held_out_inference_only": records,
            "verified_backend": "ONNX Runtime CPU",
        })
        del session
        target.rename(output)
    print(f"Exported {layout}; ran {len(records)} held-out images with ONNX Runtime CPU")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    fit = commands.add_parser("train")
    fit.add_argument("dataset", type=Path)
    fit.add_argument("weights", type=Path)
    fit.add_argument("output", type=Path)
    fit.add_argument("--device", default="0")
    fit.add_argument("--size", type=int, default=640)
    fit.add_argument("--epochs", type=int, default=50)
    fit.add_argument("--batch", type=int, default=8)
    fit.add_argument("--seed", type=int, default=42)
    convert = commands.add_parser("export")
    convert.add_argument("dataset", type=Path)
    convert.add_argument("weights", type=Path)
    convert.add_argument("output", type=Path)
    convert.add_argument("--height", type=int, default=480)
    convert.add_argument("--width", type=int, default=640)
    convert.add_argument("--raw-head", action="store_true")
    args = parser.parse_args()
    if not args.weights.is_file():
        parser.error("weights must be an existing local .pt checkpoint")
    dataset, weights, output = (path.resolve() for path in (args.dataset, args.weights, args.output))
    if args.command == "train":
        if args.size < 32 or args.size % 32 or min(args.epochs, args.batch) < 1:
            parser.error("size must be a positive multiple of 32, epochs and batch must be positive")
        train(dataset, weights, output, args.device, args.size, args.epochs, args.batch, args.seed)
    else:
        if any(value < 32 or value % 32 for value in (args.height, args.width)):
            parser.error("height and width must be positive multiples of 32")
        export(dataset, weights, output, args.height, args.width, args.raw_head)


if __name__ == "__main__":
    main()
