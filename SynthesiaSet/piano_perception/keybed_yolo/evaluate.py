"""Select confidence on validation photos and evaluate a frozen configuration on test."""

import argparse
import ast
from collections import Counter
from dataclasses import dataclass
import json
from math import dist
from pathlib import Path
import tempfile

import cv2
import numpy as np
from PIL import Image, ImageDraw

from dataset import read_json, sha256, sha256_file, write_json
from fit import PINNED_ULTRALYTICS, describe_output, letterbox, verify_dataset, yolo
from geometry import CLASSES, CORNER_NAMES
from propose import quad_from_mask
from resource_budget import ResourceBudget

CONFIDENCES = (0.001, 0.005, 0.01, 0.02, 0.03, 0.05, 0.075, 0.1, 0.15,
               0.2, 0.25, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 0.95)
IOU_THRESHOLDS = np.linspace(0.50, 0.95, 10)
END_TO_END = "end_to_end_rows_xyxy_score_class_mask_coefficients"
COLORS = ("#26e1b2", "#ffd449", "#ef83e4")


@dataclass(frozen=True)
class Frame:
    height: int
    width: int
    left: int
    top: int
    content_width: int
    content_height: int
    source_width: int
    source_height: int

    def clip_box(self, box):
        limits = (self.left, self.top, self.left, self.top)
        return np.clip(box, limits, np.array(limits) + (
            self.content_width, self.content_height, self.content_width, self.content_height,
        ))

    def to_input(self, points):
        return np.asarray(points) * (self.content_width / self.source_width,
                                     self.content_height / self.source_height) + (self.left, self.top)

    def to_source(self, points):
        return (np.asarray(points) - (self.left, self.top)) * (
            self.source_width / self.content_width, self.source_height / self.content_height,
        )


@dataclass(frozen=True)
class Mask:
    x: int
    y: int
    values: np.ndarray
    area: int

    @classmethod
    def create(cls, x, y, values):
        return cls(x, y, values, int(np.count_nonzero(values)))

    def iou(self, other):
        x1, y1 = max(self.x, other.x), max(self.y, other.y)
        x2 = min(self.x + self.values.shape[1], other.x + other.values.shape[1])
        y2 = min(self.y + self.values.shape[0], other.y + other.values.shape[0])
        if x1 >= x2 or y1 >= y2 or not self.area or not other.area:
            return 0.0
        a = self.values[y1 - self.y:y2 - self.y, x1 - self.x:x2 - self.x]
        b = other.values[y1 - other.y:y2 - other.y, x1 - other.x:x2 - other.x]
        overlap = int(np.count_nonzero(a & b))
        return overlap / (self.area + other.area - overlap)


@dataclass(frozen=True)
class Instance:
    class_id: int
    box: np.ndarray
    mask: Mask
    score: float = 1.0
    slot: int | None = None


def reference_instances(row, frame):
    references = []
    for item in row["instances"]:
        points = frame.to_input(item["polygon"])
        box = np.concatenate([points.min(axis=0), points.max(axis=0)])
        x1, y1 = np.floor(box[:2]).astype(int)
        x2, y2 = np.ceil(box[2:]).astype(int) + 1
        x2 = min(x2, frame.left + frame.content_width)
        y2 = min(y2, frame.top + frame.content_height)
        raster = np.zeros((y2 - y1, x2 - x1), dtype=np.uint8)
        vertices = np.rint((points - (x1, y1)) * 256).astype(np.int32)
        cv2.fillPoly(raster, [vertices], 1, shift=8)
        references.append(Instance(CLASSES.index(item["class"]), box, Mask.create(x1, y1, raster),
                                   slot=item.get("slot")))
    return references


def unpack_outputs(outputs):
    if describe_output(outputs) != END_TO_END:
        raise ValueError("evaluation expects the Quest-compatible NMS-free segmentation head")
    if not all(np.isfinite(output).all() for output in outputs):
        raise ValueError("nonfinite model output")
    rows = next(item[0] for item in outputs if item.ndim == 3)
    protos = next(item[0] for item in outputs if item.ndim == 4)
    if np.any((rows[:, 4] < 0) | (rows[:, 4] > 1)):
        raise ValueError("expected confidence scores in [0, 1]")
    if not np.all(np.isin(rows[:, 5], range(len(CLASSES)))):
        raise ValueError("out-of-range detection class")
    return rows, protos


