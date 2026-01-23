from pathlib import Path
from functools import partial

import torch
from torch import Tensor
from torch.utils.data import Dataset
from transformers import AutoImageProcessor
import datasets
import numpy as np
from PIL import Image


class SynthesiaDataset(Dataset):
    """
    Dataset for Synthesia images and keypoints.
    """

    def __init__(self, data_dir: Path, processor: AutoImageProcessor):
        """
        :param data_dir: Path to the directory containing images and keypoints
        :param processor: Image processor given by the backbone
        """

        self.processor = processor

        self.images = list(sorted(data_dir.glob("*.png")))
        self.keypoints = list(sorted(data_dir.glob("*.npy")))

    def __len__(self) -> int:
        return len(self.images)

    def __getitem__(self, idx: int) -> tuple[Tensor, Tensor]:
        """
        :param idx: Index of the sample

        :return: Tuple of (image, keypoints)
            - image: Tensor of shape (3, H, W)
            - keypoints: Tensor of shape (N, 2)
        """

        image = Image.open(self.images[idx]).convert("RGB")
        keypoints = np.load(self.keypoints[idx], allow_pickle=True).item()["keypoints2d"]

        image = self.processor(images=image, do_resize=False, return_tensors="pt")["pixel_values"].squeeze(0)
        keypoints = torch.from_numpy(keypoints)

        return image.to(torch.float32), keypoints.to(torch.float32)


def process_hf_item(item: dict, processor: AutoImageProcessor) -> dict[str, Tensor]:
    """Process a single item from the Hugging Face dataset.

    :param item: Item from the dataset
    :param processor: Image processor given by the backbone

    :return: Processed item with image and keypoints as tensors
    """

    image = item["image"].convert("RGB")
    keypoints = torch.tensor(item["kpts"]["keypoints2d"])

    image = processor(images=image, do_resize=False, return_tensors="pt")["pixel_values"].squeeze(0)

    item["image"] = image.to(torch.float32)
    item["keypoints2d"] = keypoints.to(torch.float32)
    del item["kpts"]

    return item


def load_dataset_from_hf(processor: AutoImageProcessor, stream: bool = True) -> datasets.IterableDataset:
    """Load the Synthesia dataset from Hugging Face.

    :param processor: Image processor given by the backbone
    :param stream: Whether to stream the dataset instead of downloading

    :return: IterableDataset with processed images and keypoints
    """

    dataset = datasets.load_dataset("gserifi/SynthesiaSet", split="train", streaming=stream)
    dataset = dataset.map(partial(process_hf_item, processor=processor))

    return dataset


if __name__ == "__main__":
    backbone = "facebook/dinov3-vits16-pretrain-lvd1689m"
    dataset = SynthesiaDataset(
        data_dir=Path.cwd().parent / "outputs",
        processor=AutoImageProcessor.from_pretrained(backbone),
    )

    image, keypoints = dataset[0]
    print(f"Image shape: {image.shape}")
    print(f"Keypoints shape: {keypoints.shape}")
