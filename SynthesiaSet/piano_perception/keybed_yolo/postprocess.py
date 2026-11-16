"""Confidence filtering and mask-based suppression for decoded key instances."""

from math import hypot, isfinite
from typing import TYPE_CHECKING

import numpy as np

from geometry import CLASSES

if TYPE_CHECKING:
    from evaluate import Instance, Mask


def mask_intersection(first: "Mask", second: "Mask") -> int:
    """Count shared foreground pixels in input-image coordinates."""
    x1, y1 = max(first.x, second.x), max(first.y, second.y)
    x2 = min(first.x + first.values.shape[1], second.x + second.values.shape[1])
    y2 = min(first.y + first.values.shape[0], second.y + second.values.shape[0])
    if x1 >= x2 or y1 >= y2 or not first.area or not second.area:
        return 0
    a = first.values[y1 - first.y:y2 - first.y, x1 - first.x:x2 - first.x]
    b = second.values[y1 - second.y:y2 - second.y, x1 - second.x:x2 - second.x]
    return int(np.count_nonzero(np.logical_and(a, b)))


def duplicate_overlaps(kept: "Mask", candidate: "Mask") -> tuple[float, float]:
    """Return mask IoU and coverage of a similarly sized, smaller candidate."""
    shared = mask_intersection(kept, candidate)
    if not shared:
        return 0.0, 0.0
    iou = shared / (kept.area + candidate.area - shared)
    coverage = shared / candidate.area if candidate.area <= kept.area <= 2 * candidate.area else 0.0
    return iou, coverage


def _bed_distance(key: "Mask", bed: "Mask") -> float:
    """Measure the gap between mask windows in estimated keybed thicknesses."""
    bed_height, bed_width = bed.values.shape
    key_height, key_width = key.values.shape
    x_gap = max(bed.x - (key.x + key_width), key.x - (bed.x + bed_width), 0)
    y_gap = max(bed.y - (key.y + key_height), key.y - (bed.y + bed_height), 0)
    return hypot(x_gap, y_gap) * max(bed_width, bed_height) / bed.area


def postprocess_instances(
    predictions: list["Instance"],
    thresholds: tuple[float, float, float],
    *,
    mask_iou: float | None = None,
    containment: float | None = None,
    max_bed_distance: float | None = None,
) -> list["Instance"]:
    """Filter in confidence order using bed, white-key and black-key cutoffs.

    Optional suppression compares only masks of the same key class. The
    containment check can remove a lower-scoring subset of an existing key
    mask while limiting the size difference to a factor of two.

    With a surviving bed and a distance limit, reject key candidates whose
    mask windows are too far from the highest-confidence bed's mask window.
    Distance is measured in estimated bed thicknesses (foreground area
    divided by the longest dimension of the bed mask window).
    """
    if len(thresholds) != len(CLASSES) or not all(0 <= score <= 1 for score in thresholds):
        raise ValueError("expected a confidence in [0, 1] for each class")
    if any(value is not None and not 0 < value <= 1 for value in (mask_iou, containment)):
        raise ValueError("overlap thresholds must be in (0, 1]")
    if max_bed_distance is not None and (not isfinite(max_bed_distance) or max_bed_distance < 0):
        raise ValueError("bed distance must be finite and nonnegative")

    cutoffs = np.asarray(thresholds, dtype=np.float32)
    eligible = sorted(
        (item for item in predictions if item.mask.area and item.score > cutoffs[item.class_id]),
        key=lambda item: -item.score,
    )
    bed = next((item.mask for item in eligible if item.class_id == 0), None)
    suppress_duplicates = mask_iou is not None or containment is not None
    kept = []
    kept_keys = {1: [], 2: []}
    for candidate in eligible:
        class_id = candidate.class_id
        if class_id == 0:
            kept.append(candidate)
            continue
        if (bed is not None and max_bed_distance is not None
                and _bed_distance(candidate.mask, bed) > max_bed_distance):
            continue
        if suppress_duplicates and any(
            (mask_iou is not None and iou >= mask_iou) or (containment is not None and coverage >= containment)
            for iou, coverage in (duplicate_overlaps(earlier.mask, candidate.mask) for earlier in kept_keys[class_id])
        ):
            continue
        kept_keys[class_id].append(candidate)
        kept.append(candidate)
    return kept
