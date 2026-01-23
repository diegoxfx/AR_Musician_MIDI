from dataclasses import dataclass, field
from pathlib import Path
from typing import Literal

from src.piano_types import PianoType


@dataclass
class CameraConfig:
    width: int = 304
    """Image width in pixels."""

    height: int = 224
    """Image height in pixels."""

    quality_preset: Literal["ViT", "low", "medium", "high"] | None = None
    """Can be used instead of width and height. ViT: 304x224, low: 640x480, medium: 1920x1440, high: 3840x2880."""

    fov_range: tuple[float, float] = field(default_factory=lambda: (30, 60))
    """Field of view range in degrees."""

    distance_range: tuple[float, float] = field(default_factory=lambda: (1.5, 2.5))
    """Distance range from the origin."""

    azimuth_range: tuple[float, float] = field(default_factory=lambda: (-20, 20))
    """Azimuth angle range in degrees."""

    elevation_range: tuple[float, float] = field(default_factory=lambda: (20, 60))
    """Elevation angle range in degrees."""

    def __post_init__(self):
        if self.quality_preset is not None:
            self.width, self.height = {
                "ViT": (304, 224),
                "low": (640, 480),
                "medium": (1920, 1440),
                "high": (3840, 2880),
            }[self.quality_preset]


@dataclass
class PianoConfig:
    piano_types: list[PianoType] = field(default_factory=lambda: [*PianoType])
    """List of piano types to randomly choose from."""

    white_length_total_range: tuple[float, float] = field(default_factory=lambda: (6.0, 12.0))
    """Range for total length of white keys."""

    flat_keyboard_prob: float = 0.5
    """Probability of generating a flat keyboard."""

    white_height_range: tuple[float, float] = field(default_factory=lambda: (2.0, 3.0))
    """Range for height of white keys (used if not flat keyboard)."""

    white_height_range_flat: tuple[float, float] = field(default_factory=lambda: (0.3, 0.7))
    """Range for height of white keys (used if flat keyboard)."""

    white_thick_bottom_range: tuple[float, float] = field(default_factory=lambda: (1.8, 2.2))
    """Range for thickness of white keys at the bottom."""

    white_thin_bottom_offset_range: tuple[float, float] = field(default_factory=lambda: (0.0, 0.2))
    """Range for offset to determine thin bottom thickness of white keys."""

    black_length_ratio_range: tuple[float, float] = field(default_factory=lambda: (0.5, 0.7))
    """Range for ratio of black key length to white key length."""

    black_height_offset_range: tuple[float, float] = field(default_factory=lambda: (0.1, 0.7))
    """Range for offset to determine height of black keys."""

    black_width_offset_range: tuple[float, float] = field(default_factory=lambda: (0.0, 0.4))
    """Range for offset to determine width of black keys."""

    space_range: tuple[float, float] = field(default_factory=lambda: (0.05, 0.2))
    """Range for space between keys."""

    group_3_ratio_range: tuple[float, float] = field(default_factory=lambda: (0.3, 0.7))
    """Range for ratio in group of 3 keys."""

    group_4_ratio_range: tuple[float, float] = field(default_factory=lambda: (0.3, 0.7))
    """Range for ratio in group of 4 keys."""


@dataclass
class SceneConfig:
    camera: CameraConfig = field(default_factory=CameraConfig)
    """Camera configuration."""

    piano: PianoConfig = field(default_factory=PianoConfig)
    """Piano configuration."""

    # Rendering parameters (See Mitsuba docs)

    film: str = "hdrfilm"
    """Type of film to use in Mitsuba."""

    rfilter: str = "gaussian"
    """Type of reconstruction filter to use in Mitsuba."""

    sampler: str = "independent"
    """Type of sampler to use in Mitsuba."""

    sample_count: int = 64
    """Number of samples to use in Mitsuba."""

    env_map_path: Path = Path("envmaps")
    """Path to the directory containing environment maps (as EXR files)."""

    env_maps: list[Path] | None = None
    """List of environment map file paths (as EXR files). If None, all files in env_map_path will be used."""

    def __post_init__(self):
        if self.env_maps is None:
            self.env_maps = [p for p in self.env_map_path.glob("*.exr")]


@dataclass
class SynthesiaGeneration:
    n_samples: int = 1
    """Number of samples to generate."""

    output_dir: Path = Path("outputs")
    """Output directory for generated samples."""

    scene: SceneConfig = field(default_factory=SceneConfig)
    """Scene configuration."""

    n_jobs: int = 1
    """Number of parallel jobs to use for generation."""

    seed: int = 0
    """Random seed for reproducibility."""

    def __post_init__(self):
        self.output_dir.mkdir(exist_ok=True, parents=True)


@dataclass
class SynthesiaVisualization:
    idx: int = 1
    """Sample index to visualize."""

    output_dir: Path = Path("outputs")
    """Output directory for generated samples."""


configs = {
    "gen": ("Generate Synthesia dataset", SynthesiaGeneration()),
    "vis": ("Visualize a sample from the Synthesia dataset", SynthesiaVisualization()),
}
