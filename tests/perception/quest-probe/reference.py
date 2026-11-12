"""Create private ORT fixtures and compare them to raw Sentis tensors and decoded masks."""

import argparse
import ast
import hashlib
import json
from pathlib import Path
import re
import struct
import sys

import cv2
import numpy as np
import onnxruntime as ort
from PIL import Image
from scipy.optimize import linear_sum_assignment

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "SynthesiaSet/piano_perception/keybed_yolo"))
from fit import PINNED_ULTRALYTICS, letterbox, verify_dataset
from evaluate import load_selection
from geometry import CLASSES


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def json_file(path, value):
    path.write_text(json.dumps(value, indent=2, allow_nan=False) + "\n", encoding="utf-8")


def write_float(path, value):
    np.asarray(value, dtype="<f4").tofile(path)


def read_float(path, shape):
    value = np.fromfile(path, dtype="<f4").reshape(shape)
    if not np.isfinite(value).all():
        raise ValueError(f"nonfinite tensor: {path}")
    return value


def operating_point(args, height, width):
    model_hash = sha(args.model)
    if not args.dataset:
        if args.selection:
            raise ValueError("validation selection requires a reviewed dataset")
        cutoff = 0.25 if args.confidence is None else args.confidence
        if not np.isfinite(cutoff) or not 0 <= cutoff <= 1:
            raise ValueError("smoke confidence must be finite and in [0,1]")
        return cutoff, {"modelSha256": model_hash, "datasetProvenanceSha256": None,
                        "validationSelectionSha256": None}
    if not args.selection or args.confidence is not None:
        raise ValueError("reviewed fixtures require --selection and forbid --confidence overrides")
    export_path = args.model.with_name("export-report.json")
    export = json.loads(export_path.read_text(encoding="utf-8"))
    if export["onnx_sha256"] != model_hash:
        raise ValueError("ONNX differs from its export report")
    binding = dict(shape=[height, width], dataset_provenance_sha256=sha(args.dataset / "provenance.json"),
                   weights_sha256=export["weights_sha256"], onnx_sha256=model_hash,
                   classes=list(CLASSES), ultralytics=PINNED_ULTRALYTICS)
    cutoff, selection_hash = load_selection(args.selection, binding)
    return cutoff, {"modelSha256": model_hash, "datasetProvenanceSha256": binding["dataset_provenance_sha256"],
                    "validationSelectionSha256": selection_hash, "exportReportSha256": sha(export_path)}


