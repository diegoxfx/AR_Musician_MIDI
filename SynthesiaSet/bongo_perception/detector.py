from pathlib import Path

import torch
import torch.nn as nn
import torch.nn.functional as F
from transformers import AutoModel, AutoProcessor
from einops import rearrange
import kornia as K
import matplotlib.pyplot as plt
from matplotlib.patches import Ellipse
import numpy as np
from PIL import Image
import tyro


class BongoDetector(nn.Module):
    def __init__(
        self,
        feats: dict,
        backbone: str = "facebook/dinov3-vits16-pretrain-lvd1689m",
        n_feature_dims: int = 384,
        width: int = 304,
        height: int = 224,
        thresh: float = 0.75,
    ):
        """BongoDetector module for detecting bongos and computing ellipse parameters.

        :param feats: Dictionary containing reference features and mask
        :param backbone: DINOv3 model path
        :param n_feature_dims: Number of feature dimensions to use
        :param width: Image width
        :param height: Image height
        :param thresh: Threshold for similarity map
        """
        super().__init__()

        self.n_feature_dims = n_feature_dims
        self.width = width
        self.height = height
        self.thresh = thresh

        # Patch size for DINOv3 is 16
        self.ph = height // 16
        self.pw = width // 16

        # Store reference features and mask from feats (ptA)
        self.register_buffer("features_a", feats["features"])
        self.register_buffer("mask_a", feats["mask"])

        # Load DINOv3 backbone
        self.backbone = AutoModel.from_pretrained(backbone)

        # Load AnyUp upsampler (Gives better results, but is not ONNX compatible)
        # self.upsampler = torch.hub.load("wimmerth/anyup", "anyup", verbose=False)

        # Set to eval mode
        self.backbone.eval()
        # self.upsampler.eval()

    def find_connected_components(self, mask: torch.Tensor) -> torch.Tensor:
        """Find the two largest connected components in a binary mask using Kornia.

        :param mask: Binary mask (H, W) where True/1 indicates valid pixels

        :return: Binary mask with two largest connected components labeled 1 and 2
        """
        # Use kornia connected components
        labeled = K.contrib.connected_components(mask[None, ...].float(), num_iterations=150)

        # Remove batch dimensions
        labeled = labeled[0, ...]

        labels_flat = labeled.view(-1)

        counts = torch.zeros_like(labels_flat)
        labels_flat_filtered = labels_flat[labels_flat > 0]
        counts.scatter_add_(0, labels_flat_filtered.long() - 1, torch.ones_like(labels_flat_filtered))
        counts = counts.view(labeled.shape)

        # Filter out background (label 0)
        mask_non_zero = labeled > 0
        unique_labels = labeled[mask_non_zero]
        counts = counts[mask_non_zero]

        # Find top 2 largest components using topk
        top_sizes, top_indices = torch.topk(counts, 2)
        top_labels = unique_labels[top_indices]

        # Create result mask using vectorized operations
        result = torch.zeros_like(labeled)

        # First largest component -> label 1
        mask1 = (labeled == top_labels[0]).float()
        result = result + mask1 * 1.0

        # Second largest component -> label 2
        mask2 = (labeled == top_labels[1]).float()
        result = result + mask2 * 2.0

        return result

    def eigh_2x2(self, mat: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
        """Compute eigenvalues and eigenvectors of a 2x2 symmetric matrix.

        Uses closed-form solution for ONNX compatibility.

        :param mat: 2x2 symmetric matrix

        :return: (eigenvalues, eigenvectors) where eigenvalues is (2,) and eigenvectors is (2, 2)
        """
        # Extract elements: mat = [[a, b], [b, d]]
        a = mat[0, 0]
        b = mat[0, 1]
        d = mat[1, 1]

        # Compute trace and determinant
        tr = a + d
        det = a * d - b * b

        # Compute discriminant
        disc = torch.sqrt(torch.clamp(tr * tr - 4 * det, min=0.0))

        # Eigenvalues (in descending order by default)
        lambda1 = (tr + disc) / 2.0
        lambda2 = (tr - disc) / 2.0

        # Eigenvectors using formula: v = [λ - d, b]
        # This is more numerically stable than [b, λ - a]
        v1_unnorm = torch.stack([lambda1 - d, b])
        v2_unnorm = torch.stack([lambda2 - d, b])

        # Normalize eigenvectors
        v1 = v1_unnorm / (torch.norm(v1_unnorm) + 1e-10)
        v2 = v2_unnorm / (torch.norm(v2_unnorm) + 1e-10)

        # Stack as columns (same format as torch.linalg.eigh)
        eigenvalues = torch.stack([lambda1, lambda2])
        eigenvectors = torch.stack([v1, v2], dim=1)

        return eigenvalues, eigenvectors

    def compute_ellipse(self, mask: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor]:
        """Compute principal components (ellipse axes) for a binary mask.

        :param mask: Binary mask (H, W) where 1 indicates valid pixels

        :return: (mean, pc1_scaled, pc2_scaled) where mean is (2,) center [x, y], pc1_scaled and pc2_scaled are (2,) scaled axes
        """
        grid_x = torch.linspace(0, mask.shape[1] - 1, mask.shape[1], device=mask.device)
        grid_y = torch.linspace(0, mask.shape[0] - 1, mask.shape[0], device=mask.device)

        # Create coordinate meshgrid
        grid_y_mesh, grid_x_mesh = torch.meshgrid(grid_y, grid_x, indexing="ij")

        # Extract coordinates where mask == 1
        valid_mask = mask == 1
        x_coords = grid_x_mesh[valid_mask]
        y_coords = grid_y_mesh[valid_mask]

        # Stack into (N, 2) point cloud
        points = torch.stack([x_coords, y_coords], dim=1)

        # Compute mean
        mean = points.mean(dim=0)

        # Center the data
        points_centered = points - mean

        # Compute covariance matrix
        cov = (points_centered.T @ points_centered) / (points_centered.shape[0] - 1)
        print(f"Cov: {cov.shape}")

        # Eigendecomposition
        eigenvalues, eigenvectors = self.eigh_2x2(cov)

        # Sort by eigenvalue (descending: largest variance first)
        idx = torch.argsort(eigenvalues, descending=True)
        eigenvalues = eigenvalues[idx]
        eigenvectors = eigenvectors[:, idx]

        # Extract principal components and variances
        pc1 = eigenvectors[:, 0]
        pc2 = eigenvectors[:, 1]
        var1 = eigenvalues[0]
        var2 = eigenvalues[1]

        # Scale by 2*std for output
        scale = 2.0
        std1 = scale * torch.sqrt(var1)
        std2 = scale * torch.sqrt(var2)

        pc1_scaled = std1 * pc1
        pc2_scaled = std2 * pc2

        return mean, pc1_scaled, pc2_scaled

    def forward(self, pixel_values: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor, torch.Tensor]:
        """Forward pass to detect bongos and compute ellipse parameters.

        :param pixel_values: Preprocessed image tensor from DINOv3 processor (B, C, H, W)

        :return: (kpts, centers, axes1, axes2) where kpts is (6, 2), centers is (2, 2), axes1 and axes2 are (2, 2)
        """
        # Compute DINOv3 features
        dinov3_outputs = self.backbone(pixel_values=pixel_values)
        tokens = dinov3_outputs.last_hidden_state[:, 5:, : self.n_feature_dims]

        # Reshape to feature map
        lr_features = rearrange(tokens, "b (ph pw) d -> b d ph pw", ph=self.ph, pw=self.pw)

        # Upsample features
        # hr_features = self.upsampler(pixel_values, lr_features, q_chunk_size=64) # AnyUp upsampler
        hr_features = F.interpolate(lr_features, size=(self.height, self.width), mode="bilinear", align_corners=False)

        # Extract features for image B
        features_b = hr_features[0]  # (C, H, W)

        # Flatten features from image A (reference)
        mask_a_flat = rearrange(self.mask_a, "h w -> (h w)")
        features_a_flat = rearrange(self.features_a, "c h w -> (h w) c")
        features_a_masked = features_a_flat[mask_a_flat == 1][..., : self.n_feature_dims]
        features_a_masked = features_a_masked[::32, :]  # Subsample for efficiency

        # Flatten features from image B
        features_b_flat = rearrange(features_b, "c h w -> (h w) c")

        # Compute similarity
        sim_a_to_b = features_a_masked @ features_b_flat.T

        # Compute similarity map
        sim_map_b = sim_a_to_b.T.mean(dim=1)
        sim_map_b = rearrange(sim_map_b, "(h w) -> h w", h=self.height, w=self.width)

        # Normalize
        sim_map_b = (sim_map_b - sim_map_b.min()) / (sim_map_b.max() - sim_map_b.min() + 1e-8)

        # Threshold
        sim_map_b[sim_map_b < self.thresh] = 0.0

        # Find connected components
        cc_b = self.find_connected_components(sim_map_b >= self.thresh)

        # Extract the two largest components
        cc1 = cc_b == 1
        cc2 = cc_b == 2

        # Compute ellipses
        mean1, pc1_1, pc2_1 = self.compute_ellipse(cc1)
        mean2, pc1_2, pc2_2 = self.compute_ellipse(cc2)

        # Stack outputs
        centers = torch.stack([mean1, mean2], dim=0)  # (2, 2)
        axes1 = torch.stack([pc1_1, pc1_2], dim=0)  # (2, 2)
        axes2 = torch.stack([pc2_1, pc2_2], dim=0)  # (2, 2)

        angles = torch.tensor([0.0, 120.0, 240.0], device=centers.device)
        angles = torch.deg2rad(angles)

        ct = torch.cos(angles)
        st = torch.sin(-angles)

        kpt1 = centers[0] + ct[0] * axes1[0] + st[0] * axes2[0]
        kpt2 = centers[0] + ct[1] * axes1[0] + st[1] * axes2[0]
        kpt3 = centers[0] + ct[2] * axes1[0] + st[2] * axes2[0]

        kpt4 = centers[1] + ct[0] * axes1[1] + st[0] * axes2[1]
        kpt5 = centers[1] + ct[1] * axes1[1] + st[1] * axes2[1]
        kpt6 = centers[1] + ct[2] * axes1[1] + st[2] * axes2[1]

        kpts = torch.stack([kpt1, kpt2, kpt3, kpt4, kpt5, kpt6], dim=0)

        return kpts, centers, axes1, axes2


def plot_ellipse(
    im: Image.Image,
    kpts: torch.Tensor,
    centers: torch.Tensor,
    axes1: torch.Tensor,
    axes2: torch.Tensor,
    save_path: Path,
) -> None:
    """Plot ellipses on an image.

    :param im: PIL Image or numpy array
    :param kpts: Keypoints tensor
    :param centers: (2, 2) tensor of ellipse centers [x, y]
    :param axes1: (2, 2) tensor of scaled major axes (PC1)
    :param axes2: (2, 2) tensor of scaled minor axes (PC2)
    :param save_path: Path to save the plot
    """
    kpts = kpts.cpu().numpy()
    centers = centers.cpu().numpy()
    axes1 = axes1.cpu().numpy()
    axes2 = axes2.cpu().numpy()

    # Create figure
    fig, ax = plt.subplots(figsize=(10, 8))
    ax.imshow(im)

    colors = ["lime", "cyan"]

    for i in range(2):
        mean = centers[i]
        pc1 = axes1[i]
        pc2 = axes2[i]

        # The axes are already scaled by 2*std
        std1 = np.linalg.norm(pc1)
        std2 = np.linalg.norm(pc2)

        # Handle zero-length axes
        if std1 < 1e-8 or std2 < 1e-8:
            continue

        # Normalize to get directions
        pc1_dir = pc1 / std1
        pc2_dir = pc2 / std2

        # Compute rotation angle of PC1 (in degrees)
        angle = np.degrees(np.arctan2(pc1_dir[1], pc1_dir[0]))

        # Plot ellipse
        ellipse_patch = Ellipse(
            xy=(mean[0], mean[1]),
            width=2 * std1,
            height=2 * std2,
            angle=angle,
            facecolor="none",
            edgecolor=colors[i],
            linewidth=2.5,
            zorder=4,
        )
        ax.add_patch(ellipse_patch)

        # Plot center point
        ax.scatter(mean[0], mean[1], c="red", s=100, marker="o", edgecolors="white", linewidths=2, zorder=5)

        # Plot principal component lines
        ax.plot(
            [mean[0] - pc1_dir[0] * std1, mean[0] + pc1_dir[0] * std1],
            [mean[1] - pc1_dir[1] * std1, mean[1] + pc1_dir[1] * std1],
            "r-",
            linewidth=3,
            zorder=5,
        )

        ax.plot(
            [mean[0] - pc2_dir[0] * std2, mean[0] + pc2_dir[0] * std2],
            [mean[1] - pc2_dir[1] * std2, mean[1] + pc2_dir[1] * std2],
            "b-",
            linewidth=3,
            zorder=5,
        )

    for kpt in kpts:
        ax.scatter(kpt[0], kpt[1], c="yellow", s=80, marker="x", edgecolors="black", linewidths=3, zorder=6)

    ax.axis("off")
    plt.tight_layout()
    plt.savefig(save_path, dpi=150, bbox_inches="tight")
    plt.close()
    print(f"Saved plot to {save_path}")


def test(
    imB: Path,
    ptA: Path,
    width: int = 304,
    height: int = 224,
    thresh: float = 0.75,
    n_feats: int = 384,
    output_dir: Path = Path("./bongo_outputs"),
) -> None:
    """Test the BongoDetector module.

    :param imB: Path to test image
    :param ptA: Path to reference features (.pt file)
    :param width: Image width
    :param height: Image height
    :param thresh: Threshold for similarity map
    :param n_feats: Number of feature dimensions
    :param output_dir: Directory to save outputs
    """
    device = "cuda" if torch.cuda.is_available() else ("mps" if torch.mps.is_available() else "cpu")
    print(f"Using device: {device}")

    # Load reference features
    print(f"Loading reference features from {ptA}")
    feats = torch.load(ptA, map_location=device)

    # Create detector
    print("Creating BongoDetector...")
    detector = BongoDetector(feats, width=width, height=height, thresh=thresh, n_feature_dims=n_feats).to(device)
    detector.eval()

    # Load and preprocess image B
    print(f"Loading test image from {imB}")
    im_b = Image.open(imB).convert("RGB").resize((width, height))

    # Process with DINOv3 processor
    dinov3_path = "facebook/dinov3-vits16-pretrain-lvd1689m"
    processor = AutoProcessor.from_pretrained(dinov3_path)
    inputs = processor(
        images=im_b,
        do_resize=False,
        do_center_crop=False,
        return_tensors="pt",
    )
    pixel_values = inputs["pixel_values"].to(device)

    # Run detection
    print("Running detection...")
    with torch.no_grad():
        outputs = detector(pixel_values)

    # Plot results
    output_dir.mkdir(parents=True, exist_ok=True)

    plot_ellipse(
        im_b,
        outputs[0],
        outputs[1],
        outputs[2],
        outputs[3],
        save_path=output_dir / "ellipses.png",
    )

    print(f"\nResults:")
    print(f"Kpts:\n{outputs[0]}")
    print(f"Centers:\n{outputs[1]}")
    print(f"\nAxes 1 (major):\n{outputs[2]}")
    print(f"Axes 2 (minor):\n{outputs[3]}")

    print(f"\nResults saved to {output_dir / 'ellipses.png'}")


def entrypoint() -> None:
    """Main entrypoint for test script."""
    tyro.cli(test)


if __name__ == "__main__":
    entrypoint()
