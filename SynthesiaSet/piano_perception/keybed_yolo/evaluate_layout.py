"""Score approximate white-key guides, separately from instance-mask accuracy.

This evaluator requires an explicitly provided instrument profile. It never
passes reference polygons or key counts from the scorer into reconstruction.
Review-derived profiles are permitted for a labelled, ideal-configuration
development experiment; they are not evidence of automatic profile recognition.
"""

import argparse
from collections import Counter
from dataclasses import asdict
from io import BytesIO
from math import dist
from pathlib import Path

import cv2
import numpy as np

from calibration import DECODE_CONFIDENCE, calibration_candidate
from dataset import read_json, sha256, sha256_file, write_json
from evaluate import Frame, decode, unpack_outputs
from geometry import checked_quad
from keybed_layout import BLACK_PITCH_CLASSES, KeyGuide, KeyboardProfile, LayoutPreview, reconstruct_layout


SAMPLE_DEPTH = .80
TOLERANCE = .25
UNIT = np.float32([[0, 0], [1, 0], [1, 1], [0, 1]])


def project(points, transform):
    return cv2.perspectiveTransform(np.float64(points).reshape(-1, 1, 2), transform).reshape(-1, 2)


def horizontal_interval(polygon, y):
    """Intersect a simple polygon with a horizontal line; no bounding-box proxy."""
    points = np.asarray(polygon, dtype=float)
    crossings = []
    for a, b in zip(points, np.roll(points, -1, axis=0), strict=True):
        if (a[1] <= y < b[1]) or (b[1] <= y < a[1]):
            crossings.append(float(a[0] + (y - a[1]) * (b[0] - a[0]) / (b[1] - a[1])))
    if len(crossings) != 2 or max(crossings) - min(crossings) <= 1e-8:
        raise ValueError("reference white key has no single usable interval at sample depth")
    return min(crossings), max(crossings)


def line_x_at_depth(segment, depth):
    a, b = np.asarray(segment, dtype=float)
    if not np.isfinite([a, b]).all() or abs(b[1] - a[1]) < 1e-9:
        return None
    t = (depth - a[1]) / (b[1] - a[1])
    if not 0 <= t <= 1:
        return None
    return float(a[0] + t * (b[0] - a[0]))


def score_layout(row, frame, fit, preview):
    """Measure guide positions against reviewed polygons only after inference.

    White guide lines are intersected at 80% of the *reviewed* playing-plane
    depth. Lateral error is in rectified mean white-key widths. This is a 2D
    guide-position metric, not mask IoU, black-key top geometry, or world error.
    """
    reference_quad = np.float32(next(item["polygon"] for item in row["instances"]
                                     if item["class"] == "playable_keybed"))
    checked_quad(reference_quad.tolist(), frame.source_width, frame.source_height)
    rectifier = cv2.getPerspectiveTransform(reference_quad, UNIT)
    reference_keys = sorted((item for item in row["instances"] if item["class"] != "playable_keybed"),
                            key=lambda item: item["slot"])
    if [item["slot"] for item in reference_keys] != list(range(len(reference_keys))):
        raise ValueError("reviewed physical key slots must be complete and unique")
    whites = [item for item in reference_keys if item["class"] == "white_key"]
    count = len(whites)
    if count < 2:
        raise ValueError("at least two reviewed white keys are required")
    targets = [sum(horizontal_interval(project(item["polygon"], rectifier), SAMPLE_DEPTH)) / 2
               for item in whites]
    # Validate the references even if reconstruction rejects the photo.
    if any(a >= b for a, b in zip(targets, targets[1:])):
        raise ValueError("reviewed white key intervals are not in physical order")

    guides = preview.guides
    pattern_matches = len(guides) == len(reference_keys) and all(
        guide.is_black == (item["class"] == "black_key")
        for guide, item in zip(guides, reference_keys)
    )
    white_guides = [guide for guide in guides if not guide.is_black]
    errors = []
    if len(white_guides) == count:
        for ordinal, (guide, expected) in enumerate(zip(white_guides, targets, strict=True)):
            source_segment = frame.to_source(np.asarray(guide.segment) + (frame.left, frame.top))
            x = line_x_at_depth(project(source_segment, rectifier), SAMPLE_DEPTH)
            errors.append(abs(x - expected) * count if x is not None and guide.white_ordinal == ordinal else None)
    source_quad = frame.to_source(np.asarray(fit.quad) + (frame.left, frame.top)) if fit else None
    unit = dist(reference_quad[2], reference_quad[3]) / count
    corner_errors = ([dist(a, b) / unit for a, b in zip(source_quad, reference_quad, strict=True)]
                     if fit else None)
    strict = corner_errors is not None and max(corner_errors) <= TOLERANCE
    positioned = (preview.status == "needs_confirmation" and pattern_matches and len(errors) == count
                  and all(error is not None and error <= TOLERANCE for error in errors))
    return {
        "status": preview.status, "rejection_reason": preview.reason,
        "reference_white_count": count, "guide_count": len(guides),
        "observed_guides": sum(guide.observed for guide in guides),
        "estimated_guides": sum(not guide.observed for guide in guides),
        "profile_pattern_matches": pattern_matches,
        "white_lateral_errors": errors,
        "white_max_lateral_error": max(errors) if errors and all(e is not None for e in errors) else None,
        "corner_errors": corner_errors, "strict_corners": strict,
        "all_white_guides_positioned": positioned,
        "joint_white_guides_and_corners": positioned and strict,
        "evidence": asdict(preview.evidence) if preview.evidence else None,
    }


