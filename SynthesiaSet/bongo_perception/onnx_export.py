from pathlib import Path

import tyro
import torch
import numpy as np
import onnxruntime as ort
from transformers import AutoProcessor
from PIL import Image

from bongo_perception.detector import BongoDetector


def export(
    imB: Path,
    ptA: Path,
    backbone: str = "facebook/dinov3-vits16-pretrain-lvd1689m",
    width: int = 304,
    height: int = 224,
    n_feats: int = 384,
    export_dir: Path = Path("./bongo_exports"),
    batch_size: int = 1,
    opset: int = 18,
) -> Path:
    """Export BongoDetector model to ONNX format.

    :param imB: Path to test image
    :param ptA: Path to reference features (.pt file)
    :param backbone: DINOv3 backbone model path
    :param width: Image width
    :param height: Image height
    :param n_feats: Number of feature dimensions
    :param export_dir: Directory to save exported model
    :param batch_size: Batch size for dummy input
    :param opset: ONNX opset version

    :return: Path to exported ONNX model
    """
    device = "cuda" if torch.cuda.is_available() else "cpu"
    print(f"Using device: {device}")

    # Load reference features
    print(f"Loading reference features from {ptA}")
    feats = torch.load(ptA, map_location=device)

    # Create detector
    print("Creating BongoDetector...")
    model = BongoDetector(feats, backbone=backbone, width=width, height=height, n_feature_dims=n_feats).to(device)
    model.eval()

    # Dummy input (preprocessed image from DINOv3 processor)
    # DINOv3 processor outputs normalized images with shape (B, 3, H, W)
    print(f"Creating dummy input with shape ({batch_size}, 3, {height}, {width})")
    im_b = Image.open(imB).convert("RGB").resize((width, height))
    processor = AutoProcessor.from_pretrained(backbone)
    inputs = processor(
        images=im_b,
        do_resize=False,
        do_center_crop=False,
        return_tensors="pt",
    )
    example_input = inputs["pixel_values"].to(device)

    # Test forward pass
    print("Testing forward pass...")
    with torch.no_grad():
        outputs = model(example_input)
    print(f"Centers shape: {outputs[0].shape}")
    print(f"Axes1 shape: {outputs[1].shape}")
    print(f"Axes2 shape: {outputs[2].shape}")

    # Export to ONNX
    export_dir.mkdir(parents=True, exist_ok=True)
    output_path = export_dir / f"bongo_detector_{height}x{width}_{n_feats}d_bilinear_kpts_merged.onnx"

    print(f"\nExporting to ONNX: {output_path}")

    torch.onnx.export(
        model,
        (example_input,),
        output_path,
        input_names=["pixel_values"],
        output_names=["kpts", "centers", "axes1", "axes2"],
        dynamic_axes={
            "pixel_values": {0: "batch"},
            "kpts": {0: "num_bongos"},
            "centers": {0: "num_bongos"},
            "axes1": {0: "num_bongos"},
            "axes2": {0: "num_bongos"},
        },
        opset_version=opset,
        do_constant_folding=True,
        export_params=True,
        external_data=False,
    )

    print(f"✓ Model exported successfully to {output_path}")
    print(f"  File size: {output_path.stat().st_size / 1024 / 1024:.2f} MB")

    # Test exported model
    print("\n" + "=" * 60)
    print("Testing ONNX model...")
    print("=" * 60)

    # Load ONNX model
    session = ort.InferenceSession(
        str(output_path),
        providers=["CUDAExecutionProvider", "CPUExecutionProvider"],
    )

    print(f"ONNX Runtime providers: {session.get_providers()}")

    # Test inference with different batch size
    test_batch_size = 1
    print(f"\nTesting with batch size: {test_batch_size}")
    test_input = example_input

    # ONNX inference
    onnx_outputs = session.run(None, {"pixel_values": test_input.cpu().numpy()})

    print("\nONNX outputs:")
    for i, (name, output) in enumerate(zip(["centers", "axes1", "axes2"], onnx_outputs)):
        print(f"  {name}: shape={output.shape}, dtype={output.dtype}")

    # Compare with PyTorch
    print("\nComparing ONNX vs PyTorch outputs...")
    with torch.no_grad():
        pytorch_outputs = model(test_input.to(device))

    # Convert PyTorch outputs to numpy
    pytorch_outputs_np = [o.cpu().numpy() for o in pytorch_outputs]

    # Compute differences
    differences = []
    for i, name in enumerate(["kpts", "centers", "axes1", "axes2"]):
        diff = np.abs(pytorch_outputs_np[i] - onnx_outputs[i]).max()
        mse = ((pytorch_outputs_np[i] - onnx_outputs[i]) ** 2).mean()
        differences.append((name, diff, mse))
        print(f"  {name}:")
        print(f"    Max absolute difference: {diff:.6e}")
        print(f"    Mean squared error: {mse:.6e}")

    # Check if differences are acceptable
    max_diff = max(d[2] for d in differences)
    if max_diff < 1e-4:
        print("\n✓ ONNX export validated successfully! Differences are negligible.")
    elif max_diff < 1e-2:
        print(f"\n⚠ ONNX export completed with small differences (max: {max_diff:.6e})")
        print("  This is usually acceptable for most applications.")
    else:
        print(f"\n⚠ WARNING: Large differences detected (max: {max_diff:.6e})")
        print("  Please verify the exported model carefully.")

    print(f"\n{'='*60}")
    print(f"Export complete! Model saved to: {output_path}")
    print(f"{'='*60}")

    return output_path


def entrypoint() -> None:
    """Main entrypoint for export script."""
    tyro.cli(export)


if __name__ == "__main__":
    entrypoint()