def prepare(args):
    options = ort.SessionOptions()
    options.intra_op_num_threads = 2
    options.inter_op_num_threads = 1
    session = ort.InferenceSession(str(args.model), sess_options=options, providers=["CPUExecutionProvider"])
    (tensor,) = session.get_inputs()
    batch, channels, height, width = tensor.shape
    if (batch, channels) != (1, 3) or tensor.type != "tensor(float)" or not all(
        isinstance(n, int) and n >= 32 for n in (height, width)
    ):
        raise ValueError(f"expected fixed NCHW float32 input, got {tensor.shape}")
    outputs = session.get_outputs()
    (det,) = [spec for spec in outputs if len(spec.shape) == 3]
    (proto,) = [spec for spec in outputs if len(spec.shape) == 4]
    _, rows, columns = det.shape
    _, planes, ph, pw = proto.shape
    if len(outputs) != 2 or det.shape[0] != 1 or proto.shape[0] != 1 or not all(
        isinstance(n, int) and n > 0 for n in (rows, columns, planes, ph, pw)
    ) or rows > 300 or columns != planes + 6 or any(
        spec.type != "tensor(float)" for spec in outputs
    ):
        raise ValueError("expected NMS-free rows [1,N,6+C] and float32 prototypes [1,C,H,W]")

    names = ast.literal_eval(session.get_modelmeta().custom_metadata_map["names"])
    class_names = tuple(names[i] for i in range(len(names)))
    cutoff, binding = operating_point(args, height, width)
    if args.dataset:
        if class_names != CLASSES:
            raise ValueError(f"reviewed dataset requires classes {CLASSES}, got {class_names}")
        packed = verify_dataset(args.dataset.resolve())
        selected = [(path, row["id"], row["group"]) for path, _, row in packed if row["split"] == "test"]
        selected.sort(key=lambda row: row[1])
        scope = "reviewed, packed held-out test photos"
    else:
        selected = [(path, path.stem, None) for path in args.smoke_image]
        scope = "compatibility smoke fixtures; not a reviewed held-out benchmark"
    if args.max_cases:
        selected = selected[:args.max_cases]
    if not selected:
        raise ValueError("no fixtures selected")
    case_ids = [case_id for _, case_id, _ in selected]
    if len(set(case_ids)) != len(case_ids) or any(not re.fullmatch(r"[a-zA-Z0-9_-]{1,120}", i) for i in case_ids):
        raise ValueError("case IDs must be unique, filename-safe, and at most 120 characters")

    out = args.output
    out.mkdir(parents=True, exist_ok=False)
    config = dict(
        height=height, width=width, rows=rows, columns=columns, protoChannels=planes,
        protoHeight=ph, protoWidth=pw, classes=len(names), detectionName=det.name, prototypeName=proto.name,
        confidence=cutoff, cases=[],
    )
    provenance = dict(
        scope=scope, **binding, confidence=cutoff, onnxRuntime=ort.__version__, numpy=np.__version__,
        opencv=cv2.__version__, classNames=class_names, imageCount=len(selected), cases=[],
    )
    for path, case_id, group in selected:
        with Image.open(path) as original:
            image = original.convert("RGB")
            sw, sh = image.size
            pixels, scale, left, top = letterbox(image, height, width)
        case = dict(id=case_id, left=left, top=top, contentWidth=round(sw * scale), contentHeight=round(sh * scale))
        config["cases"].append(case)
        output_det, output_proto = session.run([det.name, proto.name], {tensor.name: pixels})
        if not np.isfinite(output_det).all() or not np.isfinite(output_proto).all():
            raise ValueError(f"ORT returned nonfinite outputs on {case_id}")
        paths = [out / f"{case_id}.{suffix}" for suffix in ("f32", "det.f32", "proto.f32")]
        for target, value in zip(paths, (pixels, output_det, output_proto), strict=True):
            write_float(target, value)
        provenance["cases"].append(dict(
            id=case_id, group=group, sourceWidth=sw, sourceHeight=sh, sourceImageSha256=sha(path),
            inputSha256=sha(paths[0]), ortDetSha256=sha(paths[1]), ortProtoSha256=sha(paths[2]),
            ortAcceptedRows=int(np.count_nonzero(output_det[0, :, 4] > np.float32(cutoff))),
        ))
    if operating_point(args, height, width) != (cutoff, binding):
        raise ValueError("model, dataset or validation selection changed during fixture preparation")
    json_file(out / "fixture.json", config)
    provenance["fixtureConfigSha256"] = sha(out / "fixture.json")
    json_file(out / "provenance.json", provenance)
    print(f"{scope}: {len(selected)} cases, input {tensor.shape}, ORT CPU")


def crop(row, case):
    left, top = case["left"], case["top"]
    right, bottom = left + case["contentWidth"], top + case["contentHeight"]
    return np.clip(row[:4], [left, top, left, top], [right, bottom, right, bottom])


def mask_for(row, prototypes, config, case):
    h, w = config["height"], config["width"]
    channels, ph, pw = prototypes.shape
    logits = (row[6:] @ prototypes.reshape(channels, -1)).reshape(ph, pw)
    resized = cv2.resize(logits, (w, h), interpolation=cv2.INTER_LINEAR)
    x1, y1, x2, y2 = np.ceil(crop(row, case)).astype(int)
    mask = np.zeros((h, w), dtype=bool)
    mask[y1:y2, x1:x2] = resized[y1:y2, x1:x2] > 0
    return mask


def packed_masks(path, h, w, rows):
    stride = (h * w + 7) // 8
    contents = path.read_bytes()
    if len(contents) % (4 + stride):
        raise ValueError(f"wrong mask file length: {path}")
    masks = {}
    for start in range(0, len(contents), 4 + stride):
        (row,) = struct.unpack_from("<i", contents, start)
        if row not in rows or row in masks:
            raise ValueError(f"unexpected/duplicate mask row {row}: {path}")
        bits = np.frombuffer(contents, dtype="u1", offset=start + 4, count=stride)
        masks[row] = np.unpackbits(bits, count=h * w, bitorder="little").reshape(h, w).astype(bool)
    if set(masks) != set(rows):
        raise ValueError(f"missing device mask rows: {path}")
    return masks


def box_iou(first, second):
    area = lambda box: np.maximum(box[2:] - box[:2], 0).prod()
    extent = np.minimum(first[2:], second[2:]) - np.maximum(first[:2], second[:2])
    intersect = np.maximum(extent, 0).prod()
    union = area(first) + area(second) - intersect
    if union == 0:
        return float(np.max(np.abs(first - second)) <= 1e-3)
    return float(intersect / union)


