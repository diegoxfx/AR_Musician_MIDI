"""Train local corner heatmaps on real TRAIN crops; refine prediction-only seeds."""

import argparse
from dataclasses import asdict, dataclass
from math import dist
from pathlib import Path

import cv2
import numpy as np
from PIL import Image
import torch
from torch import nn
from torch.nn import functional as F

from corner_pose import verify_pose_dataset
from dataset import read_json, sha256_file, write_json
from geometry import checked_quad
from resource_budget import ResourceBudget


@dataclass(frozen=True)
class RefinementConfig:
    size: int = 128
    crop_fraction: float = .20
    minimum_crop: float = 48.
    maximum_move_fraction: float = .40
    minimum_peak: float = .0005

    def __post_init__(self):
        if type(self.size) is not int or self.size < 32 or self.size % 8:
            raise ValueError("heatmap size must be a multiple of eight, at least 32")
        values = (self.crop_fraction, self.minimum_crop, self.maximum_move_fraction, self.minimum_peak)
        if not np.isfinite(values).all() or not (0 < self.crop_fraction < 1 and self.minimum_crop > 0
                and 0 < self.maximum_move_fraction < .5 and 0 <= self.minimum_peak < 1):
            raise ValueError("invalid refinement bounds")


def crop_transform(center, extent, size):
    center = np.asarray(center, dtype=float)
    if center.shape != (2,) or not np.isfinite(center).all() or not np.isfinite(extent) or extent <= 0:
        raise ValueError("finite crop center and positive extent required")
    scale = size / extent
    return np.float64([[scale, 0, (size - 1) / 2 - center[0] * scale],
                       [0, scale, (size - 1) / 2 - center[1] * scale]])


def crop_image(image, center, extent, size):
    matrix = crop_transform(center, extent, size)
    return cv2.warpAffine(image, matrix, (size, size), flags=cv2.INTER_LINEAR,
                          borderMode=cv2.BORDER_CONSTANT, borderValue=(114, 114, 114)), matrix


def inverse_point(point, matrix):
    return cv2.invertAffineTransform(matrix) @ np.r_[point, 1.]


def block(inputs, outputs, stride=1):
    return nn.Sequential(nn.Conv2d(inputs, outputs, 3, stride=stride, padding=1), nn.ReLU(),
                         nn.Conv2d(outputs, outputs, 3, padding=1), nn.ReLU())


class CornerHeatmaps(nn.Module):
    """Four role-specific maps; no coordinates, labels or profile enter the model."""
    def __init__(self):
        super().__init__()
        self.first = block(3, 16, 2)
        self.second = block(16, 32, 2)
        self.third = block(32, 48, 2)
        self.up_second = block(80, 32)
        self.up_first = block(48, 16)
        self.last = nn.Sequential(nn.Conv2d(16, 8, 3, padding=1), nn.ReLU(), nn.Conv2d(8, 4, 1))

    def forward(self, image):
        first = self.first(image)
        second = self.second(first)
        third = self.third(second)
        up = self.up_second(torch.cat((F.interpolate(third, size=second.shape[-2:], mode="bilinear",
                                                      align_corners=False), second), 1))
        up = self.up_first(torch.cat((F.interpolate(up, size=first.shape[-2:], mode="bilinear",
                                                     align_corners=False), first), 1))
        return self.last(F.interpolate(up, size=image.shape[-2:], mode="bilinear", align_corners=False))


def heatmap_point(logits):
    if logits.ndim != 2 or not np.isfinite(logits).all():
        raise ValueError("expected finite 2D heatmap logits")
    probabilities = np.exp(logits - logits.max())
    probabilities /= probabilities.sum()
    y, x = np.unravel_index(probabilities.argmax(), probabilities.shape)
    # Local expectation gives subpixel coordinates without averaging distant modes.
    left, top = max(0, x - 3), max(0, y - 3)
    part = probabilities[top:y + 4, left:x + 4]
    ys, xs = np.mgrid[top:top + part.shape[0], left:left + part.shape[1]]
    point = np.array([(part * xs).sum(), (part * ys).sum()]) / part.sum()
    return point, float(probabilities.max())


