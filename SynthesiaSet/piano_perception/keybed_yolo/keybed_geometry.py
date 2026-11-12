"""Deterministic four-corner geometry for the calibration evaluator."""

from dataclasses import dataclass
from math import dist

import cv2
import numpy as np

from geometry import checked_quad


UNIT_QUAD = np.float32([[0, 0], [1, 0], [1, 1], [0, 1]])
HULL_TOLERANCES = (.002, .0035, .005, .0075, .01, .015, .02, .03, .04, .05)
MIN_BED_IOU = .72
MIN_BED_AREA = .003
STRONG_BED_CONFIDENCE = .30


@dataclass(frozen=True)
class KeybedFit:
    quad: tuple
    detection_index: int
    confidence: float
    iou: float
    method: str
    bed_coverage: float
    surface_coverage: float
    white_support: int
    edge_support: tuple


@dataclass(frozen=True)
class _WhiteKey:
    points: np.ndarray
    x: float
    y: float
    width: float
    depth: float
    clipped: bool


class _ClippedSurface(ValueError):
    pass


def _touches_edge(mask):
    return bool(mask[0].any() or mask[-1].any() or mask[:, 0].any() or mask[:, -1].any())


def _largest_component(mask):
    image = np.ascontiguousarray(mask != 0, dtype=np.uint8)
    count, labels, stats, _ = cv2.connectedComponentsWithStats(image)
    if count <= 2:
        return image
    largest = 1 + np.argmax(stats[1:, cv2.CC_STAT_AREA])
    return (labels == largest).astype(np.uint8)


def _contour(mask):
    contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
    return max(contours, key=cv2.contourArea)[:, 0].astype(np.float32)


def _project(points, transform):
    points = np.asarray(points, dtype=np.float32).reshape(-1, 1, 2)
    return cv2.perspectiveTransform(points, transform).reshape(-1, 2)


def _order_corners(points):
    vertices = points.tolist()
    edges = list(zip(vertices, vertices[1:] + vertices[:1]))
    long_edges = max((edges[0::2], edges[1::2]), key=lambda pair: sum(dist(*edge) for edge in pair))
    back, front = sorted(long_edges, key=lambda edge: edge[0][1] + edge[1][1])
    return np.float32([*sorted(back), *sorted(front, reverse=True)])


def _validated(quad, shape):
    return np.float32(checked_quad(quad.tolist(), shape[1], shape[0]))


def _quad_overlap(quad, mask):
    surface = np.zeros_like(mask)
    cv2.fillConvexPoly(surface, np.rint(quad * 256).astype(np.int32), 1, shift=8)
    overlap = np.count_nonzero(surface & mask)
    bed_area = np.count_nonzero(mask)
    surface_area = np.count_nonzero(surface)
    return overlap / (bed_area + surface_area - overlap), overlap / bed_area, overlap / surface_area


def _seed_quad(mask, points):
    hull = cv2.convexHull(points)
    perimeter = cv2.arcLength(hull, True)
    # A rectangle is only an initialization. Fitting and checking four supported
    # lines can recover skewed ends even when simplification merged two corners.
    seeds = [cv2.approxPolyDP(hull, e * perimeter, True)[:, 0] for e in HULL_TOLERANCES]
    seeds.append(cv2.boxPoints(cv2.minAreaRect(points)))
    best, best_iou = None, 0
    for seed in seeds:
        if len(seed) != 4:
            continue
        try:
            quad = _validated(_order_corners(seed), mask.shape)
        except ValueError:
            continue
        iou, _, _ = _quad_overlap(quad, mask)
        if iou > best_iou:
            best, best_iou = quad, iou
    if best is None or best_iou < MIN_BED_IOU:
        raise ValueError("keybed component has no supported four-corner seed")
    return best


def _edge_samples(points, edge, bins, radius, trim):
    # Working in a rectified frame gives each interval of a side comparable
    # weight even when one endpoint is much closer to the camera.
    tangent = edge % 2
    outside = -1 if edge in (1, 2) else 1
    boundary = int(outside == -1)
    u, v = points[:, tangent], points[:, 1 - tangent]
    nearby = np.abs(v - boundary) <= radius
    intervals = np.linspace(trim, 1 - trim, bins + 1)
    samples = []
    for start, end in zip(intervals[:-1], intervals[1:], strict=True):
        subset = points[nearby & (u >= start) & (u < end)]
        if len(subset) >= 2:
            samples.append(subset[np.argmin(outside * subset[:, 1 - tangent])])
    return np.asarray(samples, dtype=np.float32)


