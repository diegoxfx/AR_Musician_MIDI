"""Freeze and replay a CPU-only corner comparison on a fixed real-photo cohort."""

import argparse
from collections import Counter
from dataclasses import asdict
import hashlib
from importlib.metadata import version
from io import BytesIO
import json
import os
from pathlib import Path
import platform
import statistics
import tempfile
from time import perf_counter

from resource_budget import ResourceBudget

INPUT_SIZE = 960
CPU_THREADS = 2
BASELINES = ("segmentation", "pose")
VARIANTS = (*BASELINES, "refined")
ARTIFACTS = ("references", "segmentation", "pose", "refiner", "selection")
SCOPES = {
    "fresh-holdout": {"test"},
    "consumed-diagnostic": {"test", "diagnostic", "development-diagnostic"},
    "development-validation": {"val"},
}
SOURCE_FILES = (
    "corner_refiner_evaluate.py", "corner_refiner.py", "corner_pose.py",
    "calibration.py", "evaluate.py", "fit.py", "dataset.py", "geometry.py",
    "keybed_geometry.py", "postprocess.py", "propose.py", "resource_budget.py",
)
PACKAGES = ("ultralytics", "torch", "onnxruntime", "opencv-python", "Pillow", "numpy", "psutil")


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def binding(path):
    path = path.resolve(strict=True)
    return {"path": str(path), "sha256": digest(path)}


def require_binding(record):
    path = Path(record["path"])
    if not path.is_absolute() or digest(path) != record["sha256"]:
        raise ValueError(f"frozen checksum mismatch for {path}")
    return path


def read(path):
    return json.loads(path.read_bytes().decode("utf-8-sig"))


def versions():
    return {"python": platform.python_version(), **{name: version(name) for name in PACKAGES}}


def source_paths():
    return {name: Path(__file__).resolve().parent / name for name in SOURCE_FILES}


def execution_settings():
    from calibration import DECODE_CONFIDENCE, VALIDATION_CONFIG
    from corner_pose import SCORE_FLOOR

    segmentation = asdict(VALIDATION_CONFIG)
    segmentation["thresholds"] = list(segmentation["thresholds"])
    return {"device": "cpu", "threads": CPU_THREADS, "input_size": INPUT_SIZE,
            "package_versions": versions(), "segmentation_config": segmentation,
            "segmentation_score_floor": DECODE_CONFIDENCE, "pose_score_floor": SCORE_FLOOR}


def chosen_refiner(selection_path, model_hash, source_hash):
    from corner_refiner import RefinementConfig

    selection = read(selection_path)
    if selection["checkpoint_sha256"] != model_hash or selection["source_code_sha256"] != source_hash:
        raise ValueError("refiner checkpoint or source differs from validation selection")
    if selection["seed_variant"] not in BASELINES:
        raise ValueError("validation selection requires one named coarse variant")
    return selection, RefinementConfig(**selection["config"])


def reference_rows(path, scope):
    from dataset import indexed
    from geometry import checked_quad

    ledger = read(path)
    rows = ledger["images"]
    if ledger.get("schema") != 1 or not isinstance(rows, list) or not rows:
        raise ValueError("expected a nonempty schema 1 corner reference ledger")
    indexed(rows)
    splits = {row["split"] for row in rows}
    if len(splits) != 1 or not splits <= SCOPES[scope]:
        raise ValueError("reference rows must share one split consistent with evaluation scope")
    if len({row["image_sha256"] for row in rows}) != len(rows):
        raise ValueError("duplicate reference image checksum")
    for row in rows:
        count = row["expected_white_count"]
        width, height = row["width"], row["height"]
        if any(type(value) is not int or value < 1 for value in (count, width, height)):
            raise ValueError(f"{row['id']}: invalid white count or image dimensions")
        checked_quad(row["corners"], width, height)
        if not Path(row["image"]).is_absolute():
            raise ValueError(f"{row['id']}: reference image path must be absolute")
    return rows


def open_image(row):
    from PIL import Image

    data = Path(row["image"]).read_bytes()
    if hashlib.sha256(data).hexdigest() != row["image_sha256"]:
        raise ValueError(f"{row['id']}: reference image checksum mismatch")
    with Image.open(BytesIO(data)) as original:
        if original.size != (row["width"], row["height"]):
            raise ValueError(f"{row['id']}: reference image dimensions mismatch")
        return original.convert("RGB")


