"""Compare frozen calibration candidates with the original validation operating point."""

import argparse
from collections import Counter
from dataclasses import asdict
from io import BytesIO
import json
from math import dist
from pathlib import Path
import tempfile

import cv2
import numpy as np

from calibration import DECODE_CONFIDENCE, VALIDATION_CONFIG, calibration_candidate, key_detection_order
from dataset import sha256, write_json
from evaluate import (
    END_TO_END, Frame, confidence_matches, corners_and_order, counts_at, decode,
    distribution, pairwise, reference_instances, summarize_counts, unpack_outputs,
)
from geometry import CLASSES, CORNER_NAMES


BASELINE_CONFIDENCE = .30
MATCH_IOU = .50
CORNER_TOLERANCE = .25


def _baseline_geometry(row, frame, references, predictions, indices, kept, matches):
    result = corners_and_order(row, frame, references, kept, matches)
    source_quad = result["fitted"]
    local_quad = (frame.to_input(source_quad) - (frame.left, frame.top)).tolist() if source_quad else None
    selected = next((i for i in indices if predictions[i].class_id == 0), None)
    return {
        "quad_content_pixels": local_quad,
        "quad_source_pixels": source_quad,
        "white_width_error": result["white_width_error"],
        "fit_failure": result["failure"],
        "selected_detection_index": selected if local_quad else None,
        "roi_detection_index": None,
        "method": "original-mask-quad" if local_quad else None,
        "key_detection_order": key_detection_order(predictions, indices, local_quad, frame),
    }


def _candidate_geometry(candidate, row, frame, references):
    fit = candidate.fit
    local_quad = fit.quad if fit else None
    source_quad = errors = None
    if fit:
        source_quad = frame.to_source(np.asarray(local_quad) + (frame.left, frame.top)).tolist()
        truth = next(item["polygon"] for item in row["instances"] if item["class"] == CLASSES[0])
        unit = dist(truth[2], truth[3]) / sum(item.class_id == 1 for item in references)
        errors = {name: dist(actual, expected) / unit
                  for name, actual, expected in zip(CORNER_NAMES, source_quad, truth, strict=True)}
    return {
        "quad_content_pixels": local_quad,
        "quad_source_pixels": source_quad,
        "white_width_error": errors,
        "fit_failure": candidate.fit_failure,
        "selected_detection_index": fit.detection_index if fit else None,
        "roi_detection_index": candidate.roi_detection_index,
        "method": fit.method if fit else None,
        "fit_details": asdict(fit) if fit else None,
        "key_detection_order": candidate.key_detection_order,
    }


def score_variant(row, frame, references, predictions, candidate=None):
    """Evaluate one variant against reviewed masks and physical key slots."""
    indices = (candidate.kept_detection_indices if candidate is not None else
               tuple(index for index, item in enumerate(predictions) if item.score > np.float32(BASELINE_CONFIDENCE)))
    kept = [predictions[index] for index in indices]
    overlaps = pairwise(references, kept, lambda reference, prediction: reference.mask.iou(prediction.mask))
    matches = confidence_matches(overlaps, MATCH_IOU)
    counts = counts_at(references, kept, matches, 0)
    duplicate_counts = Counter(
        item.class_id for index, item in enumerate(kept)
        if index not in matches and (overlaps[:, index] >= MATCH_IOU).any()
    )
    if candidate is None:
        geometry = _baseline_geometry(row, frame, references, predictions, indices, kept, matches)
    else:
        geometry = _candidate_geometry(candidate, row, frame, references)

    matched = {
        indices[index]: {"class": CLASSES[kept[index].class_id],
                         "slot": references[target].slot, "mask_iou": iou}
        for index, (target, iou) in matches.items()
    }
    slot_by_detection = {index: item["slot"] for index, item in matched.items() if item["slot"] is not None}
    slots = [slot_by_detection[index] for index in geometry["key_detection_order"] or ()
             if index in slot_by_detection]
    inversions = (sum(left > right for index, left in enumerate(slots) for right in slots[index + 1:])
                  if len(slots) >= 2 else None)
    errors = geometry["white_width_error"]
    strict = errors is not None and all(error <= CORNER_TOLERANCE for error in errors.values())
    exact = not any(counts[name][field] for name in CLASSES[1:] for field in ("missing", "extra"))
    exact_ordered = exact and inversions == 0 and len(slots) == sum(counts[name]["expected"] for name in CLASSES[1:])
    return {
        "classes": counts,
        "duplicate_predictions": {name: duplicate_counts[index] for index, name in enumerate(CLASSES[1:], start=1)},
        "kept_detection_indices": indices,
        "matches_by_detection_index": matched,
        "corners": geometry,
        "matched_slot_order": slots if inversions is not None else None,
        "order_inversions": inversions,
        "exact_keys": exact,
        "strict_corners": strict,
        "exact_keys_and_ordered": exact_ordered,
        "exact_keys_strict_corners_and_ordered": exact_ordered and strict,
    }


