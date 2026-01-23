from typing import Self

import mitsuba as mi
import numpy as np

from src.config import CameraConfig, SceneConfig
from src.utils import make_homo, make_nohomo


class Camera:
    """
    Camera utility class.
    """

    def __init__(
        self,
        to_world: mi.ScalarTransform4f,
        width: int,
        height: int,
        fov: float,
    ):
        """
        :param to_world: Camera to world transformation
        :param width: Image width
        :param height: Image height
        :param fov: Field of view in degrees
        """

        self.to_world = to_world
        self.width = width
        self.height = height
        self.fov = fov

        self.P = self._get_full_projection()

    @classmethod
    def random(cls, cfg: CameraConfig) -> Self:
        """
        Generates a random camera with uniform parameters within the given ranges.

        :param cfg: Camera config specifying the random value ranges

        :return: A Camera instance with randomly sampled parameters
        """

        fov = np.random.uniform(*cfg.fov_range)
        distance = np.random.uniform(*cfg.distance_range)
        azimuth = np.random.uniform(*cfg.azimuth_range)
        elevation = np.random.uniform(*cfg.elevation_range)

        az, el = np.radians(azimuth), np.radians(elevation)
        x = distance * np.sin(az) * np.cos(el)
        y = distance * np.sin(el)
        z = distance * np.cos(az) * np.cos(el)

        origin = mi.ScalarPoint3f([float(x), float(y), float(z)])
        target = mi.ScalarPoint3f([0, 0, 0])
        up = mi.ScalarVector3f([0, 1, 0])

        to_world = mi.ScalarTransform4f.look_at(origin=origin, target=target, up=up)

        return cls(to_world=to_world, width=cfg.width, height=cfg.height, fov=fov)

    def _get_full_projection(self) -> np.ndarray:
        """
        :return: (4, 4) Full projection matrix
        """

        # Extrinsic Matrix, world -> cam
        RT = np.linalg.inv(np.array(self.to_world.matrix))

        # Intrinsic Matrix,
        # Mitsuba uses the x-axis for the fov (by default) and has a flipped sign
        K = np.eye(4)

        fx = -self.width / (2 * np.tan(np.radians(self.fov) / 2))
        fy = fx
        cx = self.width / 2
        cy = self.height / 2

        K[0, 0] = fx
        K[1, 1] = fy
        K[0, 2] = cx
        K[1, 2] = cy

        return np.matmul(K, RT)

    def _project_points(self, x: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
        """
        Projects 3D points to 2D pixel coordinates.

        :param x: (N, 3) Array of 3D points

        :return: ((N, 2), (N,)) Tuple of 2D pixel coordinates and depth values
        """

        x_view = make_nohomo(np.einsum("ij,bj->bi", self.P, make_homo(x)))
        depth = x_view[:, -1]

        x_screen = make_nohomo(x_view)

        u = x_screen[:, 0]
        v = x_screen[:, 1]

        uv = np.stack([u, v], axis=-1)

        return uv, depth

    def to_mitsuba(self, cfg: SceneConfig) -> mi.Sensor:
        """
        Converts the Camera instance to a Mitsuba Sensor. See Mitsuba's documentation for details on the arguments.

        :param cfg: Scene config

        :return: Mitsuba Sensor object
        """

        return mi.load_dict(
            {
                "type": "perspective",
                "to_world": self.to_world,
                "fov": self.fov,
                "film": {
                    "type": cfg.film,
                    "width": self.width,
                    "height": self.height,
                    "rfilter": {"type": cfg.rfilter},
                },
                "sampler": {"type": cfg.sampler, "sample_count": cfg.sample_count},
            }
        )

    def __call__(self, x: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
        # Syntactic sugar for projecting points
        return self._project_points(x)
