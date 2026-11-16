"""Preview a keyboard layout from a supported bed and sparse key detections."""

from dataclasses import dataclass
from math import isfinite
from numbers import Integral, Real
from typing import Literal, Sequence, TYPE_CHECKING

import cv2
import numpy as np

from geometry import checked_quad
from keybed_geometry import (
    MIN_BED_AREA, MIN_BED_IOU, STRONG_BED_CONFIDENCE, UNIT_QUAD, KeybedFit,
    _contour, _largest_component, _project, _quad_overlap, _touches_edge, select_keybed,
)

if TYPE_CHECKING:
    from evaluate import Frame, Instance


BLACK_PITCH_CLASSES = frozenset((1, 3, 6, 8, 10))
MIN_WHITE_ANCHORS = 5
MAX_END_OFFSET = .42  # White-key widths, relative to the respective bed ends.
MAX_WHITE_RESIDUAL = .26  # White-key widths, relative to a fitted center.
END_OFFSETS = np.linspace(-MAX_END_OFFSET, MAX_END_OFFSET, 9)


def _integer(value):
    return isinstance(value, Integral) and not isinstance(value, bool)


def _confidence(value):
    return isinstance(value, Real) and not isinstance(value, bool) and isfinite(value) and 0 <= value <= 1


@dataclass(frozen=True)
class KeyboardProfile:
    """Explicit physical range. A pitch class establishes pattern phase only."""

    key_count: int
    start_pitch_class: int | None = None
    first_midi: int | None = None

    def __post_init__(self):
        if not _integer(self.key_count) or not 1 <= self.key_count <= 128:
            raise ValueError("key_count must be an integer in [1, 128]")
        if self.first_midi is not None and (not _integer(self.first_midi)
                                           or not 0 <= self.first_midi <= 128 - self.key_count):
            raise ValueError("keyboard MIDI range must lie in [0, 127]")
        if self.start_pitch_class is not None:
            if not _integer(self.start_pitch_class) or not 0 <= self.start_pitch_class < 12:
                raise ValueError("start_pitch_class must be an integer in [0, 11]")
            if self.first_midi is not None and self.first_midi % 12 != self.start_pitch_class:
                raise ValueError("start_pitch_class disagrees with first_midi")
        phase = self.phase
        if phase is not None and (phase in BLACK_PITCH_CLASSES
                                  or (phase + self.key_count - 1) % 12 in BLACK_PITCH_CLASSES):
            raise ValueError("keyboard profile must begin and end on white keys")

    @property
    def phase(self):
        return self.first_midi % 12 if self.first_midi is not None else self.start_pitch_class


CT_S200_PROFILE = KeyboardProfile(61, first_midi=36)


@dataclass(frozen=True)
class LayoutConfig:
    white_confidence: float = .35
    black_confidence: float = .50

    def __post_init__(self):
        if not all(_confidence(value) for value in (self.white_confidence, self.black_confidence)):
            raise ValueError("key confidence thresholds must be finite and in [0, 1]")


@dataclass(frozen=True)
class KeyGuide:
    pitch_class: int
    midi_note: int | None
    white_ordinal: int | None
    is_black: bool
    normalized_x: float
    segment: tuple[tuple[float, float], tuple[float, float]]
    detection_index: int | None
    detection_score: float | None

    @property
    def observed(self):
        return self.detection_index is not None


@dataclass(frozen=True)
class LayoutEvidence:
    """Detection scores and geometric support are evidence, not slot probabilities."""

    white_spacing: float
    left_boundary: float
    right_boundary: float
    white_anchor_indices: tuple[int, ...]
    black_anchor_indices: tuple[int, ...]
    white_anchor_span: float
    white_residual_rms: float
    bed_detection_index: int
    bed_iou: float
    bed_edge_support: tuple[float, ...]
    outline_support: Literal["raw_bed_edges", "replayed_white_edges"]


@dataclass(frozen=True)
class LayoutPreview:
    status: Literal["needs_confirmation", "rejected"]
    reason: str | None = None
    guides: tuple[KeyGuide, ...] = ()
    evidence: LayoutEvidence | None = None


@dataclass(frozen=True)
class _Anchor:
    index: int
    x: float
    score: float


def _valid_frame(frame):
    dimensions = (frame.height, frame.width, frame.left, frame.top, frame.content_width,
                  frame.content_height, frame.source_width, frame.source_height)
    return (all(_integer(value) for value in dimensions)
            and min(frame.source_width, frame.source_height) > 0
            and min(frame.content_width, frame.content_height) >= 3
            and 0 <= frame.left <= frame.width - frame.content_width
            and 0 <= frame.top <= frame.height - frame.content_height)