def mask_iou(a, b):
    union = np.count_nonzero(a | b)
    return np.count_nonzero(a & b) / union if union else 1.0


def original_box(box, case, source):
    x, y = case["left"], case["top"]
    sx = source["sourceWidth"] / case["contentWidth"]
    sy = source["sourceHeight"] / case["contentHeight"]
    return [(float(a) - shift) * scale for a, shift, scale in zip(box, (x, y, x, y), (sx, sy, sx, sy))]


def summary(diff):
    return dict(meanAbs=float(np.mean(diff)), maxAbs=float(np.max(diff)))


def match_rows(det_ref, det_actual, ref_rows, actual_rows, proto_ref, device_masks, config, case, source, limits):
    valid = np.zeros((len(ref_rows), len(actual_rows)), dtype=bool)
    pairs = {}
    actual_boxes = {j: crop(det_actual[j], case) for j in actual_rows}
    for ref_index, i in enumerate(ref_rows):
        row = det_ref[i]
        reference_box = crop(row, case)
        reference_mask = mask_for(row, proto_ref, config, case)
        for actual_index, j in enumerate(actual_rows):
            observed = det_actual[j]
            if observed[5] != row[5]:
                continue
            box_delta = float(np.max(np.abs(observed[:4] - row[:4])))
            score_delta = float(abs(observed[4] - row[4]))
            if box_delta > limits["maxBoxPx"] or score_delta > limits["maxScoreDiff"]:
                continue
            overlap = box_iou(reference_box, actual_boxes[j])
            if overlap < limits["matchingBoxIoU"]:
                continue
            actual_mask = device_masks[j]
            mask_overlap = mask_iou(reference_mask, actual_mask)
            if mask_overlap < limits["minMaskIoU"]:
                continue
            valid[ref_index, actual_index] = True
            pairs[ref_index, actual_index] = dict(
                ortRow=i, sentisRow=j, classId=int(row[5]), boxIoU=overlap,
                maxBoxDiffPx=box_delta, scoreDiff=score_delta,
                coefficientError=summary(np.abs(observed[6:] - row[6:])),
                maskIoU=mask_overlap, ortMaskPixels=int(reference_mask.sum()),
                sentisMaskPixels=int(actual_mask.sum()),
                sentisSourceBox=original_box(actual_boxes[j], case, source),
            )
    # Multiple valid rows may share the same class, box and confidence. Match
    # as many valid pairs as possible without depending on either engine's row order.
    assigned_ref, assigned_actual = linear_sum_assignment(valid, maximize=True)
    return [pairs[i, j] for i, j in zip(assigned_ref, assigned_actual, strict=True) if valid[i, j]]


def load_fixture(root):
    config_path = root / "fixture.json"
    provenance = json.loads((root / "provenance.json").read_text(encoding="utf-8"))
    if sha(config_path) != provenance["fixtureConfigSha256"]:
        raise ValueError("fixture configuration hash changed")
    config = json.loads(config_path.read_text(encoding="utf-8"))
    ids = [item["id"] for item in config["cases"]]
    refs = [item["id"] for item in provenance["cases"]]
    if not ids or len(set(ids)) != len(ids) or len(set(refs)) != len(refs) or set(ids) != set(refs) or any(
        not re.fullmatch(r"[a-zA-Z0-9_-]{1,120}", case_id) for case_id in ids
    ):
        raise ValueError("fixture and provenance require the same unique safe case IDs")
    if config["confidence"] != provenance["confidence"]:
        raise ValueError("fixture confidence differs from provenance")
    for case in provenance["cases"]:
        for suffix, key in (("f32", "inputSha256"), ("det.f32", "ortDetSha256"), ("proto.f32", "ortProtoSha256")):
            if sha(root / f"{case['id']}.{suffix}") != case[key]:
                raise ValueError(f"fixture tensor hash changed: {case['id']}.{suffix}")
    return config, provenance