def decode(rows, protos, frame, confidence):
    """Mirror the Quest decoder: bilinear logits, content/box clipping, strict >0."""
    candidates = rows[rows[:, 4] > np.float32(confidence)]
    candidates = candidates[np.argsort(-candidates[:, 4], kind="stable")]
    channels, ph, pw = protos.shape
    flat = protos.reshape(channels, -1)
    predictions = []
    for start in range(0, len(candidates), 16):
        chunk = candidates[start:start + 16]
        logits = cv2.gemm(np.ascontiguousarray(chunk[:, 6:]), flat, 1, None, 0)
        for row, logit in zip(chunk, logits, strict=True):
            box = frame.clip_box(row[:4])
            x1, y1, x2, y2 = np.ceil(box).astype(int)
            resized = cv2.resize(logit.reshape(ph, pw), (frame.width, frame.height),
                                 interpolation=cv2.INTER_LINEAR)
            mask = Mask.create(x1, y1, resized[y1:y2, x1:x2] > 0)
            predictions.append(Instance(int(row[5]), box, mask, float(row[4])))
    return predictions


def pairwise(references, predictions, overlap):
    scores = np.zeros((len(references), len(predictions)), dtype=np.float32)
    for i, reference in enumerate(references):
        for j, prediction in enumerate(predictions):
            if reference.class_id == prediction.class_id:
                scores[i, j] = overlap(reference, prediction)
    return scores


def box_overlaps(references, predictions, *, compare_degenerate=False):
    if not references or not predictions:
        return np.zeros((len(references), len(predictions)), dtype=np.float32)
    truth = np.stack([item.box for item in references])[:, None, :]
    found = np.stack([item.box for item in predictions])[None, :, :]
    overlap = np.maximum(np.minimum(truth[..., 2:], found[..., 2:]) -
                         np.maximum(truth[..., :2], found[..., :2]), 0).prod(axis=2)
    area_truth = np.maximum(truth[..., 2:] - truth[..., :2], 0).prod(axis=2)
    area_found = np.maximum(found[..., 2:] - found[..., :2], 0).prod(axis=2)
    union = area_truth + area_found - overlap
    same_class = np.array([item.class_id for item in references])[:, None] == [
        item.class_id for item in predictions
    ]
    scores = np.divide(overlap, union, out=np.zeros_like(overlap, dtype=float), where=(union > 0) & same_class)
    if compare_degenerate:
        # Both engines can return the same zero-area row after clipping to image content.
        scores[(union == 0) & same_class & (np.abs(truth - found).max(axis=2) <= 0.001)] = 1.0
    return scores


def confidence_matches(overlaps, minimum):
    """Match in descending confidence order to the best unclaimed same-class target."""
    if not overlaps.size:
        return {}
    matches, claimed = {}, np.zeros(overlaps.shape[0], dtype=bool)
    for index in np.flatnonzero((overlaps >= minimum).any(axis=0)):
        available = np.where(claimed, -1, overlaps[:, index])
        target = int(available.argmax())
        if available[target] >= minimum:
            matches[int(index)] = (target, float(available[target]))
            claimed[target] = True
    return matches


def ap_stats(references, predictions, overlaps):
    matched = np.zeros((len(predictions), len(IOU_THRESHOLDS)), dtype=bool)
    for index, threshold in enumerate(IOU_THRESHOLDS):
        for prediction in confidence_matches(overlaps, threshold):
            matched[prediction, index] = True
    return (matched, np.array([item.score for item in predictions]),
            np.array([item.class_id for item in predictions]), np.array([item.class_id for item in references]))


def average_precision(records):
    from ultralytics.utils.metrics import ap_per_class

    tp, confidence, pred_cls, true_cls = (np.concatenate(part) for part in zip(*records, strict=True))
    ap, class_ids = ap_per_class(tp, confidence, pred_cls, true_cls)[5:7]
    classes = {CLASSES[int(i)]: {"mAP50": float(row[0]), "mAP50-95": float(row.mean())}
               for i, row in zip(class_ids, ap, strict=True)}
    return {"mAP50": float(ap[:, 0].mean()) if ap.size else None,
            "mAP50-95": float(ap.mean()) if ap.size else None, "by_class": classes}


