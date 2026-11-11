"""Create local SAM 3.1 label proposals for manual review."""

import argparse
from math import dist
from pathlib import Path
import tempfile

import numpy as np
from PIL import Image, ImageDraw

from dataset import annotate, image_path, read_json, sha256_file, write_json
from geometry import CLASSES, checked_polygon, checked_quad


def quad_from_mask(mask):
    import cv2

    height, width = mask.shape
    if mask[0].any() or mask[-1].any() or mask[:, 0].any() or mask[:, -1].any():
        raise ValueError("mask touches an image edge")
    total_pixels = int(mask.sum())
    if total_pixels < 0.003 * width * height:
        raise ValueError("mask too small for a full keybed")
    contours, _ = cv2.findContours(mask.astype(np.uint8), cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    largest = max(contours, key=cv2.contourArea)
    hull = cv2.convexHull(largest)
    perimeter = cv2.arcLength(hull, True)
    for fraction in (0.005, 0.01, 0.015, 0.02, 0.025, 0.03):
        candidate = cv2.approxPolyDP(hull, fraction * perimeter, True)
        if len(candidate) == 4:
            break
    if len(candidate) != 4:
        raise ValueError("mask has no stable four-corner fit")
    contour_points = candidate[:, 0].tolist()
    edges = list(zip(contour_points, contour_points[1:] + contour_points[:1]))
    along = max((edges[0::2], edges[1::2]), key=lambda pair: sum(dist(*edge) for edge in pair))
    back, front = sorted(along, key=lambda edge: edge[0][1] + edge[1][1])
    top_left, top_right = sorted(back)
    bottom_left, bottom_right = sorted(front)
    quad = checked_quad([top_left, top_right, bottom_right, bottom_left], width, height)
    fitted = np.zeros_like(mask, dtype=np.uint8)
    cv2.fillConvexPoly(fitted, np.asarray(quad, dtype=np.int32), 1)
    iou = np.logical_and(fitted, mask).sum() / np.logical_or(fitted, mask).sum()
    if iou < 0.72:
        raise ValueError(f"mask is fragmented or far from its four-corner fit ({iou:.2f} IoU)")
    return quad, float(iou)


def key_polygon(mask):
    import cv2

    height, width = mask.shape
    if mask[0].any() or mask[-1].any() or mask[:, 0].any() or mask[:, -1].any():
        raise ValueError("mask touches an image edge")
    contours, _ = cv2.findContours(mask.astype(np.uint8), cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    if not contours:
        raise ValueError("empty key mask")
    contour = max(contours, key=cv2.contourArea)
    perimeter = cv2.arcLength(contour, True)
    polygon = cv2.approxPolyDP(contour, max(0.6, 0.005 * perimeter), True)[:, 0].tolist()
    polygon = checked_polygon(polygon, width, height)
    filled = np.zeros_like(mask, dtype=np.uint8)
    cv2.fillPoly(filled, [np.asarray(polygon, dtype=np.int32)], 1)
    iou = np.logical_and(filled, mask).sum() / np.logical_or(filled, mask).sum()
    if iou < 0.72:
        raise ValueError("mask is fragmented or has a large internal hole")
    return polygon, float(iou)


def save_overlay(image, overlay, candidates, prompt, path):
    draw = ImageDraw.Draw(overlay)
    for candidate in candidates:
        if candidate.get("corners"):
            annotate(overlay, candidate["corners"])
        if polygon := candidate.get("polygon"):
            draw.line([*polygon, polygon[0]], width=2, fill="#ffe180")
            x, y = min(polygon, key=lambda point: point[1])
            draw.text((x, y), candidate["id"], fill="#ffe180", stroke_fill="black", stroke_width=1)
    banner = Image.new("RGB", (image.width, image.height + 25), "#20252a")
    banner.paste(overlay, (0, 25))
    accepted = sum("corners" in item or "polygon" in item for item in candidates)
    ImageDraw.Draw(banner).text((7, 6), f"{prompt} | {accepted}/{len(candidates)} shape candidates | review required", fill="white")
    banner.save(path)


def propose(workspace, checkpoint, device, edge, bed_prompts, threshold, selected):
    import torch
    import transformers
    from transformers import Sam3Config, Sam3Model, Sam3Processor

    output = workspace / "proposals"
    if output.exists():
        raise ValueError(f"proposals already exist: {output}")
    inventory = read_json(workspace / "inventory.json")["images"]
    if selected:
        if not selected <= {row["id"] for row in inventory}:
            raise ValueError("selected image id is not in the inventory")
        inventory = [row for row in inventory if row["id"] in selected]
    prompts = [(CLASSES[0], prompt) for prompt in bed_prompts]
    prompts.extend(zip(CLASSES[1:], ("white piano keys", "black piano keys")))
    processor = Sam3Processor.from_pretrained(checkpoint, local_files_only=True)
    processor.image_processor.size = {"height": edge, "width": edge}
    config = Sam3Config.from_pretrained(checkpoint, local_files_only=True)
    config.vision_config.backbone_config.image_size = edge
    dtype = torch.float16 if device.startswith("cuda") else torch.float32
    model = Sam3Model.from_pretrained(checkpoint, config=config, dtype=dtype, local_files_only=True).to(device).eval()
    records = []
    with tempfile.TemporaryDirectory(dir=workspace) as staging, torch.inference_mode():
        target = Path(staging) / "proposals"
        target.mkdir()
        for row in inventory:
            with Image.open(image_path(workspace, row)) as original:
                image = original.convert("RGB")
            initial = processor(images=image, text=prompts[0][1], return_tensors="pt").to(device, dtype)
            vision = model.get_vision_features(initial.pixel_values)
            candidates = []
            for prompt_index, (class_name, prompt) in enumerate(prompts, start=1):
                inputs = processor(images=image, text=prompt, return_tensors="pt").to(device, dtype)
                outputs = model(vision_embeds=vision, input_ids=inputs.input_ids, attention_mask=inputs.attention_mask)
                detections = processor.post_process_instance_segmentation(
                    outputs, threshold=threshold, mask_threshold=0.5, target_sizes=[(image.height, image.width)]
                )[0]
                limit = 2 if class_name == CLASSES[0] else 96
                order = detections["scores"].argsort(descending=True)[:limit]
                by_prompt = []
                pixels = np.asarray(image).copy()
                for rank, index in enumerate(order.tolist(), start=1):
                    mask = detections["masks"][index].cpu().numpy().astype(bool)
                    candidate_id = f"p{prompt_index}-{rank}"
                    candidate = {"id": candidate_id, "class": class_name, "prompt": prompt,
                                 "score": detections["scores"][index].item()}
                    try:
                        if class_name == CLASSES[0]:
                            candidate["corners"], candidate["mask_shape_iou"] = quad_from_mask(mask)
                        else:
                            candidate["polygon"], candidate["mask_shape_iou"] = key_polygon(mask)
                    except ValueError as error:
                        candidate["rejection"] = str(error)
                    color = np.array([27, 208, 160]) if "rejection" not in candidate else np.array([205, 65, 95])
                    pixels[mask] = np.rint(0.65 * pixels[mask] + 0.35 * color).astype(np.uint8)
                    candidate["overlay"] = f"{row['id']}--p{prompt_index}.png"
                    by_prompt.append(candidate)
                save_overlay(image, Image.fromarray(pixels), by_prompt, prompt, target / f"{row['id']}--p{prompt_index}.png")
                candidates.extend(by_prompt)
            records.append({"id": row["id"], "image_sha256": row["image_sha256"], "candidates": candidates})
            counts = {name: sum(c["class"] == name and "rejection" not in c for c in candidates) for name in CLASSES}
            print(f"{row['id']}: {counts} from {len(candidates)} masks", flush=True)
        checkpoint_files = sorted((*checkpoint.glob("*.json"), *checkpoint.glob("*.safetensors")))
        write_json(target / "index.json", {"schema": 1, "checkpoint": str(checkpoint),
                                           "checkpoint_sha256": {p.name: sha256_file(p) for p in checkpoint_files},
                                           "transformers": transformers.__version__, "torch": torch.__version__,
                                           "input_edge": edge, "threshold": threshold, "mask_threshold": 0.5,
                                           "prompts": prompts, "images": records})
        target.rename(output)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("workspace", type=Path)
    parser.add_argument("checkpoint", type=Path)
    parser.add_argument("--device", default="cuda")
    parser.add_argument("--edge", choices=(672, 1008), type=int, default=672)
    parser.add_argument("--bed-prompt", action="append")
    parser.add_argument("--image-id", action="append")
    parser.add_argument("--threshold", type=float, default=0.35)
    args = parser.parse_args()
    if not args.checkpoint.is_dir():
        parser.error("checkpoint must be an existing local directory")
    if not 0 < args.threshold < 1:
        parser.error("threshold must be between 0 and 1")
    prompts = args.bed_prompt or ["piano keys", "piano keyboard keybed"]
    propose(args.workspace, args.checkpoint.resolve(), args.device, args.edge, prompts, args.threshold,
            set(args.image_id or ()))


if __name__ == "__main__":
    main()