def _fit_line(samples, edge, bins):
    if len(samples) < max(4, bins // 2):
        raise ValueError("insufficient observed edge support")
    direction_x, direction_y, x, y = cv2.fitLine(samples, cv2.DIST_WELSCH, 0, .001, .001).ravel()
    line = np.float64([-direction_y, direction_x, direction_y * x - direction_x * y])
    errors = np.abs(samples @ line[:2] + line[2])
    # The normalized width is much larger than normalized depth in pixels.
    tolerance = .025 if edge % 2 else .085
    inliers = errors <= tolerance
    tangent = edge % 2
    if np.count_nonzero(inliers) < bins // 2 or np.ptp(samples[inliers, tangent]) < .45:
        raise ValueError("edge support is inconsistent or too localized")
    return line, float(np.count_nonzero(inliers) / bins)


def _intersections(lines):
    corners = []
    for index, second in enumerate(lines):
        first = lines[index - 1]
        try:
            corners.append(np.linalg.solve([first[:2], second[:2]], [-first[2], -second[2]]))
        except np.linalg.LinAlgError as exc:
            raise ValueError("adjacent edges cannot define a stable corner") from exc
    return np.asarray(corners, dtype=np.float32)


def _mask_edges(seed, points, shape):
    rectified = _project(points, cv2.getPerspectiveTransform(seed, UNIT_QUAD))
    lines, support = [], []
    for edge in range(4):
        bins = 14 if edge % 2 else 32
        samples = _edge_samples(rectified, edge, bins, .3, .06)
        line, fraction = _fit_line(samples, edge, bins)
        lines.append(line)
        support.append(fraction)
    corners = _intersections(lines)
    # A mask-only result must have real contour evidence near all its corners.
    if any(np.min(np.linalg.norm(rectified - corner, axis=1)) > .12 for corner in corners):
        raise ValueError("mask lacks contour support at a corner")
    return _validated(_project(corners, cv2.getPerspectiveTransform(UNIT_QUAD, seed)), shape), tuple(support)


def _bed_geometry(mask):
    if mask.ndim != 2 or min(mask.shape) < 3:
        raise ValueError("expected a two-dimensional keybed mask")
    component = _largest_component(mask)
    if _touches_edge(component):
        raise _ClippedSurface("keybed component touches an image edge")
    pixels = np.count_nonzero(component)
    if pixels < MIN_BED_AREA * component.size:
        raise ValueError("keybed component is too small")
    if pixels < .85 * np.count_nonzero(mask):
        raise ValueError("keybed mask has substantial disconnected foreground")
    points = _contour(component)
    seed = _seed_quad(component, points)
    try:
        fitted, support = _mask_edges(seed, points, component.shape)
    except ValueError:
        return component, seed, None, ()
    if _quad_overlap(fitted, component)[0] < MIN_BED_IOU:
        return component, seed, None, ()
    return component, seed, fitted, support


def fit_keybed(mask):
    """Fit a complete mask with supported sides in back-left clockwise order."""
    component, _, fitted, _ = _bed_geometry(mask)
    if fitted is None:
        raise ValueError("keybed mask lacks four supported boundaries")
    return tuple(map(tuple, fitted.tolist())), _quad_overlap(fitted, component)[0]


def _content_mask(mask, frame):
    image = np.zeros((frame.content_height, frame.content_width), dtype=np.uint8)
    x, y = mask.x - frame.left, mask.y - frame.top
    height, width = mask.values.shape
    image[y:y + height, x:x + width] = mask.values
    return image


def _white_contours(predictions, frame, threshold):
    for prediction in predictions:
        if prediction.class_id != 1 or prediction.score <= threshold or prediction.mask.area < 5:
            continue
        mask = prediction.mask
        component = _largest_component(mask.values)
        x, y = mask.x - frame.left, mask.y - frame.top
        points = _contour(component) + np.float32([x, y])
        clipped = (points[:, 0].min() <= 0 or points[:, 1].min() <= 0
                   or points[:, 0].max() >= frame.content_width - 1
                   or points[:, 1].max() >= frame.content_height - 1)
        yield points, clipped


def _white_row(contours, transform):
    keys = []
    for points, clipped in contours:
        rectified = _project(points, transform)
        low, middle, high = np.quantile(rectified, (.05, .5, .95), axis=0)
        x, y = middle
        if -.20 <= x <= 1.20 and -.15 < y < 1.15 and low[1] < .6 and high[1] > .4:
            width, depth = high - low
            keys.append(_WhiteKey(rectified, float(x), float(y), float(width), float(depth), clipped))
    if len(keys) < 6:
        raise ValueError("too few white-key contours support the bed")
    width, depth, center_y = np.median([(key.width, key.depth, key.y) for key in keys], axis=0)
    row = sorted((key for key in keys if
                  .5 * width <= key.width <= 1.8 * width
                  and .5 * depth <= key.depth <= 1.6 * depth
                  and abs(key.y - center_y) < .35 * depth), key=lambda key: key.x)
    if len(row) < 6:
        raise ValueError("white-key contours do not form a coherent row")
    if any(key.clipped for key in row):
        raise _ClippedSurface("white-key support touches an image edge")
    return row, width


def _white_edges(contours, seed, shape):
    rectifier = cv2.getPerspectiveTransform(seed, UNIT_QUAD)
    row, width = _white_row(contours, rectifier)
    left, right = row[0], row[-1]
    # Each endpoint must be part of the same observed row. A detached outlier
    # must not move a side of the quadrilateral to a different instrument.
    if row[2].x - left.x > 4 * width or right.x - row[-3].x > 4 * width:
        raise ValueError("outer white key is separated from the row")
    combined = np.concatenate([key.points for key in row])
    lines, support = [], []
    for edge, points in enumerate((combined, right.points, combined, left.points)):
        bins, radius = (15, .25) if edge % 2 else (30, .35)
        line, fraction = _fit_line(_edge_samples(points, edge, bins, radius, .04), edge, bins)
        lines.append(line)
        support.append(fraction)
    corners = _intersections(lines)
    for corner, key in zip(corners, (left, right, right, left), strict=True):
        delta = np.abs(key.points - corner)
        if not np.any((delta[:, 0] <= width) & (delta[:, 1] <= .20)):
            raise ValueError("an outer corner lacks nearby white-key support")
    quad = _validated(_project(corners, cv2.getPerspectiveTransform(UNIT_QUAD, seed)), shape)
    return quad, len(row), tuple(support)


def _result(quad, index, confidence, overlap, method, whites, edge_support):
    iou, bed_coverage, surface_coverage = overlap
    return KeybedFit(tuple(map(tuple, quad.tolist())), index, confidence, iou, method, bed_coverage,
                     surface_coverage, whites, edge_support)


def select_keybed(predictions, frame, *, bed_confidence=.025, white_confidence=.30):
    """Select the first supported bed by confidence and return content-local corners.

    Lower-confidence bed masks must be corroborated by a row of white keys.
    White contours may move a boundary only where those contours support a
    connected surface substantially overlapping the bed. No key count or
    expected endpoints are assumed.
    """
    candidates = sorted((i for i, p in enumerate(predictions)
                         if p.class_id == 0 and p.score > bed_confidence and p.mask.area),
                        key=lambda index: -predictions[index].score)
    if not candidates:
        raise ValueError("no keybed candidate above the geometry confidence floor")
    contours = tuple(_white_contours(predictions, frame, white_confidence))
    reason = "no keybed candidate has supported boundaries"
    for index in candidates:
        prediction = predictions[index]
        mask = _content_mask(prediction.mask, frame)
        try:
            component, seed, mask_quad, mask_support = _bed_geometry(mask)
        except _ClippedSurface:
            # Do not replace a clipped detection with an interior fragment.
            raise
        except ValueError as exc:
            reason = str(exc)
            continue
        guide = mask_quad if mask_quad is not None else seed
        try:
            quad, white_support, edge_support = _white_edges(contours, guide, mask.shape)
        except _ClippedSurface:
            raise
        except ValueError as exc:
            reason = str(exc)
        else:
            overlap = _quad_overlap(quad, component)
            if overlap[1] >= .85 and overlap[2] >= .80:
                return _result(quad, index, prediction.score, overlap, "white-key-edges",
                               white_support, edge_support)
            reason = "white-key and bed support cover different surfaces"
        if mask_quad is not None and prediction.score > STRONG_BED_CONFIDENCE:
            return _result(mask_quad, index, prediction.score, _quad_overlap(mask_quad, component),
                           "bed-mask-edges", 0, mask_support)
    raise ValueError(reason)