def counts_at(references, predictions, matches, confidence):
    expected = Counter(item.class_id for item in references)
    emitted = Counter(item.class_id for item in predictions if item.score > np.float32(confidence))
    correct = Counter(predictions[i].class_id for i in matches if predictions[i].score > np.float32(confidence))
    return {name: {"expected": expected[i], "predicted": emitted[i], "matched": correct[i],
                   "missing": expected[i] - correct[i], "extra": emitted[i] - correct[i],
                   "count_error": emitted[i] - expected[i]}
            for i, name in enumerate(CLASSES)}


def summarize_counts(records):
    totals = {}
    for name in CLASSES:
        rows = [counts[name] for counts in records]
        sums = {key: sum(row[key] for row in rows) for key in ("expected", "predicted", "matched", "missing", "extra")}
        tp, pred, true = sums["matched"], sums["predicted"], sums["expected"]
        totals[name] = {**sums, "precision": tp / pred if pred else None,
                        "recall": tp / true if true else None,
                        "f1": 2 * tp / (pred + true) if pred + true else None,
                        "count_mae": float(np.mean([abs(row["count_error"]) for row in rows])),
                        "count_bias": float(np.mean([row["count_error"] for row in rows])),
                        "exact_count_rate": float(np.mean([row["count_error"] == 0 for row in rows]))}
    return totals


def pick_confidence(sweep):
    def score(item):
        totals = item["classes"]
        macro_f1 = np.mean([row["f1"] or 0 for row in totals.values()])
        key_mae = sum(totals[name]["count_mae"] for name in CLASSES[1:])
        return macro_f1, -key_mae, item["confidence"]

    return max(sweep, key=score)


def order_in_quad(quad, centroids):
    if quad is None or len(centroids) < 2:
        return {"matched_slot_order": None, "order_inversions": None}
    transform = cv2.getPerspectiveTransform(np.float32(quad), np.float32([[0, 0], [1, 0], [1, 1], [0, 1]]))
    homogeneous = np.array([[x, y, 1] for x, y, _ in centroids]) @ transform.T
    if not np.isfinite(homogeneous).all() or np.any(np.abs(homogeneous[:, 2]) < 1e-9):
        return {"matched_slot_order": None, "order_inversions": None}
    x = homogeneous[:, 0] / homogeneous[:, 2]
    slots = [centroids[i][2] for i in np.argsort(x, kind="stable")]
    inversions = sum(left > right for i, left in enumerate(slots) for right in slots[i + 1:])
    return {"matched_slot_order": slots, "order_inversions": inversions}


def corners_and_order(row, frame, references, predictions, matches):
    bed = next((item for item in predictions if item.class_id == 0), None)
    truth = next((item["polygon"] for item in row["instances"] if item["class"] == CLASSES[0]), None)
    found, error, reason = None, None, None
    if bed is None:
        reason = "no keybed prediction"
    else:
        cropped = np.zeros((frame.content_height, frame.content_width), dtype=bool)
        mask = bed.mask
        x, y = mask.x - frame.left, mask.y - frame.top
        cropped[y:y + mask.values.shape[0], x:x + mask.values.shape[1]] = mask.values
        try:
            fitted, _ = quad_from_mask(cropped)
        except ValueError as exc:
            reason = str(exc)
        else:
            found = frame.to_source(np.array(fitted) + (frame.left, frame.top)).tolist()
    width_at_input = None
    if truth:
        white_count = sum(item.class_id == 1 for item in references)
        unit = dist(truth[2], truth[3]) / white_count
        width_at_input = dist(*frame.to_input(truth[2:4])) / white_count
        if found:
            error = {name: dist(p, q) / unit for name, p, q in zip(CORNER_NAMES, truth, found, strict=True)}
    centroids = []
    for prediction, (target, _) in matches.items():
        slot = references[target].slot
        if slot is not None:
            mask = predictions[prediction].mask
            ys, xs = np.nonzero(mask.values)
            centroids.append((xs.mean() + mask.x, ys.mean() + mask.y, slot))
    fitted_order = order_in_quad(frame.to_input(found) if found else None, centroids)
    diagnostic_order = order_in_quad(frame.to_input(truth) if truth else None, centroids)
    return {"ground_truth": truth, "fitted": found, "failure": reason, "white_width_error": error,
            "front_white_width_at_input_px": width_at_input,
            **fitted_order, "ground_truth_quad_order": diagnostic_order}


