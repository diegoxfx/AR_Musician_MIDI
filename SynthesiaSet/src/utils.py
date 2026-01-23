import numpy as np
import trimesh
from trimesh.smoothing import filter_humphrey


def make_homo(x: np.ndarray) -> np.ndarray:
    """
    Adds homogeneous coordinate to the array.

    :param x: (..., d) Input array

    :return: (..., d+1) Homogeneous array
    """

    return np.concatenate([x, np.ones((*x.shape[:-1], 1))], axis=-1)


def make_nohomo(x: np.ndarray) -> np.ndarray:
    """
    Normalizes a homogeneous array.

    :param x: (..., d+1) Input array

    :return: (..., d) Normalized inhomogeneous array
    """

    return x[..., :-1] / x[..., -1, None]


def refine_corners(
    mesh: trimesh.Trimesh,
    subdiv_iter=4,
    alpha=0.1,
    beta=0.1,
    smooth_iter=1,
) -> trimesh.Trimesh:
    """
    Smooth the edges of a certain mesh using subdivision and Humphrey Filtering.

    :param mesh: Mesh to smoothen.
    :param subdiv_iter: Number of subdivision iterations, defaults to 4
    :param alpha: Controls shrinkage of mesh, defaults to 0.1
    :param beta: Controls how strong it smoothens, defaults to 0.1
    :param smooth_iter: Number of smoothing iterations, defaults to 1

    :return: Smoothened mesh.
    """

    mesh = mesh.subdivide(iterations=subdiv_iter)
    filter_humphrey(mesh, alpha=alpha, beta=beta, iterations=smooth_iter)
    mesh = mesh.simplify_quadric_decimation(0.9, aggression=0)
    return mesh