def sparse_anchor_indices(predictions, frame, fit, white_limit=8, black_limit=5):
    """Keep at most one high-score key per equal bed interval, without labels.

    This thins reconstruction anchors only. The outline still uses the original
    model outputs. The returned indices refer to that unchanged prediction list.
    """
    if fit is None:
        return ()
    rectifier = cv2.getPerspectiveTransform(np.float32(fit.quad), UNIT)
    keep = set()
    for class_id, limit in ((1, white_limit), (2, black_limit)):
        bins = {}
        for index, item in enumerate(predictions):
            if item.class_id != class_id or not item.mask.area:
                continue
            ys, xs = np.nonzero(item.mask.values)
            x, y = project([[xs.mean() + item.mask.x - frame.left,
                             ys.mean() + item.mask.y - frame.top]], rectifier)[0]
            if 0 <= x < 1 and -.1 <= y <= 1.1:
                bucket = min(limit - 1, int(x * limit))
                if bucket not in bins or item.score > predictions[bins[bucket]].score:
                    bins[bucket] = index
        keep.update(bins.values())
    return tuple(sorted(keep))


def summarize(cases):
    total = len(cases)
    return {
        "total": total,
        "previews": sum(case["status"] == "needs_confirmation" for case in cases),
        "rejections": dict(Counter(case["rejection_reason"] for case in cases if case["status"] == "rejected")),
        **{field: sum(case[field] for case in cases) for field in
           ("all_white_guides_positioned", "strict_corners", "joint_white_guides_and_corners")},
        "observed_guides": sum(case["observed_guides"] for case in cases),
        "estimated_guides": sum(case["estimated_guides"] for case in cases),
    }


def uniform_profile_preview(profile, fit):
    """An unvalidated, outline-only comparison, not the reconstruction API."""
    if fit is None or profile.phase is None:
        return LayoutPreview("rejected", "outline and explicit pattern required")
    black = [(profile.phase + i) % 12 in BLACK_PITCH_CLASSES for i in range(profile.key_count)]
    count = sum(not value for value in black)
    transform = cv2.getPerspectiveTransform(UNIT, np.float32(fit.quad))
    ordinal, guides = 0, []
    for index, is_black in enumerate(black):
        x = (ordinal + (0 if is_black else .5)) / count
        segment = project([[x, 0], [x, .6 if is_black else 1]], transform)
        guides.append(KeyGuide((profile.phase + index) % 12,
                               profile.first_midi + index if profile.first_midi is not None else None,
                               None if is_black else ordinal, is_black, x,
                               tuple(map(tuple, segment.tolist())), None, None))
        ordinal += not is_black
    return LayoutPreview("needs_confirmation", guides=tuple(guides))


