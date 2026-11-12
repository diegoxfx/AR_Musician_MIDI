"""Combine key filtering and quadrilateral fitting into a calibration candidate."""

from collections import defaultdict, deque
from dataclasses import dataclass
from typing import TYPE_CHECKING

import numpy as np

from evaluate import order_in_quad
from keybed_geometry import KeybedFit, select_keybed
from postprocess import postprocess_instances

if TYPE_CHECKING:
    from evaluate import Frame, Instance


DECODE_CONFIDENCE = .001


@dataclass(frozen=True)
class CalibrationConfig:
    thresholds: tuple[float, float, float] = (.30, .125, .15)
    mask_iou: float | None = .30
    containment: float | None = None
    max_bed_distance: float | None = 1.0
    bed_confidence: float = .025
    white_confidence: float = .30


VALIDATION_CONFIG = CalibrationConfig()


@dataclass(frozen=True)
class CalibrationCandidate:
    """Prediction indices refer to the decoded list before filtering."""

    kept_detection_indices: tuple[int, ...]
    roi_detection_index: int | None
    fit: KeybedFit | None
    fit_failure: str | None
    key_detection_order: tuple[int, ...] | None


def key_detection_order(predictions, indices, quad, frame):
    """Sort kept key mask centroids by x in a rectified content-local quad."""
    if quad is None:
        return None
    centroids = []
    for index in indices:
        item = predictions[index]
        if item.class_id != 0 and item.mask.area:
            ys, xs = np.nonzero(item.mask.values)
            centroids.append((xs.mean() + item.mask.x - frame.left,
                              ys.mean() + item.mask.y - frame.top, index))
    order = order_in_quad(quad, centroids)["matched_slot_order"]
    return tuple(order) if order is not None else None


def calibration_candidate(
    predictions: list["Instance"],
    frame: "Frame",
    config: CalibrationConfig = VALIDATION_CONFIG,
) -> CalibrationCandidate:
    """Build a candidate from decoded model masks and letterbox metadata.

    The distance filter uses the strongest bed surviving the instance cutoff
    as its region of interest. Geometry independently inspects the unfiltered
    predictions and may select another bed. Its quad uses content-local pixels.
    Key order is inferred from centroids and contains detection indices. This
    result carries no claim of complete keys, correct physical slots, or
    readiness for automatic acceptance.
    """
    if not all(0 <= confidence <= 1 for confidence in (config.bed_confidence, config.white_confidence)):
        raise ValueError("geometry confidences must be in [0, 1]")
    kept = postprocess_instances(
        predictions, config.thresholds, mask_iou=config.mask_iou,
        containment=config.containment, max_bed_distance=config.max_bed_distance,
    )
    original = defaultdict(deque)
    for index, item in enumerate(predictions):
        original[id(item)].append(index)
    indices = tuple(original[id(item)].popleft() for item in kept)
    roi = next((index for index in indices if predictions[index].class_id == 0), None)
    if config.max_bed_distance is None:
        roi = None
    try:
        fit = select_keybed(predictions, frame, bed_confidence=config.bed_confidence,
                            white_confidence=config.white_confidence)
    except ValueError as exc:
        return CalibrationCandidate(indices, roi, None, str(exc), None)
    return CalibrationCandidate(indices, roi, fit, None, key_detection_order(predictions, indices, fit.quad, frame))