def freeze(output, scope, paths):
    if output.exists():
        raise ValueError(f"plan already exists: {output}")
    artifacts = {name: binding(paths[name]) for name in ARTIFACTS}
    code = {name: binding(path) for name, path in source_paths().items()}
    selection, config = chosen_refiner(paths["selection"], artifacts["refiner"]["sha256"],
                                        code["corner_refiner.py"]["sha256"])
    rows = reference_rows(paths["references"], scope)
    for row in rows:
        with open_image(row):
            pass
    checked_plan = {
        "schema": 1, "scope": scope, "split": rows[0]["split"], "case_ids": [row["id"] for row in rows],
        "execution": execution_settings(),
        "artifacts": artifacts, "source_code": code,
        "refinement": {"seed_variant": selection["seed_variant"], "epoch": selection["epoch"],
                       "config": asdict(config)},
    }
    for record in (*artifacts.values(), *code.values()):
        require_binding(record)
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("x", encoding="utf-8") as stream:
        stream.write(json.dumps(checked_plan, indent=2, ensure_ascii=False) + "\n")
    return digest(output)


def verify_plan(path, checksum):
    if digest(path) != checksum.lower():
        raise ValueError("frozen plan checksum mismatch")
    plan = read(path)
    if plan.get("schema") != 1 or plan.get("scope") not in SCOPES:
        raise ValueError("unknown corner evaluation plan schema or scope")
    if set(plan["artifacts"]) != set(ARTIFACTS) or set(plan["source_code"]) != set(SOURCE_FILES):
        raise ValueError("plan does not bind every required model, reference and source file")
    paths = {name: require_binding(plan["artifacts"][name]) for name in ARTIFACTS}
    for name, expected in source_paths().items():
        if require_binding(plan["source_code"][name]) != expected:
            raise ValueError(f"plan binds a different local source for {name}")
    if plan["execution"] != execution_settings():
        raise ValueError("frozen execution environment differs")
    selection, config = chosen_refiner(paths["selection"], plan["artifacts"]["refiner"]["sha256"],
                                        plan["source_code"]["corner_refiner.py"]["sha256"])
    if plan["refinement"] != {"seed_variant": selection["seed_variant"], "epoch": selection["epoch"],
                              "config": asdict(config)}:
        raise ValueError("frozen refinement configuration differs from validation selection")
    rows = reference_rows(paths["references"], plan["scope"])
    if plan["split"] != rows[0]["split"] or plan["case_ids"] != [row["id"] for row in rows]:
        raise ValueError("frozen reference cohort changed")
    for row in rows:
        if digest(Path(row["image"])) != row["image_sha256"]:
            raise ValueError(f"{row['id']}: reference image checksum mismatch")
    return plan, paths, rows, config


def checked_seed(points, size):
    from geometry import checked_quad

    try:
        checked_quad(points, *size)
    except ValueError as error:
        return None, str(error)
    return points, None


def segmentation_seed(session, image):
    import numpy as np

    from calibration import DECODE_CONFIDENCE, VALIDATION_CONFIG, calibration_candidate
    from evaluate import Frame, decode, unpack_outputs
    from fit import letterbox

    width, height = image.size
    pixels, scale, left, top = letterbox(image, INPUT_SIZE, INPUT_SIZE)
    frame = Frame(INPUT_SIZE, INPUT_SIZE, left, top, round(width * scale), round(height * scale), width, height)
    outputs = session.run(None, {session.get_inputs()[0].name: pixels})
    instances = decode(*unpack_outputs(outputs), frame, DECODE_CONFIDENCE)
    candidate = calibration_candidate(instances, frame, config=VALIDATION_CONFIG)
    if candidate.fit is None:
        return {"quad": None, "raw_ordered_corners": None, "rejection": candidate.fit_failure,
                "roi_detection_index": candidate.roi_detection_index}
    fitted = candidate.fit
    # KeybedFit uses content-local coordinates, whereas Frame.to_source expects padded inputs.
    raw = frame.to_source(np.asarray(fitted.quad) + (frame.left, frame.top)).tolist()
    quad, rejection = checked_seed(raw, image.size)
    return {"quad": quad, "raw_ordered_corners": raw, "rejection": rejection, "method": fitted.method,
            "object_confidence": float(fitted.confidence), "detection_index": int(fitted.detection_index),
            "roi_detection_index": candidate.roi_detection_index}


