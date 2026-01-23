from dataclasses import dataclass, field
from pathlib import Path
from typing import Self

from piano_perception.models import ModelType, model_registry


@dataclass
class TrainConfig:
    # General
    experiment: str | None = None
    accelerator: str = "auto"
    devices: int = 1
    allow_tf32: bool = True
    batch_size: int = 128
    n_epochs: int = 100
    seed: int = 0
    detect_anomaly: bool = False

    # Data
    data_dir: Path | None = None  # If None, use Hugging Face SynthesiaSet
    stream_data: bool = True  # Stream Hugging Face dataset instead of downloading to disk
    val_split: float = 0.1
    input_size: tuple[int, int] = (224, 304)
    n_keypoints: int = 4
    heatmap_sigma: tuple[float, float] = field(default_factory=lambda: (6.0, 1.0))  # linear decay schedule
    n_workers: int = 8
    pin_mem: bool = True

    # Model and Optimization
    model: ModelType = "conv_shuffle"
    backbone: str = "facebook/dinov3-vits16-pretrain-lvd1689m"
    freeze_backbone: bool = True
    lr: float = 1e-3

    # Checkpointing and Logging
    output_path: Path = Path("piano_outputs")
    save_every_n_epochs: int = 5
    ckpt: Path | None = None
    val_every_n_epochs: int = 1
    n_val_vis: int = 6
    param_hist_every_n_steps: int = 100

    def __post_init__(self) -> None:
        """Initialize output directory after dataclass initialization."""
        if self.experiment is None:
            self.experiment = self.model
        self.output_dir = self.output_path / self.experiment

    @classmethod
    def from_dict(cls, cfg_dict: dict) -> Self:
        """Create config from dictionary, converting path strings to Path objects.

        :param cfg_dict: Configuration dictionary

        :return: TrainConfig instance
        """
        for k in cfg_dict.keys():
            if k in ["data_dir", "output_path", "ckpt"] and cfg_dict[k] is not None:
                cfg_dict[k] = Path(cfg_dict[k])

        return cls(**cfg_dict)


train_configs = {}

for detector_type in model_registry.keys():
    for dino_type in ["vits16", "vits16plus", "vitb16"]:
        experiment = f"{detector_type}_{dino_type}"
        train_configs[experiment] = (
            f"{model_registry[detector_type][0]} {dino_type}",
            TrainConfig(
                experiment=experiment, model=detector_type, backbone=f"facebook/dinov3-{dino_type}-pretrain-lvd1689m"
            ),
        )
