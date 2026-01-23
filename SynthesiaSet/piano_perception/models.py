from typing import Literal, Type

import torch
from torch import Tensor
import torch.nn as nn
from transformers import AutoModel
from einops import rearrange

from piano_perception.utils import heatmaps_to_keypoints


class Detector(nn.Module):
    """
    Base class for keypoint detectors using a DINOv3 backbone.
    """

    def __init__(
        self,
        n_keypoints: int = 4,
        backbone: str = "facebook/dinov3-vits16-pretrain-lvd1689m",
    ):
        """
        :param n_keypoints: Number of keypoints to detect
        :param backbone: Pretrained DINOv3 backbone model name
        """

        super().__init__()

        self.backbone = AutoModel.from_pretrained(backbone)
        self.hidden_dim: int = self.backbone.config.hidden_size
        self.n_registers: int = self.backbone.config.num_register_tokens
        self.patch_size: int = self.backbone.config.patch_size
        self.n_keypoints = n_keypoints

    def forward_backbone(self, x: Tensor) -> Tensor:
        """
        Extract features from the backbone.

        :param x: (b, c, h, w) Input images

        :return: (b, hidden_dim, ph, pw) Extracted features
        """

        b, c, h, w = x.shape
        ph, pw = h // self.patch_size, w // self.patch_size

        # Get DINOv3 features
        outputs = self.backbone(x)
        features = outputs.last_hidden_state[:, 1 + self.n_registers :, :]  # Exclude [CLS] + Register tokens
        features = rearrange(features, "b (ph pw) c -> b c ph pw", ph=ph, pw=pw)

        return features

    def forward_heatmaps(self, features: Tensor) -> Tensor:
        """
        Generate heatmaps from extracted features.

        :param features: (b, hidden_dim, ph, pw) Extracted features

        :return: (b, n_keypoints, h, w) Per-keypoint heatmaps
        """

        raise NotImplementedError("Subclasses must implement forward_heatmaps method.")

    def forward(self, x: Tensor) -> tuple[Tensor, Tensor]:
        """
        Forward pass through the detector.

        :param x: (b, c, h, w) Input images

        :return: (heatmaps, kpts)
            - heatmaps: (b, n_keypoints, h, w) Per-keypoint heatmaps (normalized to [0, 1])
            - kpts: (b, n_keypoints, 2) Keypoint coordinates
        """

        features = self.forward_backbone(x)

        heatmaps = self.forward_heatmaps(features)
        heatmaps = torch.sigmoid(heatmaps)

        kpts = heatmaps_to_keypoints(heatmaps)

        return heatmaps, kpts


class ConvTransposeDetector(Detector):
    """
    Keypoint detector using transposed convolutions for upsampling.
    """

    def __init__(
        self,
        n_keypoints: int = 4,
        backbone: str = "facebook/dinov3-vits16-pretrain-lvd1689m",
    ):
        """
        :param n_keypoints: Number of keypoints to detect
        :param backbone: Pretrained DINOv3 backbone model name
        """
        super().__init__(
            n_keypoints=n_keypoints,
            backbone=backbone,
        )

        upsample_layers = []
        for i, j in zip([1, 2, 4, 8], [2, 4, 8, 16]):
            c_in = self.hidden_dim // i
            c_out = self.hidden_dim // j

            # C -> C / 2, H -> 2H, W -> 2W
            upsample_layers.append(
                nn.Sequential(
                    nn.ConvTranspose2d(c_in, c_out, kernel_size=2, stride=2),
                    nn.BatchNorm2d(c_out),
                    nn.ReLU(inplace=True),
                )
            )

        self.upsample = nn.Sequential(*upsample_layers)

        self.heatmap_head = nn.Conv2d(self.hidden_dim // 16, self.n_keypoints, kernel_size=1)

    def forward_heatmaps(self, features: Tensor) -> Tensor:
        """
        Generate heatmaps from extracted features using transposed convolutions.

        :param features: (b, hidden_dim, ph, pw) Extracted features

        :return: (b, n_keypoints, h, w) Per-keypoint heatmaps
        """
        features_upsampled = self.upsample(features)
        heatmaps = self.heatmap_head(features_upsampled)  # (b, n_keypoints, h, w)

        return heatmaps


class ConvShuffleDetector(Detector):
    """
    Keypoint detector using sub-pixel convolution for upsampling.
    """

    def __init__(
        self,
        n_keypoints: int = 4,
        backbone: str = "facebook/dinov3-vits16-pretrain-lvd1689m",
    ):
        """
        :param n_keypoints: Number of keypoints to detect
        :param backbone: Pretrained DINOv3 backbone model name
        """
        super().__init__(
            n_keypoints=n_keypoints,
            backbone=backbone,
        )

        upsample_layers = []

        for i, j in zip([1, 2, 4, 8], [2, 4, 8, 16]):
            c_in = self.hidden_dim // i
            c_out = self.hidden_dim // j

            # C -> C / 2, H -> 2H, W -> 2W
            upsample_layers.append(
                nn.Sequential(
                    nn.Conv2d(c_in, c_out * 4, 3, padding=1),
                    nn.BatchNorm2d(c_out * 4),
                    nn.ReLU(inplace=True),
                    nn.PixelShuffle(2),
                )
            )

        self.upsample = nn.Sequential(*upsample_layers)

        self.heatmap_head = nn.Conv2d(self.hidden_dim // 16, self.n_keypoints, kernel_size=1)

    def forward_heatmaps(self, features: Tensor) -> Tensor:
        """
        Generate heatmaps from extracted features using sub-pixel convolution.

        :param features: (b, hidden_dim, ph, pw) Extracted features

        :return: (b, n_keypoints, h, w) Per-keypoint heatmaps
        """
        features_upsampled = self.upsample(features)
        heatmaps = self.heatmap_head(features_upsampled)  # (b, n_keypoints, h, w)

        return heatmaps


ModelType = Literal["conv_transpose", "conv_shuffle"]

model_registry: dict[ModelType, tuple[str, Type[Detector]]] = {
    "conv_transpose": ("ConvTranspose", ConvTransposeDetector),
    "conv_shuffle": ("ConvShuffle", ConvShuffleDetector),
}