def refine_quad(model, image, quad, config=RefinementConfig()):
    """Only RGB and a predicted source-pixel quad determine inference crops."""
    if quad is None:
        return {"quad": None, "rejection": "no valid coarse seed", "peaks": []}
    try:
        quad = np.asarray(checked_quad(np.asarray(quad).tolist(), image.shape[1], image.shape[0]))
    except ValueError as exc:
        return {"quad": None, "rejection": "coarse seed: " + str(exc), "peaks": []}
    extent = max(config.minimum_crop, dist(quad[2], quad[3]) * config.crop_fraction)
    crops, matrices = zip(*(crop_image(image, point, extent, config.size) for point in quad), strict=True)
    inputs = torch.from_numpy(np.ascontiguousarray(np.stack(crops).transpose(0, 3, 1, 2))).float() / 255
    with torch.inference_mode():
        output = model(inputs).cpu().numpy()
    refined, peaks, retained = [], [], []
    for index, (seed, matrix) in enumerate(zip(quad, matrices, strict=True)):
        point, peak = heatmap_point(output[index, index])
        proposed = inverse_point(point, matrix)
        unchanged = bool(peak < config.minimum_peak or np.linalg.norm(proposed - seed) >
                         config.maximum_move_fraction * extent)
        refined.append(seed.tolist() if unchanged else proposed.tolist())
        peaks.append(peak)
        retained.append(unchanged)
    try:
        checked_quad(refined, image.shape[1], image.shape[0])
    except ValueError as exc:
        return {"quad": None, "rejection": "refined geometry: " + str(exc), "peaks": peaks,
                "proposed_quad": refined, "retained_seed": retained, "crop_extent": extent}
    return {"quad": refined, "rejection": None, "peaks": peaks, "retained_seed": retained,
            "crop_extent": extent, "status": "needs_confirmation"}


def score(reference, result):
    quad = result["quad"]
    unit = dist(reference["corners"][2], reference["corners"][3]) / reference["white_keys"]
    errors = [dist(a, b) / unit for a, b in zip(quad, reference["corners"], strict=True)] if quad else None
    return {"id": reference["id"], **result, "errors_white_widths": errors,
            "strict": errors is not None and max(errors) <= .25}


def summary(cases):
    errors = [max(case["errors_white_widths"]) for case in cases if case["errors_white_widths"] is not None]
    capped = (sum(min(value, 4) for value in errors) + 4 * (len(cases) - len(errors))) / len(cases)
    return {"total": len(cases), "fitted": len(errors), "strict": sum(case["strict"] for case in cases),
            "mean_capped_max_error": capped, "median_max_error_fitted": float(np.median(errors)) if errors else None}


def validate_seeds(manifest, records):
    expected = {row["id"] for row in records if row["split"] == "val"}
    if manifest.get("split") != "val" or not expected or not manifest.get("variants"):
        raise ValueError("validation seeds are required")
    for variant in manifest["variants"].values():
        if set(variant["quads"]) != expected or not variant.get("source_sha256"):
            raise ValueError("seed identity set or provenance is invalid")


def evaluate(model, records, images, seeds, config):
    report = {}
    for name, variant in seeds["variants"].items():
        cases = [score(row, refine_quad(model, images[row["id"]], variant["quads"][row["id"]], config))
                 for row in records if row["split"] == "val"]
        report[name] = {"summary": summary(cases), "cases": cases}
    return report


