"""Pixel-space geometry shared by proposals, review, and evaluation."""

from math import dist, isfinite

CORNER_NAMES = ("back-left", "back-right", "front-right", "front-left")
CLASSES = ("playable_keybed", "white_key", "black_key")


def cross(a, b, c):
    return (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])


def edges_intersect(a, b, c, d):
    if any(max(min(a[axis], b[axis]), min(c[axis], d[axis])) >
           min(max(a[axis], b[axis]), max(c[axis], d[axis])) for axis in (0, 1)):
        return False
    return cross(a, b, c) * cross(a, b, d) <= 0 and cross(c, d, a) * cross(c, d, b) <= 0


def checked_polygon(points, width, height):
    if not isinstance(points, (list, tuple)) or len(points) < 3:
        raise ValueError("expected at least three polygon vertices")
    polygon = []
    for point in points:
        if not isinstance(point, (list, tuple)) or len(point) != 2:
            raise ValueError("each vertex must have x and y")
        x, y = map(float, point)
        if not (isfinite(x) and isfinite(y) and 0 < x < width - 1 and 0 < y < height - 1):
            raise ValueError("vertex is outside the image or on a clipped edge")
        polygon.append((x, y))
    edges = list(zip(polygon, polygon[1:] + polygon[:1]))
    area = sum(ax * by - ay * bx for (ax, ay), (bx, by) in edges)
    if abs(area) <= 2 or len(set(polygon)) != len(polygon):
        raise ValueError("polygon has no usable area or has repeated vertices")
    for first, (a, b) in enumerate(edges):
        for second in range(first + 2, len(edges)):
            if first == 0 and second == len(edges) - 1:
                continue
            if edges_intersect(a, b, *edges[second]):
                raise ValueError("polygon edges cross")
    return polygon


def checked_quad(points, width, height):
    """Validate a complete, clockwise quadrilateral in top-left image coordinates."""
    if not isinstance(points, (list, tuple)) or len(points) != 4:
        raise ValueError("expected four ordered corners")
    quad = checked_polygon(points, width, height)
    for index in range(4):
        if cross(quad[index - 2], quad[index - 1], quad[index]) <= 0:
            raise ValueError("corners must form a convex clockwise quadrilateral")
    if quad[0][0] >= quad[1][0] or quad[3][0] >= quad[2][0]:
        raise ValueError("left and right corners appear reversed")
    if quad[0][1] + quad[1][1] >= quad[2][1] + quad[3][1]:
        raise ValueError("front edge must appear below the back edge")
    if dist(quad[0], quad[1]) + dist(quad[2], quad[3]) <= dist(quad[1], quad[2]) + dist(quad[3], quad[0]):
        raise ValueError("front and back must be the long keybed edges")
    return quad


def segment_rows(instances, width, height):
    """Encode one normalized polygon per YOLO segmentation instance."""
    lines = []
    for instance in instances:
        values = [str(CLASSES.index(instance["class"]))]
        for x, y in instance["polygon"]:
            values.extend((f"{x / width:.8f}", f"{y / height:.8f}"))
        lines.append(" ".join(values) + "\n")
    return "".join(lines)