def summarize_variant(cases, *, roi_enabled=False):
    image_count = len(cases)
    corners = [case["corners"] for case in cases]
    fitted = [item["white_width_error"] for item in corners if item["white_width_error"] is not None]
    ordered = [case["order_inversions"] for case in cases if case["order_inversions"] is not None]
    results = {
        field: {"passed": sum(case[field] for case in cases), "total": image_count,
                "rate": sum(case[field] for case in cases) / image_count}
        for field in ("exact_keys", "strict_corners", "exact_keys_and_ordered", "exact_keys_strict_corners_and_ordered")
    }
    return {
        "classes": summarize_counts([case["classes"] for case in cases]),
        "duplicate_predictions": {
            name: sum(case["duplicate_predictions"][name] for case in cases) for name in CLASSES[1:]
        },
        **results,
        "geometry": {
            "fitted": len(fitted), "rejected": image_count - len(fitted), "total": image_count,
            "coverage": len(fitted) / image_count,
            "fit_methods": dict(Counter(item["method"] for item in corners if item["method"])),
            "rejection_reasons": dict(Counter(item["fit_failure"] for item in corners if item["fit_failure"])),
            "fitted_with_different_roi_detection": sum(
                item["selected_detection_index"] is not None and item["roi_detection_index"] is not None
                and item["selected_detection_index"] != item["roi_detection_index"] for item in corners
            ) if roi_enabled else None,
            "fitted_without_roi_detection": sum(
                item["selected_detection_index"] is not None and item["roi_detection_index"] is None for item in corners
            ) if roi_enabled else None,
            "per_corner_fitted": {name: distribution([errors[name] for errors in fitted]) for name in CORNER_NAMES},
            "mean_corner_error_per_fit": distribution([float(np.mean(list(errors.values()))) for errors in fitted]),
            "worst_corner_error_per_fit": distribution([max(errors.values()) for errors in fitted]),
        },
        "physical_order": {"observed": len(ordered), "unobserved": image_count - len(ordered),
                           "with_inversions": sum(value > 0 for value in ordered)},
    }


def evaluate_cache(manifest_path):
    """Replay a cache of reviewed positive VAL photos without accessing model or image files."""
    manifest_path = manifest_path.resolve()
    manifest_bytes = manifest_path.read_bytes()
    manifest = json.loads(manifest_bytes.decode("utf-8-sig"))
    entries = manifest["cases"]
    if manifest.get("split") != "val" or not entries or any(entry["row"]["split"] != "val" for entry in entries):
        raise ValueError("a nonempty validation-only prediction cache is required")
    if any(entry["row"]["review"]["status"] != "accepted" for entry in entries):
        raise ValueError("the calibration comparison requires accepted positive photos")
    if len({entry["id"] for entry in entries}) != len(entries):
        raise ValueError("duplicate case IDs in the validation cache")

    cases = []
    for entry in entries:
        row = entry["row"]
        frame = Frame(**entry["frame"])
        if (row["id"] != entry["id"] or row["group"] != entry["group"]
                or manifest["input_shape"] != [1, 3, frame.height, frame.width]):
            raise ValueError(f"{entry['id']}: case metadata does not match the validation cache")
        path = (manifest_path.parent / entry["predictions"]).resolve()
        if path.parent != manifest_path.parent or path.suffix != ".npz":
            raise ValueError(f"{entry['id']}: expected a prediction .npz next to the manifest")
        data = path.read_bytes()
        if sha256(data) != entry["sha256"]:
            raise ValueError(f"{entry['id']}: cached prediction hash mismatch")
        with np.load(BytesIO(data), allow_pickle=False) as tensors:
            rows, protos = unpack_outputs((tensors["rows"][None], tensors["protos"][None]))
        predictions = decode(rows, protos, frame, DECODE_CONFIDENCE)
        candidate = calibration_candidate(predictions, frame)
        references = reference_instances(row, frame)
        cases.append({
            "id": entry["id"], "group": entry["group"], "predictions_sha256": entry["sha256"],
            "baseline": score_variant(row, frame, references, predictions),
            "candidate": score_variant(row, frame, references, predictions, candidate),
        })
    return {
        "split": "val",
        "scope": "Positive validation photos used for configuration selection; no independent reliability estimate.",
        "candidate_status": "Validation-selected calibration candidate requiring review.",
        "cache_manifest_sha256": sha256(manifest_bytes),
        "model_sha256": manifest["model_sha256"],
        "dataset_provenance_sha256": manifest["dataset_provenance_sha256"],
        "input_shape": manifest["input_shape"],
        "output_layout": END_TO_END,
        "image_count": len(cases),
        "group_count": len({case["group"] for case in cases}),
        "decode_confidence": DECODE_CONFIDENCE,
        "baseline_confidence": BASELINE_CONFIDENCE,
        "candidate_config": asdict(VALIDATION_CONFIG),
        "metrics": {
            "matched_at_mask_iou": MATCH_IOU,
            "match_method": "Confidence-order one-to-one same-class mask matches.",
            "duplicate_prediction": "An unmatched key prediction with a same-class reference mask at or above match IoU.",
            "strict_corner_max_white_widths": CORNER_TOLERANCE,
            "corner_error_unit": "Mean reviewed front-edge white-key width in source pixels.",
            "image_rate_denominator": "All validation photos, including rejected fits.",
            "detection_index": "Index in the decoded list before calibration filtering.",
            "key_detection_order": "Inferred left-to-right order of kept key mask centroids in the fitted quad.",
            "matched_slot_order": "Reviewed physical slots of matched detections in inferred centroid order.",
        },
        "variants": {
            "baseline": summarize_variant([case["baseline"] for case in cases]),
            "candidate": summarize_variant([case["candidate"] for case in cases],
                                           roi_enabled=VALIDATION_CONFIG.max_bed_distance is not None),
        },
        "cases": cases,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("output", type=Path, help="new directory for evaluation.json")
    args = parser.parse_args()
    if args.output.exists():
        parser.error(f"output already exists: {args.output}")
    cv2.setNumThreads(2)
    report = evaluate_cache(args.manifest)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=args.output.parent) as temporary:
        target = Path(temporary) / "evaluation"
        target.mkdir()
        write_json(target / "evaluation.json", report)
        target.rename(args.output)
    for name, variant in report["variants"].items():
        joint = variant["exact_keys_strict_corners_and_ordered"]
        print(f"{name}: {joint['passed']}/{joint['total']} exact keys, strict corners and ordered")


if __name__ == "__main__":
    main()