def evaluate(cache_path, profiles_path):
    cache_bytes, profile_bytes = cache_path.read_bytes(), profiles_path.read_bytes()
    cache, profiles = read_json(cache_path), read_json(profiles_path)
    if cache.get("split") not in ("val", "development-diagnostic"):
        raise ValueError("use validation or explicitly consumed development-diagnostic data")
    if not profiles.get("source") or not profiles.get("scope"):
        raise ValueError("profile provenance and evaluation scope are required")
    entries = cache["cases"]
    if not entries or len({item["id"] for item in entries}) != len(entries):
        raise ValueError("expected unique, nonempty cache cases")
    if set(profiles["profiles"]) != {item["id"] for item in entries}:
        raise ValueError("exactly one explicit configured profile per cache case is required")
    cases = []
    for entry in entries:
        row, frame = entry["row"], Frame(**entry["frame"])
        if row["id"] != entry["id"] or row["group"] != entry["group"] or row["review"]["status"] != "accepted":
            raise ValueError("cache/reference identity mismatch or unaccepted reference")
        if cache["split"] == "val" and row["split"] != "val":
            raise ValueError("non-validation row in validation cache")
        if cache["input_shape"] != [1, 3, frame.height, frame.width]:
            raise ValueError("input shape mismatch")
        path = (cache_path.parent / entry["predictions"]).resolve()
        # Development diagnostic manifests may bind prior caches by absolute path.
        data = path.read_bytes()
        if sha256(data) != entry["sha256"]:
            raise ValueError("cached prediction checksum mismatch")
        with np.load(BytesIO(data), allow_pickle=False) as arrays:
            rows, protos = unpack_outputs((arrays["rows"][None], arrays["protos"][None]))
        predictions = decode(rows, protos, frame, DECODE_CONFIDENCE)
        candidate = calibration_candidate(predictions, frame)
        profile = KeyboardProfile(**profiles["profiles"][entry["id"]])
        results = {"uniform_outline_profile": score_layout(
            row, frame, candidate.fit, uniform_profile_preview(profile, candidate.fit))}
        for name, indices in (("all_anchors", None),
                              ("sparse_8_white_5_black", sparse_anchor_indices(predictions, frame, candidate.fit))):
            preview = reconstruct_layout(predictions, frame, candidate.fit, profile, anchor_indices=indices)
            results[name] = score_layout(row, frame, candidate.fit, preview)
        cases.append({"id": entry["id"], "group": entry["group"],
                      "configured_profile": asdict(profile),
                      "outline": asdict(candidate.fit) if candidate.fit else None, **results})
    return {
        "scope": "Development only; configured profile, no independent reliability estimate.",
        "profile_source": profiles["source"], "profile_scope": profiles["scope"],
        "cache_manifest_sha256": sha256(cache_bytes), "profiles_sha256": sha256(profile_bytes),
        "model_sha256": cache["model_sha256"], "split": cache["split"],
        "code_sha256": {name: sha256_file(Path(__file__).parent / name) for name in
                        ("evaluate_layout.py", "keybed_layout.py", "keybed_geometry.py", "calibration.py",
                         "postprocess.py", "geometry.py", "evaluate.py")},
        "metric": {"white_sample_depth": SAMPLE_DEPTH, "tolerance_white_widths": TOLERANCE,
                   "denominator": "All reviewed photos, including rejected reconstructions.",
                   "exclusions": "Does not score black-key height/depth, MIDI identity, or 3D alignment."},
        "variants": {name: summarize([case[name] for case in cases])
                     for name in ("uniform_outline_profile", "all_anchors", "sparse_8_white_5_black")},
        "cases": cases,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("cache", type=Path)
    parser.add_argument("profiles", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    if args.output.exists():
        parser.error("output already exists")
    cv2.setNumThreads(2)
    report = evaluate(args.cache.resolve(), args.profiles.resolve())
    args.output.mkdir(parents=True, exist_ok=False)
    write_json(args.output / "evaluation.json", report)
    print(report["variants"])


if __name__ == "__main__":
    main()
