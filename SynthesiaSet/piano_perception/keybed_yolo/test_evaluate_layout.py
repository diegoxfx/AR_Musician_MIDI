"""Independent coordinate and failure checks for approximate guide metrics."""

from types import SimpleNamespace as NS
import unittest

import cv2
import numpy as np

from evaluate import Frame
from evaluate_layout import horizontal_interval, line_x_at_depth, project, score_layout, summarize, UNIT


def fixture(shift=0):
    # Asymmetric letterboxing and deliberately unequal X/Y source scaling.
    frame = Frame(400, 700, 31, 47, 600, 280, 1200, 800)
    quad = np.float32([[200, 200], [960, 230], [1080, 650], [100, 680]])
    transform = cv2.getPerspectiveTransform(UNIT, quad)
    whites = []
    guides = []
    for index in range(7):
        left, right = index / 7, (index + 1) / 7
        whites.append({"class": "white_key", "slot": index,
                       "polygon": project([[left, 0], [right, 0], [right, 1], [left, 1]], transform).tolist()})
        segment = project([[(index + .5 + shift) / 7, 0], [(index + .5 + shift) / 7, 1]], transform)
        guides.append(NS(is_black=False, white_ordinal=index, observed=index in (0, 3, 6),
                         segment=frame.to_input(segment) - (frame.left, frame.top)))
    row = {"instances": [{"class": "playable_keybed", "polygon": quad.tolist()}, *whites]}
    fit = NS(quad=frame.to_input(quad) - (frame.left, frame.top))
    preview = NS(status="needs_confirmation", reason=None, guides=guides, evidence=None)
    return row, frame, fit, preview


class LayoutEvaluationTests(unittest.TestCase):
    def test_projected_guides_score_at_the_reviewed_plane_depth(self):
        result = score_layout(*fixture())
        self.assertTrue(result["joint_white_guides_and_corners"])
        self.assertLess(result["white_max_lateral_error"], 1e-5)
        self.assertEqual(result["estimated_guides"], 4)

    def test_quarter_key_lateral_shift_fails_despite_perfect_corners(self):
        result = score_layout(*fixture(.26))
        self.assertTrue(result["strict_corners"])
        self.assertFalse(result["all_white_guides_positioned"])
        self.assertAlmostEqual(result["white_max_lateral_error"], .26, places=5)

    def test_rejected_preview_remains_in_full_photo_denominator(self):
        row, frame, _, preview = fixture()
        preview.status, preview.reason, preview.guides = "rejected", "missing outline", []
        result = score_layout(row, frame, None, preview)
        stats = summarize([score_layout(*fixture()), result])
        self.assertEqual(stats["total"], 2)
        self.assertEqual(stats["joint_white_guides_and_corners"], 1)
        self.assertEqual(stats["rejections"], {"missing outline": 1})

    def test_partial_guide_cannot_extrapolate_past_its_segment(self):
        self.assertIsNone(line_x_at_depth([[0, 0], [1, .6]], .8))
        row, frame, fit, preview = fixture()
        preview.guides[2].segment[1] = preview.guides[2].segment.mean(axis=0)
        self.assertFalse(score_layout(row, frame, fit, preview)["all_white_guides_positioned"])

    def test_disconnected_or_wrong_reference_order_is_not_scored(self):
        with self.assertRaises(ValueError):
            horizontal_interval([[0, 0], [1, 0], [1, .2], [.2, .2], [.2, .8], [1, .8], [1, 1], [0, 1]], 2)
        row, frame, fit, preview = fixture()
        row["instances"][1]["slot"] = 4
        with self.assertRaisesRegex(ValueError, "unique"):
            score_layout(row, frame, fit, preview)

    def test_profile_pattern_and_guide_order_are_required(self):
        row, frame, fit, preview = fixture()
        preview.guides[2].is_black = True
        self.assertFalse(score_layout(row, frame, fit, preview)["all_white_guides_positioned"])
        row, frame, fit, preview = fixture()
        preview.guides[2].white_ordinal = 3
        self.assertFalse(score_layout(row, frame, fit, preview)["all_white_guides_positioned"])


if __name__ == "__main__":
    unittest.main()