class TrainingCrops(torch.utils.data.Dataset):
    def __init__(self, records, images, size=128, seed=42):
        self.records = [row for row in records if row["split"] == "train"]
        self.images, self.size, self.seed, self.epoch = images, size, seed, 0
        self.yy, self.xx = np.mgrid[:size, :size]

    def __len__(self):
        return len(self.records) * 4

    def __getitem__(self, index):
        row, role = self.records[index // 4], index % 4
        rng = np.random.default_rng(self.seed + self.epoch * len(self) + index)
        extent = max(48., dist(row["corners"][2], row["corners"][3]) * rng.uniform(.14, .28))
        truth = np.asarray(row["corners"][role])
        center = truth + rng.uniform(-.30, .30, 2) * extent
        crop, matrix = crop_image(self.images[row["id"]], center, extent, self.size)
        target = matrix @ np.r_[truth, 1.]
        heatmap = np.exp(-((self.xx - target[0]) ** 2 + (self.yy - target[1]) ** 2) / (2 * 2.0 ** 2))
        heatmap /= heatmap.sum()
        pixels = np.clip(crop.astype(np.float32) / 255 * rng.uniform(.80, 1.20), 0, 1)
        return np.ascontiguousarray(pixels.transpose(2, 0, 1)), role, heatmap.astype(np.float32)


def train(dataset, seeds_path, output, epochs=40, batch=24, seed=42):
    if output.exists():
        raise ValueError("training output already exists")
    budget = ResourceBudget(output.parent / f"{output.name}-resources.jsonl")
    budget.configure_torch("cpu")
    torch.set_num_threads(4)
    cv2.setNumThreads(1)
    torch.manual_seed(seed)
    records = verify_pose_dataset(dataset)
    seeds = read_json(seeds_path)
    validate_seeds(seeds, records)
    used = [row for row in records if row["split"] in ("train", "val")]
    images = {}
    for row in used:
        with Image.open(dataset / "images" / row["split"] / f'{row["id"]}.png') as image:
            images[row["id"]] = np.asarray(image.convert("RGB"))
    config = RefinementConfig()
    crops = TrainingCrops(records, images, config.size, seed)
    loader = torch.utils.data.DataLoader(crops, batch_size=batch, shuffle=True, num_workers=0,
                                         generator=torch.Generator().manual_seed(seed))
    model = CornerHeatmaps()
    optimizer = torch.optim.AdamW(model.parameters(), lr=.001, weight_decay=.0001)
    output.mkdir(parents=True)
    source_hash = sha256_file(Path(__file__))
    data_hash = sha256_file(dataset / "pose-provenance.json")
    seed_hash = sha256_file(seeds_path)
    history, best = [], (-1, -float("inf"))
    for epoch in range(epochs):
        crops.epoch = epoch
        model.train()
        total = 0.
        for image, role, target in loader:
            optimizer.zero_grad()
            prediction = model(image)[torch.arange(len(image)), role]
            loss = -(target.flatten(1) * F.log_softmax(prediction.flatten(1), dim=1)).sum(1).mean()
            loss.backward()
            torch.nn.utils.clip_grad_norm_(model.parameters(), 10.)
            optimizer.step()
            total += float(loss.detach()) * len(image)
        row = {"epoch": epoch + 1, "loss": total / len(crops)}
        if (epoch + 1) % 5 == 0 or epoch == epochs - 1:
            model.eval()
            report = evaluate(model, records, images, seeds, config)
            row["validation"] = {name: value["summary"] for name, value in report.items()}
            for name, result in report.items():
                rank = (result["summary"]["strict"], -result["summary"]["mean_capped_max_error"])
                if rank > best:
                    best = rank
                    torch.save(model.state_dict(), output / "best.pt")
                    write_json(output / "selection.json", {"epoch": epoch + 1, "seed_variant": name,
                               "config": asdict(config), "summary": result["summary"],
                               "dataset_sha256": data_hash, "seeds_sha256": seed_hash,
                               "source_code_sha256": source_hash, "checkpoint_sha256": sha256_file(output / "best.pt")})
                    write_json(output / "best-validation.json", report)
        history.append(row)
        write_json(output / "history.json", history)
        print(row, flush=True)
    torch.save(model.state_dict(), output / "last.pt")
    if (data_hash != sha256_file(dataset / "pose-provenance.json") or seed_hash != sha256_file(seeds_path)
            or source_hash != sha256_file(Path(__file__))):
        raise ValueError("training inputs changed during the run")
    write_json(output / "training-provenance.json", {"epochs": epochs, "batch": batch, "seed": seed,
               "train_images": len(crops.records), "val_images": len(used) - len(crops.records),
               "parameters": sum(parameter.numel() for parameter in model.parameters()),
               "source_code_sha256": source_hash, "dataset_sha256": data_hash, "seeds_sha256": seed_hash,
               "config": asdict(config), "resource_policy": budget.cpu, "device": "cpu",
               "scope": "Real TRAIN crops only; checkpoints and seed variant selected on VAL. No TEST inference."})


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("dataset", type=Path)
    parser.add_argument("seeds", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--epochs", type=int, default=40)
    args = parser.parse_args()
    if args.epochs < 1:
        parser.error("positive epoch count required")
    train(args.dataset.resolve(), args.seeds.resolve(), args.output.resolve(), args.epochs)