def pose_seed(model, image):
    import numpy as np

    from corner_pose import KPT_SHAPE, infer_image

    keypoints, confidence = infer_image(model, image, INPUT_SIZE)
    result = {"quad": None, "raw_ordered_corners": None, "rejection": "no_detection",
              "object_confidence": confidence, "keypoint_confidence": None}
    if keypoints is None:
        return result, keypoints
    if (keypoints.shape != KPT_SHAPE or not np.isfinite(keypoints).all()
            or not 0 <= confidence <= 1 or not np.all((0 <= keypoints[:, 2]) & (keypoints[:, 2] <= 1))):
        return {**result, "rejection": "invalid_pose_output"}, keypoints
    raw = keypoints[:, :2].tolist()
    quad, rejection = checked_seed(raw, image.size)
    return {"quad": quad, "raw_ordered_corners": raw, "rejection": rejection,
            "object_confidence": confidence, "keypoint_confidence": keypoints[:, 2].tolist()}, keypoints


def inference(image, segmentation, pose, refiner, seed_variant, config):
    import numpy as np

    from corner_refiner import refine_quad

    start = perf_counter()
    segmented = segmentation_seed(segmentation, image)
    segmentation_seconds = perf_counter() - start
    start = perf_counter()
    posed, raw_pose = pose_seed(pose, image)
    pose_seconds = perf_counter() - start
    variants = {"segmentation": segmented, "pose": posed}
    start = perf_counter()
    local = refine_quad(refiner, np.asarray(image), variants[seed_variant]["quad"], config)
    local_seconds = perf_counter() - start
    variants["refined"] = {**local, "coarse_seed": seed_variant}
    times = {"segmentation": segmentation_seconds, "pose": pose_seconds, "refiner_only": local_seconds,
             "refined_pipeline": (segmentation_seconds if seed_variant == "segmentation" else pose_seconds)
                                 + local_seconds}
    return variants, raw_pose, times


def score_case(row, results, raw_pose):
    from corner_pose import score_pose
    from corner_refiner import score
    from geometry import CORNER_NAMES

    reference = {**row, "white_keys": row["expected_white_count"], "group": row.get("group", row["id"])}
    scored = {name: score(reference, result) for name, result in results.items()}
    pose_result = score_pose(reference, raw_pose, results["pose"]["object_confidence"])
    if pose_result["rejection"] != scored["pose"]["rejection"]:
        raise ValueError(f"{row['id']}: pose geometry and scorer disagree")
    scored["pose"]["raw_errors_white_widths_by_corner"] = pose_result["errors_white_widths"]
    for variant in scored.values():
        errors = variant["errors_white_widths"]
        variant["errors_white_widths_by_corner"] = dict(zip(CORNER_NAMES, errors, strict=True)) if errors else None
        variant["max_error_white_widths"] = max(errors) if errors else None
    return scored


def overlay(path, image, reference, prediction):
    from PIL import ImageDraw

    pictured = image.copy()
    pictured.thumbnail((1200, 1200))
    scale_x, scale_y = pictured.width / image.width, pictured.height / image.height
    draw = ImageDraw.Draw(pictured)
    for quad, color in ((reference, "#15df68"), (prediction, "#f42c38")):
        if quad is None:
            continue
        points = [(x * scale_x, y * scale_y) for x, y in quad]
        draw.line([*points, points[0]], fill=color, width=2)
        for x, y in points:
            draw.ellipse((x - 3, y - 3, x + 3, y + 3), fill=color)
    pictured.save(path)