def verified_run(results, reference=None):
    metadata = json.loads((results / "device-summary.json").read_text(encoding="utf-8"))
    if metadata["outcome"] != "Executed":
        raise ValueError("device run did not complete successfully")
    if sha(results / "run.json") != metadata["runConfigSha256"]:
        raise ValueError("device run configuration hash changed")
    config = json.loads((results / "run.json").read_text(encoding="utf-8"))
    ids = [item["id"] for item in config["cases"]]
    if not ids or len(set(ids)) != len(ids) or any(not re.fullmatch(r"[a-zA-Z0-9_-]{1,120}", i) for i in ids):
        raise ValueError("invalid device case IDs")
    required = {"run.json", "device/benchmark.json"} | {
        f"device/{case_id}.{suffix}" for case_id in ids for suffix in ("det.f32", "proto.f32", "masks")
    }
    recorded = set()
    for artifact in metadata["resultFiles"]:
        name = artifact["path"]
        path = results / name
        if name in recorded or "\\" in name or ".." in Path(name).parts or not path.resolve().is_relative_to(results.resolve()):
            raise ValueError("invalid or duplicate device result path")
        recorded.add(name)
        if sha(path) != artifact["sha256"]:
            raise ValueError(f"device result hash changed: {name}")
    if not required <= recorded:
        raise ValueError("device receipt does not bind all output artifacts")
    if reference is not None:
        fixture, provenance = load_fixture(reference)
        if (sha(reference / "provenance.json") != metadata["fixtureProvenanceSha256"] or
            provenance["fixtureConfigSha256"] != metadata["fixtureConfigSha256"] or
            provenance["modelSha256"] != metadata["modelSha256"] or
            {key: value for key, value in config.items() if key not in ("backend", "repeats", "sliceBudgetMs")} != fixture):
            raise ValueError("device run belongs to a different fixture, model or operating point")
    benchmark = json.loads((results / "device/benchmark.json").read_text(encoding="utf-8"))
    if benchmark["interrupted"] or benchmark["backend"] != config["backend"] or metadata["backend"] != config["backend"]:
        raise ValueError("device run was interrupted or used a different backend")
    if not isinstance(config["repeats"], int) or config["repeats"] < 1:
        raise ValueError("device repeats must be positive")
    expected = [(cycle, case_id) for cycle in range(config["repeats"]) for case_id in ids]
    passes = benchmark["inference"]
    if [(item["cycle"], item["id"]) for item in passes] != expected or not passes[0]["cold"] or any(
        item["cold"] for item in passes[1:]
    ):
        raise ValueError("benchmark does not cover all configured cycles in order")
    return metadata, config, benchmark


def compare(args):
    expected = args.reference
    actual = args.actual
    if actual.name != "device":
        raise ValueError("--actual must be the runner's device directory with its parent receipt")
    metadata, _, _ = verified_run(actual.parent, expected)
    config, provenance = load_fixture(expected)
    by_id = {item["id"]: item for item in provenance["cases"]}
    shape = config["rows"], config["columns"]
    proto_shape = config["protoChannels"], config["protoHeight"], config["protoWidth"]
    h, w = config["height"], config["width"]
    cutoff = np.float32(config["confidence"])
    report = dict(
        scope=provenance["scope"], modelSha256=provenance["modelSha256"], confidence=config["confidence"],
        validationSelectionSha256=provenance.get("validationSelectionSha256"),
        deviceReceiptSha256=sha(actual.parent / "device-summary.json"),
        thresholds=dict(prototypeAbs=0.001, prototypeRel=0.001, matchingBoxIoU=0.5,
                        maxBoxPx=1.0, maxScoreDiff=0.01, minMaskIoU=0.97, minDecoderIoU=0.99),
        cases=[], passParity=True, nonemptyComparedMasks=0,
    )
    limits = report["thresholds"]
    for case in config["cases"]:
        case_id = case["id"]
        expected_det_path, expected_proto_path = (expected / f"{case_id}.{suffix}.f32" for suffix in ("det", "proto"))
        source = by_id[case_id]
        if sha(expected_det_path) != source["ortDetSha256"] or sha(expected_proto_path) != source["ortProtoSha256"]:
            raise ValueError(f"ORT reference hash changed: {case_id}")
        det_ref = read_float(expected_det_path, shape)
        proto_ref = read_float(expected_proto_path, proto_shape)
        det_actual = read_float(actual / f"{case_id}.det.f32", shape)
        proto_actual = read_float(actual / f"{case_id}.proto.f32", proto_shape)
        ref_rows = np.flatnonzero(det_ref[:, 4] > cutoff).tolist()
        actual_rows = np.flatnonzero(det_actual[:, 4] > cutoff).tolist()
        device_masks = packed_masks(actual / f"{case_id}.masks", h, w, actual_rows)
        decoder_iou = {
            i: mask_iou(mask_for(det_actual[i], proto_actual, config, case), device_masks[i])
            for i in actual_rows
        }
        matches = match_rows(
            det_ref, det_actual, ref_rows, actual_rows, proto_ref, device_masks, config, case, source, limits,
        )
        result = dict(
            id=case_id, ortRows=len(ref_rows), sentisRows=len(actual_rows),
            unmatchedOrt=sorted(set(ref_rows) - {item["ortRow"] for item in matches}),
            unmatchedSentis=sorted(set(actual_rows) - {item["sentisRow"] for item in matches}),
            prototypeError=summary(np.abs(proto_actual - proto_ref)), matches=matches,
            worstDecoderIoU=min(decoder_iou.values(), default=None),
        )
        result["passPrototype"] = bool(np.allclose(
            proto_actual, proto_ref, rtol=limits["prototypeRel"], atol=limits["prototypeAbs"]
        ))
        report["nonemptyComparedMasks"] += sum(
            row["ortMaskPixels"] > 0 and row["sentisMaskPixels"] > 0 for row in matches
        )
        result["passParity"] = (
            result["passPrototype"] and not result["unmatchedOrt"] and not result["unmatchedSentis"]
            and min(decoder_iou.values(), default=1) >= limits["minDecoderIoU"]
        )
        report["cases"].append(result)
        report["passParity"] &= result["passParity"]
    report["maskParityObserved"] = report["nonemptyComparedMasks"] > 0
    json_file(args.report, report)
    print(f"parity={report['passParity']} images={len(report['cases'])} nonempty_mask_pairs={report['nonemptyComparedMasks']}")
    return 0 if report["passParity"] and report["maskParityObserved"] else 1


