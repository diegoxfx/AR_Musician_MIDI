import torch
from torch import Tensor
from einops import rearrange, repeat


def keypoints_to_heatmaps(
    keypoints: Tensor,
    heatmap_size: tuple[int, int],
    sigma: float = 2.0,
) -> Tensor:
    """
    Convert keypoints to Gaussian heatmaps

    :param keypoints: (b, n, 2) Keypoint coordinates as (x, y)
    :param heatmap_size: (h, w) Desired heatmap spatial dimensions
    :param sigma: Standard deviation of Gaussian (controls spread)

    :return: (b, n, h, w) Gaussian heatmaps centered at keypoints
    """

    b, n, _ = keypoints.shape
    h, w = heatmap_size
    dd = dict(device=keypoints.device, dtype=keypoints.dtype)

    # Create coordinate grids
    x_coords = torch.arange(w, **dd)
    y_coords = torch.arange(h, **dd)
    y_grid, x_grid = torch.meshgrid(y_coords, x_coords, indexing="ij")

    # Expand for batch and keypoints
    x_grid = repeat(x_grid, "h w -> b n h w", b=b, n=n)
    y_grid = repeat(y_grid, "h w -> b n h w", b=b, n=n)

    # Extract keypoint coordinates and expand
    kpt_x = rearrange(keypoints[:, :, 0], "b n -> b n 1 1")
    kpt_y = rearrange(keypoints[:, :, 1], "b n -> b n 1 1")

    # Compute Gaussian heatmaps (unnormalized, peak=1.0)
    heatmaps = torch.exp(-((x_grid - kpt_x) ** 2 + (y_grid - kpt_y) ** 2) / (2 * sigma**2))

    return heatmaps


def heatmaps_to_keypoints(heatmaps: Tensor) -> Tensor:
    """
    Convert heatmaps to keypoints and confidence scores

    :param heatmaps: (b, n, h, w) Unnormalized heatmaps for each keypoint

    :return: (b, n, 3) Keypoints packed as (x, y, confidence)
    """

    b, n, h, w = heatmaps.shape
    dd = dict(device=heatmaps.device, dtype=heatmaps.dtype)

    heatmaps_flat = rearrange(heatmaps, "b n h w -> b n (h w)")

    # Confidence is the maximum value in each heatmap
    confidences = heatmaps_flat.max(dim=-1).values

    # Normalize for computing expected value (soft-argmax)
    heatmaps_norm = heatmaps_flat / (heatmaps_flat.sum(dim=-1, keepdim=True) + 1e-10)

    # Create coordinate grids [0, W-1] and [0, H-1]
    x_coords = torch.arange(w, **dd)
    y_coords = torch.arange(h, **dd)
    y_grid, x_grid = torch.meshgrid(y_coords, x_coords, indexing="ij")

    # Flatten and expand for batch and keypoints
    x_grid = repeat(x_grid, "h w -> b n (h w)", b=b, n=n)
    y_grid = repeat(y_grid, "h w -> b n (h w)", b=b, n=n)

    # Expected value under normalized probability distribution (soft-argmax)
    x_pred = (heatmaps_norm * x_grid).sum(dim=-1)
    y_pred = (heatmaps_norm * y_grid).sum(dim=-1)

    max_idx = heatmaps_flat.argmax(dim=-1)
    y_pred_max = max_idx // w
    x_pred_max = max_idx % w

    x_pred = x_pred_max.float()
    y_pred = y_pred_max.float()

    # Stack into (b, n, 3)
    keypoints = torch.stack([x_pred, y_pred, confidences], dim=-1)

    return keypoints