def _component(prediction, frame):
    mask = prediction.mask
    values = np.asarray(mask.values)
    x, y = mask.x - frame.left, mask.y - frame.top
    if (values.ndim != 2 or values.dtype.kind not in "buif" or not np.isfinite(values).all()
            or not _integer(x) or not _integer(y) or not mask.area
            or not 0 <= x <= frame.content_width - values.shape[1]
            or not 0 <= y <= frame.content_height - values.shape[0]):
        raise ValueError("detection mask is outside the image or has invalid pixels")
    component = _largest_component(values)
    pixels = np.count_nonzero(component)
    if pixels < 5 or pixels < .85 * np.count_nonzero(values):
        raise ValueError("detection mask lacks a substantial connected component")
    return component, x, y


def _bed_outline(predictions, frame, fit, white_count):
    index = fit.detection_index
    if not _integer(index) or not 0 <= index < len(predictions):
        raise ValueError("keybed fit does not identify a raw detection")
    prediction = predictions[index]
    if prediction.class_id != 0 or not _confidence(prediction.score) or prediction.score <= 0:
        raise ValueError("keybed fit does not identify a playable-keybed detection")
    try:
        quad = np.float32(checked_quad(fit.quad, frame.content_width, frame.content_height))
    except (TypeError, ValueError) as exc:
        raise ValueError("keybed fit has invalid or clipped corners") from exc
    component, x, y = _component(prediction, frame)
    mask = np.zeros((frame.content_height, frame.content_width), dtype=np.uint8)
    height, width = component.shape
    mask[y:y + height, x:x + width] = component
    if _touches_edge(mask):
        raise ValueError("playable-keybed detection touches a clipped image edge")
    if np.count_nonzero(component) < MIN_BED_AREA * mask.size:
        raise ValueError("playable-keybed detection is too small")

    iou, bed_coverage, surface_coverage = _quad_overlap(quad, mask)
    if iou < MIN_BED_IOU or min(bed_coverage, surface_coverage) < .80:
        raise ValueError("the supplied corners do not overlap the playable-keybed detection")
    rectifier = cv2.getPerspectiveTransform(quad, UNIT_QUAD)
    projector = cv2.getPerspectiveTransform(UNIT_QUAD, quad)
    contour = _project(_contour(component) + np.float32([x, y]), rectifier)
    # Validate raw mask support near each supplied side, even for an external fit.
    # Side ends deserve a tighter width tolerance than rear/front depth noise.
    lengths = np.linalg.norm(quad - np.roll(quad, -1, axis=0), axis=1)
    side_tolerance = min(.08, max(.8 / white_count, 2 / min(lengths[0], lengths[2])))
    support = []
    supported = True
    for edge, boundary in enumerate((0, 1, 1, 0)):
        tangent = edge % 2
        tolerance = side_tolerance if tangent else .13
        positions = contour[np.abs(contour[:, 1 - tangent] - boundary) <= tolerance, tangent]
        bins = np.linspace(.04, .96, 13)
        occupied = np.count_nonzero(np.histogram(positions, bins)[0])
        support.append(occupied / (len(bins) - 1))
        supported &= (occupied >= 8 and np.any((.04 <= positions) & (positions <= .20))
                      and np.any((.80 <= positions) & (positions <= .96)))
    if supported and prediction.score >= STRONG_BED_CONFIDENCE:
        return rectifier, projector, float(iou), tuple(support), "raw_bed_edges"

    # The existing selector can recover an end or long edge where the bed mask
    # itself falls short. Reproduce that result with the raw detections before
    # accepting its geometry as preview evidence, regardless of caller metadata.
    if fit.method == "white-key-edges":
        try:
            replay = select_keybed(predictions, frame)
        except ValueError:
            replay = None
        if (replay is not None and replay.method == "white-key-edges" and replay.detection_index == index
                and np.array_equal(quad, np.float32(replay.quad))):
            return rectifier, projector, float(iou), replay.edge_support, "replayed_white_edges"
    if prediction.score < STRONG_BED_CONFIDENCE:
        raise ValueError("keybed fit requires a confident playable-keybed detection or verified white-key edges")
    raise ValueError("playable-keybed detection lacks support along all four supplied sides")


