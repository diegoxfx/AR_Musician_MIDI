import numpy as np

from src.config import PianoConfig


class PianoMeasure:
    """
    Piano measurements utility class.
    """

    def __init__(self, cfg: PianoConfig):
        """
        :param cfg: Piano config specifying the random value ranges
        """

        # White keys
        self.white_length_total = np.random.uniform(*cfg.white_length_total_range)

        self.is_flat_keyboard = np.random.rand() < cfg.flat_keyboard_prob
        if self.is_flat_keyboard:
            self.white_height = np.random.uniform(*cfg.white_height_range_flat)
        else:
            self.white_height = np.random.uniform(*cfg.white_height_range)

        self.white_thick_bottom = np.random.uniform(*cfg.white_thick_bottom_range)

        thin_bottom_offset = np.random.uniform(*cfg.white_thin_bottom_offset_range)
        self.white_thin_bottom = self.white_thick_bottom - thin_bottom_offset

        # Black keys
        self.black_length_ratio = np.random.uniform(*cfg.black_length_ratio_range)

        black_height_offset = np.random.uniform(*cfg.black_height_offset_range)
        self.black_height = self.white_height + black_height_offset

        black_width_offset = np.random.uniform(*cfg.black_width_offset_range)
        self.black_width = self.white_thin_bottom / 2 + black_width_offset

        # Other params
        self.space = np.random.uniform(*cfg.space_range)
        self.group_3_ratio = np.random.uniform(*cfg.group_3_ratio_range)
        self.group_4_ratio = np.random.uniform(*cfg.group_4_ratio_range)

        # Implicit params
        self.white_height_offset = self.white_height / 2
        self.black_height_offset = self.black_height / 2

        self.white_length_top = self.black_length_ratio * self.white_length_total
        self.white_length_bottom = (1 - self.black_length_ratio) * self.white_length_total

        self.black_length = self.white_length_top

        self.white_top_offset = -self.white_length_total / 2
        self.black_offset = self.white_top_offset

        self.half_space = self.space / 2
