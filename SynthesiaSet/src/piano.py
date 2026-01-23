from pathlib import Path
from typing import Self

import mitsuba as mi
import numpy as np
from trimesh import Trimesh

from src.config import PianoConfig
from src.key_group import KeyGroup
from src.piano_types import PianoType
from src.piano_utils import PianoMeasure


class Piano:
    """
    Piano utility class.
    """

    def __init__(self, measure: PianoMeasure, piano_type: PianoType):
        """
        :param measure: Piano measurements
        :param piano_type: Piano type
        """

        self.measure = measure
        self.piano_type = piano_type

        # Note: keypoints are computed during mesh generation
        self._keypoints3d = None

    @classmethod
    def random(cls, cfg: PianoConfig) -> Self:
        """
        Generates a random Piano instance based on the given piano type.

        :param cfg: Piano config specifying the random value ranges

        :return: A Piano instance with random configuration
        """

        measure = PianoMeasure(cfg)

        piano_type_idx = np.random.randint(0, len(cfg.piano_types))
        piano_type = cfg.piano_types[piano_type_idx]

        return cls(measure, piano_type)

    @property
    def keypoints3d(self) -> np.ndarray:
        """
        :return: (N, 3) 3D keypoints
        """

        if self._keypoints3d is None:
            self._to_meshes()

        return self._keypoints3d

    def _to_meshes(self) -> tuple[Trimesh, Trimesh]:
        """
        Converts the piano configuration into 3D meshes for white and black keys.

        :return: (white_mesh, black_mesh) Tuple of Trimesh objects for white and black keys
        """

        groups = self.piano_type.to_key_groups()

        merged = KeyGroup.merge_all(list(map(lambda g: KeyGroup.materialize(g, self.measure), groups)))
        white_mesh, black_mesh = merged.get_trimesh()

        # scale uniformly
        white_mesh.apply_scale(1.0 / merged.width)
        black_mesh.apply_scale(1.0 / merged.width)

        self._extract_keypoints3d(white_mesh)

        return white_mesh, black_mesh

    def _extract_keypoints3d(self, mesh: Trimesh):
        min_x, _, min_z = mesh.vertices.min(axis=0)
        max_x, max_y, max_z = mesh.vertices.max(axis=0)

        self._keypoints3d = np.array(
            [
                [min_x, max_y, min_z],
                [max_x, max_y, min_z],
                [max_x, max_y, max_z],
                [min_x, max_y, max_z],
            ]
        )

    def to_mitsuba(self, temp_path: Path) -> tuple[mi.Shape, mi.Shape]:
        """
        Converts the piano meshes to Mitsuba shapes and saves them as temporary PLY files.

        :param temp_path: Path to the temporary directory for storing PLY files.

        :return: (white_keys, black_keys) Tuple of Mitsuba shapes for white and black keys
        """

        white_mesh, black_mesh = self._to_meshes()

        white_path = temp_path / "white.obj"
        black_path = temp_path / "black.obj"

        white_mesh.export(white_path)
        black_mesh.export(black_path)

        white_bsdf = {
            "type": "plastic",
            "diffuse_reflectance": {"type": "rgb", "value": [1.0, 1.0, 1.0]},
            "specular_reflectance": {"type": "rgb", "value": [0.4, 0.4, 0.4]},
        }
        black_bsdf = {
            "type": "plastic",
            "diffuse_reflectance": {"type": "rgb", "value": [0.05, 0.05, 0.05]},
            "specular_reflectance": {"type": "rgb", "value": [0.1, 0.1, 0.1]},
        }

        white_keys = mi.load_dict(
            {
                "type": "obj",
                "filename": str(white_path.resolve()),
                "bsdf": white_bsdf,
            }
        )

        black_keys = mi.load_dict(
            {
                "type": "obj",
                "filename": str(black_path.resolve()),
                "bsdf": black_bsdf,
            }
        )

        return white_keys, black_keys