def distribution(values):
    if not values:
        return None
    return {"n": len(values), "mean": float(np.mean(values)), "median": float(np.median(values)),
            "p90": float(np.percentile(values, 90)), "max": float(max(values))}


def make_overlay(path, pixels, references, predictions, matches, case, frame):
    picture = np.moveaxis(pixels[0], 0, -1)
    picture = Image.fromarray(np.rint(picture * 255).astype(np.uint8))
    panels = []
    matched_refs = {target for target, _ in matches.values()}
    for heading, items, matched in (("Reviewed labels", references, matched_refs),
                                    ("Model output", predictions, matches)):
        panel = picture.copy()
        draw = ImageDraw.Draw(panel)
        for index, instance in enumerate(items):
            mask = instance.mask
            color = COLORS[instance.class_id] if index in matched else "#ff5a53"
            if not mask.area:
                x1, y1, x2, y2 = instance.box
                if x2 >= x1 and y2 >= y1:
                    draw.rectangle((x1, y1, x2, y2), outline=color, width=1)
                continue
            outlines, _ = cv2.findContours(mask.values.astype(np.uint8), cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
            for outline in outlines:
                points = [tuple(point) for point in outline[:, 0] + (mask.x, mask.y)]
                draw.line([*points, points[0]], fill=color, width=2 if instance.class_id == 0 else 1)
        if heading == "Model output" and case["corners"]["fitted"]:
            corners = [tuple(point) for point in frame.to_input(case["corners"]["fitted"])]
            draw.line([*corners, corners[0]], fill="white", width=1)
        panel.thumbnail((560, 420), Image.Resampling.LANCZOS)
        panels.append(panel)
    w, h = panels[0].size
    sheet = Image.new("RGB", (2 * w, h + 38), "#171c23")
    sheet.paste(panels[0], (0, 18))
    sheet.paste(panels[1], (w, 18))
    draw = ImageDraw.Draw(sheet)
    draw.text((4, 3), "Reviewed labels", fill="white")
    draw.text((w + 4, 3), "Model output", fill="white")
    draw.text((4, h + 22), f"{case['id']}   Red outlines = unmatched   |   " + "   ".join(
        f"{name}: {item['matched']}/{item['expected']} matched (+{item['extra']})"
        for name, item in case["classes"].items()), fill="white")
    sheet.save(path)


def parity(reference, actual, ref_proto, actual_proto):
    from scipy.optimize import linear_sum_assignment

    overlaps = box_overlaps(reference, actual, compare_degenerate=True)
    valid = np.zeros(overlaps.shape, dtype=bool)
    candidates = {}
    for i, j in np.argwhere(overlaps >= 0.5):
        expected, observed = reference[i], actual[j]
        box_delta = float(np.abs(expected.box - observed.box).max())
        confidence_delta = abs(expected.score - observed.score)
        if box_delta > 1 or confidence_delta > 0.01:
            continue
        mask_iou = expected.mask.iou(observed.mask) if expected.mask.area or observed.mask.area else None
        if mask_iou is None or mask_iou >= 0.97:
            valid[i, j] = True
            candidates[i, j] = (box_delta, confidence_delta, mask_iou)
    # The engine can reorder equal-scoring rows, including rows with identical boxes.
    # Find a maximum-cardinality assignment among pairs that meet every tolerance.
    ref_indices, actual_indices = linear_sum_assignment(valid, maximize=True)
    checks = [candidates[i, j] for i, j in zip(ref_indices, actual_indices, strict=True) if valid[i, j]]
    mask_scores = [check[2] for check in checks if check[2] is not None]
    same_proto_shape = ref_proto.shape == actual_proto.shape
    prototype_pass = same_proto_shape and bool(np.allclose(ref_proto, actual_proto, rtol=0.001, atol=0.001))
    return {"matched": len(checks), "unmatched_torch": len(reference) - len(checks),
            "unmatched_onnx": len(actual) - len(checks), "prototype_close": prototype_pass,
            "prototype_max_abs_diff": float(np.abs(ref_proto - actual_proto).max()) if same_proto_shape else None,
            "nonempty_mask_pairs": len(mask_scores), "lowest_mask_iou": min(mask_scores, default=None),
            "largest_box_pixel_delta": max((item[0] for item in checks), default=None),
            "largest_confidence_delta": max((item[1] for item in checks), default=None),
            "pass": prototype_pass and len(checks) == len(reference) == len(actual)}


def open_onnx(path, binding):
    import onnxruntime as ort

    report = read_json(path.with_name("export-report.json"))
    if report["weights_sha256"] != binding["weights_sha256"] or report["onnx_sha256"] != binding["onnx_sha256"]:
        raise ValueError("ONNX file and local export report must match the evaluated checkpoint")
    options = ort.SessionOptions()
    options.intra_op_num_threads, options.inter_op_num_threads = 4, 1
    session = ort.InferenceSession(str(path), sess_options=options, providers=["CPUExecutionProvider"])
    (spec,) = session.get_inputs()
    shape = [1, 3, *binding["shape"]]
    if spec.type != "tensor(float)" or spec.shape != shape:
        raise ValueError(f"expected float32 shape {shape}, received {spec.shape}")
    names = ast.literal_eval(session.get_modelmeta().custom_metadata_map["names"])
    if tuple(names[i] for i in range(len(names))) != CLASSES:
        raise ValueError("ONNX class order differs from the reviewed data")
    return session, spec.name, ort.__version__


def binding_for(args):
    return {"shape": [args.height, args.width], "dataset_provenance_sha256": sha256_file(args.dataset / "provenance.json"),
            "weights_sha256": sha256_file(args.weights), "onnx_sha256": sha256_file(args.onnx) if args.onnx else None,
            "classes": list(CLASSES), "ultralytics": PINNED_ULTRALYTICS}


def load_selection(path, binding):
    contents = path.read_bytes()
    selection = json.loads(contents)
    if selection.get("split") != "val" or selection.get("binding") != binding:
        raise ValueError("test requires validation selection with the same data, checkpoint, export and shape")
    cutoff = selection["confidence"]
    if cutoff not in CONFIDENCES:
        raise ValueError("invalid frozen confidence")
    return cutoff, sha256(contents)


def evaluate(args):
    split = "val" if args.command == "select" else "test"
    selecting = split == "val"
    binding = binding_for(args)
    if args.output.exists():
        raise ValueError(f"evaluation output already exists: {args.output}")
    cutoff, selection_hash = None, None
    if not selecting:
        cutoff, selection_hash = load_selection(args.selection, binding)
    inputs = [(path, row) for path, _, row in verify_dataset(args.dataset) if row["split"] == split]
    if not inputs:
        raise ValueError(f"no reviewed {split} photos")
    budget = ResourceBudget(args.output.parent / f"{args.output.name}-resources.jsonl")
    budget.configure_torch("cpu")
    torch = budget.torch
    cv2.setNumThreads(min(4, len(budget.cpu["affinity"])))
    model = yolo(args.output.parent)(str(args.weights), task="segment")
    model.model.end2end = True
    if tuple(model.names[i] for i in range(len(model.names))) != CLASSES or not model.model.end2end:
        raise ValueError("expected a three-class YOLO26 end-to-end segmentation checkpoint")
    net = model.model.float().eval().fuse(verbose=False)
    if args.onnx:
        session, input_name, ort_version = open_onnx(args.onnx, binding)
    sweep, box_ap, mask_ap, records = {value: [] for value in CONFIDENCES}, [], [], []
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=args.output.parent) as temp, torch.inference_mode():
        target = Path(temp) / "evaluation"
        overlay_dir = target / "overlays"
        overlay_dir.mkdir(parents=True)
        for path, row in inputs:
            with Image.open(path) as original:
                image = original.convert("RGB")
            pixels, scale, left, top = letterbox(image, args.height, args.width)
            frame = Frame(args.height, args.width, left, top, round(image.width * scale),
                          round(image.height * scale), image.width, image.height)
            result, _ = net(torch.from_numpy(pixels))
            torch_rows, torch_proto = unpack_outputs([tensor.numpy() for tensor in result])
            rows, protos = torch_rows, torch_proto
            if args.onnx:
                rows, protos = unpack_outputs(session.run(None, {input_name: pixels}))
            references = reference_instances(row, frame)
            predictions = decode(rows, protos, frame, CONFIDENCES[0])
            mask_overlaps = pairwise(references, predictions, lambda a, b: a.mask.iou(b.mask))
            box_pairs = box_overlaps(references, predictions)
            matches = confidence_matches(mask_overlaps, 0.5)
            mask_ap.append(ap_stats(references, predictions, mask_overlaps))
            box_ap.append(ap_stats(references, predictions, box_pairs))
            if selecting:
                for value in CONFIDENCES:
                    sweep[value].append(counts_at(references, predictions, matches, value))
            parity_inputs = (torch_rows, torch_proto, protos) if args.onnx else None
            records.append((path, row, frame, references, predictions, matches, parity_inputs))
        if selecting:
            curve = [{"confidence": value, "classes": summarize_counts(sweep[value])} for value in CONFIDENCES]
            cutoff = pick_confidence(curve)["confidence"]
            write_json(target / "selection.json", {"split": split, "binding": binding, "confidence": cutoff,
                       "objective": "highest macro mask F1 at IoU 0.50, lowest key count MAE, highest confidence",
                       "sweep": curve})
        cases = []
        for path, row, frame, references, predictions, matches, parity_inputs in records:
            counts = counts_at(references, predictions, matches, cutoff)
            kept = [item for item in predictions if item.score > np.float32(cutoff)]
            matched = {i: match for i, match in matches.items() if i < len(kept)}
            case = {"id": row["id"], "group": row["group"], "review_status": row["review"]["status"],
                    "classes": counts, "matched_mask_ious": {name: [iou for i, (_, iou) in matched.items()
                                                                   if kept[i].class_id == c]
                                                             for c, name in enumerate(CLASSES)},
                    "corners": corners_and_order(row, frame, references, kept, matched)}
            if parity_inputs:
                torch_rows, torch_proto, protos = parity_inputs
                case["onnx_parity"] = parity(decode(torch_rows, torch_proto, frame, cutoff), kept, torch_proto, protos)
            with Image.open(path) as original:
                pixels, _, _, _ = letterbox(original.convert("RGB"), frame.height, frame.width)
            make_overlay(overlay_dir / f"{case['id']}.png", pixels, references, kept, matched, case, frame)
            cases.append(case)
        report = summarize(args, binding, split, cutoff, cases, box_ap, mask_ap)
        if args.onnx:
            report["onnxruntime"] = ort_version
        if selection_hash is not None:
            report["selection_sha256"] = selection_hash
        make_contact_sheet(target, cases)
        write_json(target / "evaluation.json", report)
        if binding_for(args) != binding:
            raise ValueError("dataset provenance, checkpoint or export changed during evaluation")
        if selection_hash is not None and (
            not args.selection.is_file() or sha256_file(args.selection) != selection_hash
        ):
            raise ValueError("validation selection changed during evaluation")
        target.rename(args.output)
    print(f"{split} ({len(cases)} images): confidence={cutoff}, mask mAP50-95={report['mask_ap']['mAP50-95']:.4f}")


