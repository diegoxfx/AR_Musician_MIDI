from pathlib import Path

import torch
import tyro
from transformers import Sam3Processor, Sam3Model, AutoProcessor, AutoModel
import matplotlib
import numpy as np
from einops import rearrange
from PIL import Image

BONGO_PROMPT = "top part of the bongo"
N_FEATS = 384


def overlay_masks(image: Image.Image, masks: torch.Tensor) -> Image.Image:
    """Overlay segmentation masks on image with colors.

    :param image: Input PIL image
    :param masks: Binary masks tensor

    :return: Image with overlaid masks
    """
    image = image.convert("RGBA")
    masks = 255 * masks.cpu().numpy().astype(np.uint8)

    n_masks = masks.shape[0]
    cmap = matplotlib.colormaps.get_cmap("rainbow").resampled(n_masks)
    colors = [tuple(int(c * 255) for c in cmap(i)[:3]) for i in range(n_masks)]

    for mask, color in zip(masks, colors):
        mask = Image.fromarray(mask)
        overlay = Image.new("RGBA", image.size, color + (0,))
        alpha = mask.point(lambda v: int(v * 0.5))
        overlay.putalpha(alpha)
        image = Image.alpha_composite(image, overlay)
    return image


def main(image_dir: Path = Path("./bongo_assets"), width: int = 304, height: int = 224) -> None:
    """Preprocess bongo images using SAM3 and DINOv3.

    :param image_dir: Directory containing input images
    :param width: Target image width
    :param height: Target image height
    """
    hr_base = None
    device = "cuda" if torch.cuda.is_available() else "cpu"

    ph, pw = height // 16, width // 16  # DINOv3 patch size is 16

    print("Loading SAM3...")
    model = Sam3Model.from_pretrained("facebook/sam3").to(device).eval()
    processor = Sam3Processor.from_pretrained("facebook/sam3")

    print("Loading DINOv3...")
    dinov3_path = "facebook/dinov3-vits16-pretrain-lvd1689m"
    dinov3_model = AutoModel.from_pretrained(dinov3_path).to(device).eval()
    dinov3_processor = AutoProcessor.from_pretrained(dinov3_path)

    print("Loading AnyUp...")
    upsampler = torch.hub.load("wimmerth/anyup", "anyup", verbose=False).to(device).eval()

    print(f"SAM3 #parameters: {sum(p.numel() for p in model.parameters())/1e6:.3f}M")
    print(f"DINOv3 #parameters: {sum(p.numel() for p in dinov3_model.parameters())/1e6:.3f}M")
    print(f"AnyUp #parameters: {sum(p.numel() for p in upsampler.parameters())/1e6:.3f}M")

    output_dir = Path("./bongo_outputs")
    output_dir.mkdir(exist_ok=True, parents=True)

    im_paths = sum([list(sorted(image_dir.glob(f"*.{suffix}"))) for suffix in ["png", "jpg", "jpeg"]], start=[])
    for i, im_path in enumerate(im_paths):
        im = Image.open(im_path).convert("RGB")
        im = im.resize((width, height))
        inputs = processor(images=im, text=BONGO_PROMPT, return_tensors="pt").to(device)
        dinov3_inputs = dinov3_processor(
            images=im,
            do_resize=False,
            do_center_crop=False,
            return_tensors="pt",
        )
        hr_image = dinov3_inputs["pixel_values"].to(device)

        with torch.no_grad():
            outputs = model(**inputs)

        with torch.no_grad():
            dinov3_outputs = dinov3_model(pixel_values=hr_image)
            tokens = dinov3_outputs.last_hidden_state[:, 5:, :N_FEATS]  # drop [CLS] + 4 Registers

            lr_features = rearrange(tokens, "b (ph pw) d -> b d ph pw", ph=ph, pw=pw)
            hr_features = upsampler(hr_image, lr_features)

            hr_flat = hr_features[0].permute(1, 2, 0).reshape(-1, tokens.shape[-1])
            if hr_base is None:
                hr_base = hr_flat

            hr_merged = torch.cat([hr_base, hr_flat], dim=0)

            mean = hr_merged.mean(dim=0, keepdim=True)
            X = hr_merged - mean

            U, S, Vh = torch.linalg.svd(X, full_matrices=False)
            pcs = Vh[:3].T

            proj_all = X @ pcs
            X = proj_all[height * width :].reshape(height, width, 3)

            X = (X - X.min()) / (X.max() - X.min())
            im = Image.fromarray((X.detach().cpu().numpy() * 255).astype(np.uint8))
            im.save(output_dir / f"{im_path.stem}_pca.png")

            print(proj_all.shape)

        results = processor.post_process_instance_segmentation(
            outputs,
            threshold=0.5,
            mask_threshold=0.5,
            target_sizes=inputs.get("original_sizes").tolist(),
        )[0]

        masks, scores, boxes = results["masks"], results["scores"], results["boxes"]

        mask = torch.zeros_like(masks[0])
        for j in range(masks.shape[0]):
            mask = mask | masks[j]

        print(f"Image {i+1}/{len(im_paths)}: Found {masks.shape[0]} objects")
        print(scores.tolist(), boxes.tolist())

        im = overlay_masks(im, masks)
        im.save(output_dir / im_path.with_suffix(".png").name)

        print(f"HR Features shape: {hr_features.shape}")

        torch.save(
            {
                "features": hr_features[0].cpu(),
                "mask": mask.cpu(),
            },
            output_dir / im_path.with_suffix(".pt").name,
        )


def entrypoint() -> None:
    """Main entrypoint for preprocessing script."""
    tyro.cli(main)


if __name__ == "__main__":
    entrypoint()
