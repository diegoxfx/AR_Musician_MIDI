from typing import Literal
from pathlib import Path
import json

import torch
from torch.export import Dim
from transformers import AutoModel
import numpy as np
import onnxruntime as ort
import tyro

from piano_perception.train import Trainer, TrainConfig

ModelType = Literal[
    "vits16",
    "vits16plus",
    "vitb16",
    "vitl16",
]


def export(ckpt: Path, export_dir: Path = Path("./piano_exports")) -> None:
    """Export model to ONNX format.

    :param ckpt: Path to model checkpoint
    :param export_dir: Directory to save exported model
    """
    # Load model and processor
    cfg_path = next(ckpt.parent.parent.glob("config*.json"))
    cfg_dict = json.load(open(cfg_path, "r"))
    cfg = TrainConfig.from_dict(cfg_dict)
    cfg.accelerator = "cpu"
    cfg.ckpt = ckpt

    trainer = Trainer(cfg, Path.cwd(), Path.cwd(), None)
    model = trainer.model.module
    model.eval()

    # Dummy input
    b = 2
    example_input = torch.randn(b, 3, 240, 320)

    # Export to ONNX
    export_dir.mkdir(parents=True, exist_ok=True)
    export_path = export_dir / f"{cfg.experiment}_240x320_merged.onnx"
    torch.onnx.export(
        model,
        (example_input,),
        export_path,
        input_names=["pixel_values"],
        output_names=["heatmaps", "kpts"],
        dynamic_axes={
            "pixel_values": {0: "batch"},
            "heatmaps": {0: "batch"},
            "kpts": {0: "batch"},
        },
        opset_version=18,
        do_constant_folding=True,
        external_data=False,
    )

    # Test exported model

    # Load ONNX model
    session = ort.InferenceSession(
        export_path,
        providers=["CUDAExecutionProvider", "CPUExecutionProvider"],
    )

    # Test inference
    example_input = torch.randn(4, 3, 240, 320)
    onnx_outputs = session.run(None, {"pixel_values": example_input.numpy()})

    for i, output in enumerate(onnx_outputs):
        print(f"ONNX output {i}:", output.shape)

    # Compare with PyTorch
    with torch.no_grad():
        pytorch_outputs = model(example_input)

    print(
        "Difference:",
        ((pytorch_outputs[0].numpy() - onnx_outputs[0]) ** 2).mean(),
    )


def entrypoint() -> None:
    """Main entrypoint for ONNX export script."""
    tyro.cli(export)


if __name__ == "__main__":
    entrypoint()