def summarize(args, binding, split, cutoff, cases, box_ap, mask_ap):
    positives = [case for case in cases if case["review_status"] == "accepted"]
    negatives = [case for case in cases if case["review_status"] == "negative"]
    fitted = [case["corners"]["white_width_error"] for case in positives if case["corners"]["white_width_error"]]
    ordered = [case for case in positives if case["corners"]["order_inversions"] is not None]
    gt_order = [case["corners"]["ground_truth_quad_order"]["order_inversions"] for case in positives]
    parity_report = None
    if args.onnx:
        all_pass = all(case["onnx_parity"]["pass"] for case in cases)
        nonempty = sum(case["onnx_parity"]["nonempty_mask_pairs"] for case in cases)
        parity_report = {"all_cases_pass": all_pass, "nonempty_mask_pairs": nonempty,
                         "mask_parity_observed": nonempty > 0, "pass": all_pass and nonempty > 0}
    return {
        "split": split, "binding": binding, "confidence": cutoff,
        "inference": "ONNX Runtime CPU" if args.onnx else "PyTorch CPU",
        "image_count": len(cases), "group_count": len({case["group"] for case in cases}),
        "positive_images": len(positives), "negative_images": len(negatives),
        "negative_false_keybed_detections": sum(case["classes"][CLASSES[0]]["extra"] > 0 for case in negatives),
        "negative_false_calibrations": sum(case["corners"]["fitted"] is not None for case in negatives),
        "box_ap": average_precision(box_ap), "mask_ap": average_precision(mask_ap),
        "ap_scope": "fixed-shape, confidence >0.001, one-to-one confidence-order matches at IoU 0.50:0.05:0.95",
        "operating_point": summarize_counts([case["classes"] for case in cases]),
        "matched_mask_iou": {name: distribution([score for case in cases for score in case["matched_mask_ious"][name]])
                             for name in CLASSES},
        "front_white_width_at_input_px": distribution([
            case["corners"]["front_white_width_at_input_px"] for case in positives
        ]),
        "corners": {"fitted_positive_images": len(fitted), "failure_positive_images": len(positives) - len(fitted),
                    "unit": "mean front-edge white-key width in source pixels",
                    "per_corner": {name: distribution([case[name] for case in fitted]) for name in CORNER_NAMES},
                    "mean_over_corners_per_image": distribution([float(np.mean(list(case.values()))) for case in fitted]),
                    "worst_corner_per_image": distribution([max(case.values()) for case in fitted])},
        "order": {"positive_images_observed": len(ordered), "positive_images_unobserved": len(positives) - len(ordered),
                  "positive_images_with_inversions": sum(case["corners"]["order_inversions"] > 0 for case in ordered),
                  "positive_images_with_all_keys_correct_and_ordered": sum(
                      case["corners"]["order_inversions"] == 0 and
                      not any(case["classes"][name][field] for name in CLASSES[1:] for field in ("missing", "extra"))
                      for case in ordered),
                  "ground_truth_quad_diagnostic": {
                      "positive_images_observed": sum(value is not None for value in gt_order),
                      "positive_images_with_inversions": sum(value is not None and value > 0 for value in gt_order),
                  }},
        "parity": parity_report,
        "cases": cases,
    }


