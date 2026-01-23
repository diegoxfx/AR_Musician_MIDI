from pathlib import Path
from typing import Self

import mitsuba as mi
import numpy as np

from src.camera import Camera
from src.config import SceneConfig
from src.piano import Piano


class Scene:
    """
    Scene utility class.
    """

    def __init__(self, cfg: SceneConfig, camera: Camera, piano: Piano, env_map: Path):
        """
        :param cfg: Scene config
        :param camera: Camera instance
        :param piano: Piano instance
        :param env_map: Path to the environment map
        """

        self.cfg = cfg
        self.camera = camera
        self.piano = piano
        self.env_map = env_map

    @classmethod
    def random(cls, cfg: SceneConfig) -> Self:
        camera = Camera.random(cfg.camera)
        piano = Piano.random(cfg.piano)

        env_map_idx = np.random.randint(0, len(cfg.env_maps))
        env_map = cfg.env_maps[env_map_idx]

        return cls(cfg=cfg, camera=camera, piano=piano, env_map=env_map)

    def to_mitsuba(self, temp_path: Path) -> mi.Scene:
        """
        Converts the Scene instance to a Mitsuba Scene.

        :param temp_path: Path to the temporary directory for storing intermediate files.

        :return: Mitsuba Scene object
        """

        sensor = self.camera.to_mitsuba(self.cfg)
        white_mesh, black_mesh = self.piano.to_mitsuba(temp_path)

        scene_dict = {
            "type": "scene",
            "emitter": {"type": "envmap", "filename": str(self.env_map.resolve()), "scale": 1.0},
            "sensor": sensor,
            "integrator": {"type": "path"},
            "white_keys": white_mesh,
            "black_keys": black_mesh,
        }

        return mi.load_dict(scene_dict)