def summarize(args):
    metadata, config, benchmark = verified_run(args.results)
    cold, *warm = benchmark["inference"]

    def distribution(field):
        values = [item[field] for item in warm]
        if not values:
            return None
        return dict(median=float(np.median(values)), p95=float(np.percentile(values, 95)), max=max(values))

    report = dict(
        backend=benchmark["backend"], device=benchmark["device"], gpu=benchmark["gpu"],
        graphicsApi=benchmark["graphicsApi"], interrupted=benchmark["interrupted"], confidence=config["confidence"],
        deviceReceiptSha256=sha(args.results / "device-summary.json"),
        onnxSha256=metadata["modelSha256"], sentisSha256=metadata["sentisSha256"], apkSha256=metadata["apkSha256"],
        peakSampledAppPssMiB=(metadata["peakSampledPssKiB"] / 1024 if metadata["peakSampledPssKiB"] else None),
        modelLoadMs=benchmark["modelLoadMs"], workerCreateMs=benchmark["workerCreateMs"],
        idleFrames=benchmark["idleFrames"], cold=cold, warmPasses=len(warm),
        warm={field: distribution(field) for field in (
            "inputMs", "scheduleWallMs", "scheduleMainMs", "readbackMs", "decodeMs", "inferenceMs"
        )},
        warmWorstFrameMs=max((item["frames"]["maxMs"] for item in warm), default=None),
        warmFramesOver25Ms=sum(item["frames"]["over25Ms"] for item in warm),
        warmFramesOver50Ms=sum(item["frames"]["over50Ms"] for item in warm),
    )
    json_file(args.report, report)
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    fixtures = commands.add_parser("prepare")
    fixtures.add_argument("--model", required=True, type=Path)
    fixtures.add_argument("--output", required=True, type=Path)
    source = fixtures.add_mutually_exclusive_group(required=True)
    source.add_argument("--dataset", type=Path)
    source.add_argument("--smoke-image", nargs="+", type=Path)
    fixtures.add_argument("--max-cases", type=int)
    fixtures.add_argument("--selection", type=Path, help="frozen validation selection, required with --dataset")
    fixtures.add_argument("--confidence", type=float, help="smoke fixtures only; default 0.25")
    parity = commands.add_parser("compare")
    parity.add_argument("--reference", required=True, type=Path)
    parity.add_argument("--actual", required=True, type=Path)
    parity.add_argument("--report", required=True, type=Path)
    timing = commands.add_parser("summarize")
    timing.add_argument("--results", required=True, type=Path)
    timing.add_argument("--report", required=True, type=Path)
    args = parser.parse_args()
    if args.command == "prepare":
        if (args.confidence is not None and not 0 <= args.confidence <= 1) or (args.max_cases is not None and args.max_cases < 1):
            parser.error("confidence must be in [0,1] and max-cases must be positive")
        prepare(args)
        return 0
    return compare(args) if args.command == "compare" else summarize(args)


if __name__ == "__main__":
    raise SystemExit(main())
