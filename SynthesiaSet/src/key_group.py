from typing import Self

import numpy as np
import trimesh
from trimesh import Trimesh

from src.piano_types import KeyGroupType
from src.piano_utils import PianoMeasure
from src.utils import refine_corners


class KeyGroup:
    """
    Utility to create and merge groups of white and black piano keys.
    """

    _GROUP_DISPATCH = {
        KeyGroupType.GROUP_1: "create_1_group",
        KeyGroupType.GROUP_2: "create_2_group",
        KeyGroupType.GROUP_3: "create_3_group",
        KeyGroupType.GROUP_4: "create_4_group",
        KeyGroupType.GROUP_7: "create_7_group",
    }

    def __init__(self, measure: PianoMeasure):
        """
        Init a group of keys following the given measurements.

        :param measure: Piano measurements like thickness of keys etc.
        """

        self.width = 0  # Total mesh width
        self.n_black_keys = 0
        self.n_white_keys = 0
        self.white_key_meshes = []
        self.black_key_meshes = []
        self.measure = measure

    @classmethod
    def materialize(cls, key_group_type: KeyGroupType, measure: PianoMeasure) -> Self:
        """
        Creates a predefined piano group.

        :param key_group_type: Piano group type.
        :param measure: Piano measurements like thickness of keys etc.

        :return: Key group instance.
        """

        method_name = cls._GROUP_DISPATCH[key_group_type]
        return getattr(cls, method_name)(measure)

    @classmethod
    def merge(cls, left: Self, right: Self) -> Self:
        """
        Merge two key groups together, such that the right group is put on the right
        side of the left group. The final mesh is zero-centered.

        Assumes that both groups have the same measurement.

        :param left: Left key group.
        :param right: Right key group.

        :return: Merged key group.
        """

        total_width = left.width + right.width

        left_shift = -(total_width - left.width) / 2
        right_shift = (total_width - right.width) / 2

        left._shift_all(left_shift)
        right._shift_all(right_shift)

        group = cls(left.measure)
        group.width = total_width
        group.n_black_keys = left.n_black_keys + right.n_black_keys
        group.n_white_keys = left.n_white_keys + right.n_white_keys
        group.white_key_meshes = left.white_key_meshes + right.white_key_meshes
        group.black_key_meshes = left.black_key_meshes + right.black_key_meshes

        return group

    @classmethod
    def merge_all(cls, groups: list[Self]) -> Self:
        """
        Merge list of key groups into one big group. Left-most group is the first in the
        list and right-most is the last. Final mesh is zero-centered.

        :param groups: List of key groups to merge.

        :return: Big merged key group.
        """

        merged_group = cls(groups[0].measure)
        for group in groups:
            merged_group = cls.merge(merged_group, group)
        return merged_group

    def _shift_all(self, x_translation: float):
        """
        Translates all white and black keys on the x-axis.

        :param x_translation: Translation value.
        """

        for mesh in self.white_key_meshes:
            mesh.apply_translation([x_translation, 0, 0])
        for mesh in self.black_key_meshes:
            mesh.apply_translation([x_translation, 0, 0])

    def _add_white_key(
        self,
        bottom_width: float,
        x_translation: float,
        left_indent: float,
        right_indent: float,
    ):
        """
        Magic function which creates a parameterized white key. Each white key is
        fundamentally two stacked white boxes: the thick bottom and the thin upper.
        The two boxes get merged together into a single mesh before returning.
        The top-width is implicitly defined using left_indent and right_indent.

        Important: the width of the mesh is not the same as bottom_width, as the spacing
                   gets implicitly removed. See PianoConfig for more details.

        :param bottom_width: The lower width of the key.
        :param x_translation: Translation on x-axis.
        :param left_indent: How far apart the thin top part is to the outermost left edge.
        :param right_indent: How far apart the thin top part is to the outermost right edge.
        """

        # Create the thick bottom part (need to remove one half_space in height)
        bottom = trimesh.creation.box(
            extents=[
                bottom_width - self.measure.space,
                self.measure.white_height,
                self.measure.white_length_bottom - self.measure.half_space,
            ]
        )

        # space translation (z is positive, as we need to move down)
        bottom.apply_translation([0, 0, self.measure.half_space / 2])

        # Create the thin top part
        top_width = bottom_width - left_indent - right_indent
        # need to add halfspace to z, as it gets removed from the bottom half
        top = trimesh.creation.box(
            extents=[
                top_width - self.measure.space,
                self.measure.white_height,
                self.measure.white_length_top + self.measure.half_space,
            ]
        )
        top_offset = -bottom_width / 2 + top_width / 2 + left_indent
        # + half_space in z as we want to move down
        top.apply_translation([top_offset, 0, self.measure.white_top_offset + self.measure.half_space / 2])

        # Merge together and position globally
        key = trimesh.boolean.boolean_manifold([bottom, top], operation="union", check_volume=False)
        key.apply_translation([x_translation, self.measure.white_height_offset, 0])

        # Color the key white
        normalized_color = np.array([1.0, 1.0, 1.0], dtype=np.float32)
        key.visual.vertex_colors = np.tile(normalized_color, (len(key.vertices), 1))

        self.white_key_meshes.append(key)
        self.width += bottom_width

    def _add_black_key(self, x_translation: float):
        """
        Magic function which creates a parameterized black key.

        :param x_translation: Group-local translation on x-axis.
        """

        # Remove a half-width from left, right and bottom. Spacing has no influence on
        # top alignment of a black key.
        black = trimesh.creation.box(
            extents=[
                self.measure.black_width - self.measure.space,
                self.measure.black_height,
                self.measure.black_length - self.measure.half_space,
            ]
        )
        # Global translation
        black.apply_translation([x_translation, self.measure.black_height_offset, self.measure.black_offset])

        # spacing translation (note that z is negative)
        black.apply_translation([0, 0, -self.measure.half_space / 2])

        # Color the key black
        normalized_color = np.array([0.0, 0.0, 0.0], dtype=np.float32)
        black.visual.vertex_colors = np.tile(normalized_color, (len(black.vertices), 1))

        self.black_key_meshes.append(black)

    @classmethod
    def create_3_group(cls, measure: PianoMeasure) -> Self:
        """
        Create key group of notes:  C# D#
                                   C  D  E

        :param measure: Measurements used for the group.

        :return: Key group with 3 whites and 2 blacks.
        """

        black_offset = (
            measure.white_thin_bottom - measure.black_width
        ) / 2 + measure.group_3_ratio * measure.black_width
        inner_offset = (1 - measure.group_3_ratio) * measure.black_width
        outer_offset = measure.group_3_ratio * measure.black_width

        white_1_translation = -(measure.white_thick_bottom + measure.white_thin_bottom) / 2
        white_3_translation = -white_1_translation

        group = cls(measure)
        group._add_white_key(measure.white_thick_bottom, white_1_translation, 0, outer_offset)
        group._add_white_key(measure.white_thin_bottom, 0, inner_offset, inner_offset)
        group._add_white_key(measure.white_thick_bottom, white_3_translation, outer_offset, 0)

        group._add_black_key(black_offset)
        group._add_black_key(-black_offset)
        return group

    @classmethod
    def create_4_group(cls, measure: PianoMeasure) -> Self:
        """
        Create key group of notes:  F# G# A#
                                   F  G  A  B

        :param measure: Measurements used for the group.

        :return: Key group with 4 whites and 3 blacks.
        """

        black_offset = measure.white_thin_bottom + (measure.group_4_ratio - 0.5) * measure.black_width
        inner_offset = (1 - measure.group_4_ratio) * measure.black_width
        outer_offset = measure.group_4_ratio * measure.black_width

        white_translation_1 = -(measure.white_thin_bottom + measure.white_thick_bottom / 2)
        white_translation_2 = -(measure.white_thin_bottom / 2)
        white_translation_3 = -white_translation_2
        white_translation_4 = -white_translation_1

        group = cls(measure)

        group._add_black_key(0)
        group._add_black_key(black_offset)
        group._add_black_key(-black_offset)

        group._add_white_key(measure.white_thick_bottom, white_translation_1, 0, outer_offset)
        group._add_white_key(measure.white_thin_bottom, white_translation_2, inner_offset, measure.black_width / 2)
        group._add_white_key(measure.white_thin_bottom, white_translation_3, measure.black_width / 2, inner_offset)
        group._add_white_key(measure.white_thick_bottom, white_translation_4, outer_offset, 0)

        return group

    @classmethod
    def create_7_group(cls, measure: PianoMeasure) -> Self:
        """
        Create key group of notes:  C# D#    F# G# A#
                                   C  D  E  F  G  A  B

        :param measure: Measurements used for the group.

        :return: Key group with 7 whites and 5 blacks.
        """

        left = cls.create_3_group(measure)
        right = cls.create_4_group(measure)
        return cls.merge(left, right)

    @classmethod
    def create_1_group(cls, measure: PianoMeasure) -> Self:
        """
        Create key group of a single white key.

        :param measure: Measurements used for the group.

        :return: Key group with 1 full white key.
        """
        group = cls(measure)
        group._add_white_key(measure.white_thick_bottom, 0, 0, 0)
        return group

    @classmethod
    def create_2_group(cls, measure: PianoMeasure) -> Self:
        """
        Create key group of two whites and single black in the middle.

        :param measure: Measurements used for the group.

        :return: Key group with 2 whites and 1 black in center.
        """

        white_translation_1 = -measure.white_thin_bottom / 2
        white_translation_2 = -white_translation_1

        group = cls(measure)

        group._add_black_key(0)
        group._add_white_key(measure.white_thin_bottom, white_translation_1, 0, measure.black_width / 2)
        group._add_white_key(measure.white_thin_bottom, white_translation_2, measure.black_width / 2, 0)
        return group

    def get_trimesh(self) -> tuple[Trimesh, Trimesh]:
        """
        Merges all white and black keys in a group into two separate trimesh Meshes.

        :return: White and black keys trimesh mesh.
        """

        whites = trimesh.util.concatenate(self.white_key_meshes)
        blacks = trimesh.util.concatenate(self.black_key_meshes)

        # Smooth the edges of the meshes
        whites = refine_corners(whites)
        blacks = refine_corners(blacks)

        return whites, blacks