def _find_anchors(predictions, frame, rectifier, white_count, config, indices):
    whites, blacks = [], []
    for index in indices:
        prediction = predictions[index]
        if prediction.class_id not in (1, 2) or not _confidence(prediction.score):
            continue
        white = prediction.class_id == 1
        threshold = config.white_confidence if white else config.black_confidence
        if prediction.score <= threshold:
            continue
        try:
            component, x, y = _component(prediction, frame)
        except ValueError:
            continue
        ys, xs = np.nonzero(component)
        xs, ys = xs + x, ys + y
        if (xs.min() == 0 or ys.min() == 0 or xs.max() == frame.content_width - 1
                or ys.max() == frame.content_height - 1):
            continue
        points = _project(np.column_stack((xs, ys)), rectifier)
        if not np.isfinite(points).all():
            continue
        back, front = np.quantile(points[:, 1], (.02, .98))
        if white:
            # The solid front band has a more stable center than the entire
            # mask of a white key, whose rear notches can be asymmetric.
            band = points[(.73 <= points[:, 1]) & (points[:, 1] <= .95)]
            if len(band) < 5 or np.ptp(band[:, 1]) < .10 or back > .49 or not .85 <= front <= 1.12:
                continue
            x_samples = band[:, 0]
            min_width, max_width = .52, 1.30
        else:
            if front - back < .15 or back > .45 or not .18 <= front <= .80:
                continue
            x_samples = points[:, 0]
            min_width, max_width = .19, .96
        low, high = np.quantile(x_samples, (.06, .94))
        center = (low + high) / 2
        if 0 < center < 1 and min_width <= (high - low) * white_count <= max_width:
            (whites if white else blacks).append(_Anchor(index, float(center), float(prediction.score)))
    return whites, blacks


def _assign_white(anchors, centers, spacing, left, white_count):
    slots = np.rint((centers - left) / spacing - .5).astype(int)
    errors = np.abs(centers - left - (slots + .5) * spacing) / spacing
    matches = {}
    for position in np.flatnonzero((slots >= 0) & (slots < white_count) & (errors <= MAX_WHITE_RESIDUAL)):
        slot, anchor, error = int(slots[position]), anchors[position], errors[position]
        evidence = (error, -anchor.score, anchor.index)
        if slot not in matches or evidence < matches[slot][0]:
            matches[slot] = evidence, anchor
    return matches


def _white_fit_rank(matches, left, end_offset):
    # Near-perfect anchors contribute a full vote. A borderline detection
    # contributes little, so giving it a slot cannot pull a coherent row off-center.
    strength = sum(1 - (rank[0] / MAX_WHITE_RESIDUAL) ** 2 for rank, _ in matches.values())
    return -strength, -len(matches), abs(left) + abs(end_offset)


def _white_fit(anchors, white_count):
    if len(anchors) < MIN_WHITE_ANCHORS:
        raise ValueError("too few reliable white-key anchors")
    centers = np.asarray([anchor.x * white_count for anchor in anchors])
    best = None
    # Search only lattices whose outer boundaries agree with the bed. This
    # accommodates skipped slots without using adjacent detections as neighbors.
    for left in END_OFFSETS:
        for end_offset in END_OFFSETS:
            spacing = (white_count + end_offset - left) / white_count
            matches = _assign_white(anchors, centers, spacing, left, white_count)
            if len(matches) < MIN_WHITE_ANCHORS:
                continue
            rank = _white_fit_rank(matches, left, end_offset)
            if best is None or rank < best[0]:
                best = rank, left, spacing, matches
    if best is None:
        raise ValueError("white-key anchors do not agree with the configured spacing")
    rank, left, spacing, matches = best

    # Refine spacing and phase from the winning distinct anchors. The profile
    # and outline still bound the phase, preventing a one-white-key shift.
    design = np.column_stack((np.ones(len(matches)), np.asarray(list(matches)) + .5))
    measured = [anchor.x * white_count for _, anchor in matches.values()]
    refined_left, refined_spacing = np.linalg.lstsq(design, measured, rcond=None)[0]
    refined_right = refined_left + white_count * refined_spacing
    if abs(refined_left) <= MAX_END_OFFSET and abs(refined_right - white_count) <= MAX_END_OFFSET:
        refined = _assign_white(anchors, centers, refined_spacing, refined_left, white_count)
        if len(refined) >= MIN_WHITE_ANCHORS and _white_fit_rank(
                refined, refined_left, refined_right - white_count) <= rank:
            left, spacing, matches = refined_left, refined_spacing, refined

    distinct_candidates = len(set(np.rint(centers - .5).astype(int)))
    if len(matches) < .65 * distinct_candidates:
        raise ValueError("white-key anchors are inconsistent with the configured spacing")
    positions = np.asarray(sorted(anchor.x for _, anchor in matches.values()))
    span = float(np.ptp(positions))
    if span < .64 or not np.any((.30 < positions) & (positions < .70)) or np.diff(positions).max() > .43:
        raise ValueError("white-key anchors are too localized across the keybed")
    residual = float(np.sqrt(np.mean([error[0] ** 2 for error, _ in matches.values()])))
    if residual > .18:
        raise ValueError("white-key anchors have excessive spacing residuals")
    matches_by_slot = {slot: pair[1] for slot, pair in matches.items()}
    return left / white_count, spacing / white_count, matches_by_slot, span, residual