def make_contact_sheet(target, cases):
    def worst(case):
        return (sum(case["classes"][name][field] for name in CLASSES for field in ("missing", "extra")),
                max((case["corners"]["white_width_error"] or {}).values(), default=0))

    examples = sorted(cases, key=worst, reverse=True)[:12]
    sheet = Image.new("RGB", (1280, 26 + 240 * ((len(examples) + 1) // 2)), "#171c23")
    ImageDraw.Draw(sheet).text((10, 6), "Largest unmatched counts and corner errors", fill="white")
    for i, case in enumerate(examples):
        with Image.open(target / "overlays" / f"{case['id']}.png") as panel:
            panel.thumbnail((632, 232), Image.Resampling.LANCZOS)
            sheet.paste(panel, (4 + 640 * (i % 2), 26 + 240 * (i // 2)))
    sheet.save(target / "contact-sheet.jpg", quality=90)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    for name in ("select", "test"):
        command = commands.add_parser(name)
        command.add_argument("dataset", type=Path)
        command.add_argument("weights", type=Path)
        command.add_argument("output", type=Path)
        command.add_argument("--height", required=True, type=int)
        command.add_argument("--width", required=True, type=int)
        command.add_argument("--onnx", type=Path)
        if name == "test":
            command.add_argument("--selection", required=True, type=Path)
    args = parser.parse_args()
    if any(value < 32 or value % 32 for value in (args.height, args.width)):
        parser.error("height and width must be positive multiples of 32")
    for key in ("dataset", "weights", "output", "onnx", "selection"):
        if path := getattr(args, key, None):
            setattr(args, key, path.resolve())
    evaluate(args)


if __name__ == "__main__":
    main()