def load_models(paths, settings):
    import onnxruntime as ort
    import torch

    from corner_pose import load_pose
    from corner_refiner import CornerHeatmaps

    options = ort.SessionOptions()
    options.intra_op_num_threads = CPU_THREADS
    options.inter_op_num_threads = CPU_THREADS
    segmentation = ort.InferenceSession(str(paths["segmentation"]), sess_options=options,
                                        providers=["CPUExecutionProvider"])
    (source,) = segmentation.get_inputs()
    if source.shape != [1, 3, INPUT_SIZE, INPUT_SIZE] or source.type != "tensor(float)":
        raise ValueError("segmentation checkpoint must accept a fixed 960-square FP32 tensor")
    pose = load_pose(paths["pose"], settings).to("cpu")
    refiner = CornerHeatmaps().eval()
    refiner.load_state_dict(torch.load(paths["refiner"], map_location="cpu", weights_only=True))
    return segmentation, pose, refiner


def evaluate(plan_path, plan_hash, output, overlays=False):
    if output.exists():
        raise ValueError(f"evaluation output already exists: {output}")
    budget = ResourceBudget(output.parent / f"{output.name}-resources.jsonl")
    budget.configure_torch("cpu")
    budget.torch.set_num_threads(CPU_THREADS)
    plan, paths, rows, config = verify_plan(plan_path, plan_hash)

    import cv2

    from corner_refiner import summary
    from dataset import write_json
    from geometry import CORNER_NAMES

    cv2.setNumThreads(1)
    models = load_models(paths, output.parent)
    seed_variant = plan["refinement"]["seed_variant"]
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=output.parent) as temporary:
        target = Path(temporary) / "evaluation"
        target.mkdir()
        if overlays:
            for variant in VARIANTS:
                (target / variant).mkdir()
        cases = []
        start = perf_counter()
        for row in rows:
            with open_image(row) as image:
                results, raw_pose, elapsed = inference(image, *models, seed_variant, config)
                variants = score_case(row, results, raw_pose)
                cases.append({"id": row["id"], "reference": row, "variants": variants,
                              "cpu_inference_seconds": elapsed})
                if overlays:
                    for variant, result in variants.items():
                        overlay(target / variant / f"{row['id']}.png", image, row["corners"], result["quad"])
        wall = perf_counter() - start
        reports = {}
        for variant in VARIANTS:
            scored = [case["variants"][variant] for case in cases]
            reports[variant] = {**summary(scored),
                                "rejection_reasons": dict(Counter(item["rejection"] for item in scored
                                                                 if item["rejection"]))}
        timings = {}
        for key in cases[0]["cpu_inference_seconds"]:
            values = [case["cpu_inference_seconds"][key] for case in cases]
            timings[key] = {"total_seconds": sum(values), "median_seconds": statistics.median(values)}
        verify_plan(plan_path, plan_hash)
        report = {
            "schema": 1, "plan_sha256": plan_hash.lower(), "plan": plan, "corner_order": CORNER_NAMES,
            "scope": plan["scope"], "split": plan["split"], "variants": reports, "cases": cases,
            "strict_tolerance_white_widths": .25, "rejections_in_denominator": True,
            "timing_scope": "Desktop CPU. Per-model timings include preprocessing and geometric postprocessing.",
            "cpu_timings": timings, "case_loop_wall_seconds": wall, "overlays": overlays,
            "resource_policy": budget.cpu,
        }
        write_json(target / "evaluation.json", report)
        if output.exists():
            raise ValueError(f"evaluation output already exists: {output}")
        target.rename(output)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    freeze_command = commands.add_parser("freeze")
    freeze_command.add_argument("plan", type=Path)
    freeze_command.add_argument("--scope", required=True, choices=SCOPES)
    for name in ARTIFACTS:
        freeze_command.add_argument(f"--{name}", required=True, type=Path)
    replay = commands.add_parser("evaluate")
    replay.add_argument("plan", type=Path)
    replay.add_argument("output", type=Path)
    replay.add_argument("--plan-sha256", required=True)
    replay.add_argument("--overlays", action="store_true")
    args = parser.parse_args()
    for name in ("OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS"):
        os.environ[name] = str(CPU_THREADS)
    if args.command == "freeze":
        ResourceBudget()
        print(freeze(args.plan.resolve(), args.scope, {name: getattr(args, name) for name in ARTIFACTS}))
    else:
        report = evaluate(args.plan.resolve(), args.plan_sha256, args.output.resolve(), args.overlays)
        print(json.dumps(report["variants"], indent=2))


if __name__ == "__main__":
    main()