def _match_black(anchors, boundaries, left, spacing, white_count):
    candidates = {}
    for anchor in anchors:
        coordinate = (anchor.x - left) / spacing
        boundary = round(coordinate)
        error = abs(coordinate - boundary)
        if not 1 <= boundary < white_count or error > .35:
            continue
        rank = (error, -anchor.score, anchor.index)
        if boundary not in candidates or rank < candidates[boundary][0]:
            candidates[boundary] = rank, anchor
    # A black mask projected onto the white plane may shift. Treat only
    # multiple, centered observations at incompatible gaps as contradictory.
    centered = {boundary for boundary, (rank, _) in candidates.items() if rank[0] <= .22}
    conflicts = centered - boundaries
    if len(conflicts) >= 3 and len(conflicts) > .40 * len(centered):
        raise ValueError("black-key anchors contradict the configured keyboard pattern")
    return {boundary: anchor for boundary, (_, anchor) in candidates.items() if boundary in boundaries}


def reconstruct_layout(
    predictions: Sequence["Instance"], frame: "Frame", fit: KeybedFit | None,
    profile: KeyboardProfile | None, config: LayoutConfig = LayoutConfig(),
    *, anchor_indices: Sequence[int] | None = None,
) -> LayoutPreview:
    """Use a provided profile and a proposed content-local quad to preview guides.

    Adjacent guides come from a jointly fitted white-key lattice and the
    profile's chromatic pattern. Lines in content-local pixels illustrate
    position, including approximate black-key positions on the white-key plane.
    Raw detection indices identify supporting detections in the original input.
    Use anchor_indices to limit spacing/position observations while retaining the
    original detection evidence for checking the contour.
    Even a supported preview requires review of the profile and four corners.
    """
    if profile is None or profile.phase is None:
        return LayoutPreview("rejected", "an explicit keyboard count and start pitch class or first MIDI note are required")
    if fit is None:
        return LayoutPreview("rejected", "a supported full keybed outline is required")
    if not _valid_frame(frame):
        return LayoutPreview("rejected", "invalid frame content geometry")
    indices = range(len(predictions)) if anchor_indices is None else tuple(anchor_indices)
    if not all(_integer(index) and 0 <= index < len(predictions) for index in indices):
        raise ValueError("anchor indices must refer to raw detections")

    pitch_classes = [(profile.phase + offset) % 12 for offset in range(profile.key_count)]
    black_keys = [pitch in BLACK_PITCH_CLASSES for pitch in pitch_classes]
    white_count = len(black_keys) - sum(black_keys)
    black_boundaries = set()
    ordinal = 0
    for black in black_keys:
        if black:
            black_boundaries.add(ordinal)
        else:
            ordinal += 1
    try:
        rectifier, projector, iou, support, outline_support = _bed_outline(predictions, frame, fit, white_count)
        whites, blacks = _find_anchors(predictions, frame, rectifier, white_count, config, indices)
        left, spacing, white_matches, span, residual = _white_fit(whites, white_count)
        black_matches = _match_black(blacks, black_boundaries, left, spacing, white_count)
    except (ValueError, cv2.error, np.linalg.LinAlgError) as exc:
        return LayoutPreview("rejected", str(exc))

    guides = []
    ordinal = 0
    for offset, (pitch, black) in enumerate(zip(pitch_classes, black_keys, strict=True)):
        detection = (black_matches if black else white_matches).get(ordinal)
        x = left + (ordinal + (0 if black else .5)) * spacing
        if black and detection is not None:
            x = detection.x
        points = _project([(x, 0), (x, .60 if black else 1)], projector)
        if not 0 < x < 1 or not np.isfinite(points).all():
            return LayoutPreview("rejected", "a guide falls outside usable keybed geometry")
        guides.append(KeyGuide(
            pitch, None if profile.first_midi is None else profile.first_midi + offset,
            None if black else ordinal, black, float(x), tuple(map(tuple, points.tolist())),
            None if detection is None else detection.index, None if detection is None else detection.score,
        ))
        ordinal += not black

    evidence = LayoutEvidence(
        float(spacing), float(left), float(left + white_count * spacing),
        tuple(white_matches[slot].index for slot in sorted(white_matches)),
        tuple(black_matches[slot].index for slot in sorted(black_matches)), span, residual,
        int(fit.detection_index), iou, support, outline_support,
    )
    return LayoutPreview("needs_confirmation", guides=tuple(guides), evidence=evidence)
