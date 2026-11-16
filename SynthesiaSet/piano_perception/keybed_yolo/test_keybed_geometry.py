"""Regression checks for complete-keybed corner fitting."""

import unittest

import cv2
import numpy as np

from evaluate import Frame, Instance, Mask
from keybed_geometry import fit_keybed, select_keybed


HEIGHT, WIDTH = 240, 500
FRAME = Frame(HEIGHT, WIDTH, 0, 0, WIDTH, HEIGHT, WIDTH, HEIGHT)
AXIS_ALIGNED = ((60, 75), (430, 75), (430, 155), (60, 155))


def raster(vertices):
    mask = np.zeros((HEIGHT, WIDTH), np.uint8)
    cv2.fillConvexPoly(mask, np.asarray(vertices, dtype=np.int32), 1)
    return mask


def instance(class_id, mask, score):
    ys, xs = np.nonzero(mask)
    x, y, x2, y2 = xs.min(), ys.min(), xs.max() + 1, ys.max() + 1
    return Instance(class_id, np.float32([x, y, x2, y2]),
                    Mask.create(int(x), int(y), mask[y:y2, x:x2] != 0), score)


def white_keys(left=60, right=430, *, top=75, bottom=155):
    edges = np.rint(np.linspace(left, right, 21)).astype(int)
    return [instance(1, raster(((a, top), (b - 1, top), (b - 1, bottom), (a, bottom))), .85)
            for a, b in zip(edges[:-1], edges[1:], strict=True)]


class KeybedGeometryTests(unittest.TestCase):
    def assert_near(self, actual, expected, pixels):
        errors = np.linalg.norm(np.asarray(actual) - np.asarray(expected), axis=1)
        self.assertLessEqual(float(errors.max()), pixels)

    def test_rotated_and_oblique_corners(self):
        for truth in (AXIS_ALIGNED, ((77, 48), (450, 99), (427, 181), (55, 130)),
                      ((102, 67), (389, 82), (435, 158), (54, 153))):
            with self.subTest(truth=truth):
                corners, iou = fit_keybed(raster(truth))
                self.assert_near(corners, truth, 2)
                self.assertGreater(iou, .96)

    def test_isolated_noise_does_not_change_the_fit(self):
        mask = raster(AXIS_ALIGNED)
        clean_quad, clean_iou = fit_keybed(mask)
        mask[0, :5] = 1
        mask[211:215, 463:467] = 1
        noisy_quad, noisy_iou = fit_keybed(mask)
        self.assertEqual(noisy_quad, clean_quad)
        self.assertEqual(noisy_iou, clean_iou)

    def test_ellipse_and_fragmented_masks_are_rejected(self):
        ellipse = np.zeros((HEIGHT, WIDTH), np.uint8)
        cv2.ellipse(ellipse, (245, 115), (185, 40), 0, 0, 360, 1, -1)
        with self.assertRaisesRegex(ValueError, "supported"):
            fit_keybed(ellipse)
        fragmented = raster(AXIS_ALIGNED)
        fragmented[170:220, 60:430] = 1
        with self.assertRaisesRegex(ValueError, "disconnected"):
            fit_keybed(fragmented)

    def test_clipped_mask_cannot_be_replaced_by_an_interior_fragment(self):
        clipped = instance(0, raster(((0, 75), (430, 75), (430, 155), (0, 155))), .95)
        interior = instance(0, raster(AXIS_ALIGNED), .8)
        with self.assertRaisesRegex(ValueError, "touches an image edge"):
            select_keybed([interior, clipped, *white_keys()], FRAME)

    def test_a_corroborated_weak_bed_can_use_observed_outer_keys(self):
        weak = instance(0, raster(((89, 76), (425, 76), (425, 153), (89, 153))), .11)
        fit = select_keybed([*white_keys(), weak], FRAME)
        self.assertEqual(fit.method, "white-key-edges")
        self.assertEqual(fit.detection_index, 20)
        self.assertEqual(fit.white_support, 20)
        self.assertGreater(fit.surface_coverage, .85)
        self.assert_near(fit.quad, AXIS_ALIGNED, 2)
        with self.assertRaisesRegex(ValueError, "too few white"):
            select_keybed([weak], FRAME)

    def test_bed_below_the_geometry_confidence_floor_is_rejected(self):
        weak = instance(0, raster(AXIS_ALIGNED), .02)
        with self.assertRaisesRegex(ValueError, "confidence floor"):
            select_keybed([weak, *white_keys()], FRAME)

    def test_obscured_row_ends_do_not_support_a_weak_bed(self):
        weak = instance(0, raster(AXIS_ALIGNED), .11)
        keys = white_keys()
        keys[0] = instance(1, raster(((60, 110), (77, 110), (77, 155), (60, 155))), .85)
        with self.assertRaisesRegex(ValueError, "nearby white-key support"):
            select_keybed([weak, *keys], FRAME)

    def test_a_strong_complete_bed_can_fit_its_own_edges(self):
        strong = instance(0, raster(AXIS_ALIGNED), .88)
        fit = select_keybed([strong], FRAME)
        self.assertEqual(fit.method, "bed-mask-edges")
        self.assertEqual(fit.white_support, 0)
        self.assert_near(fit.quad, AXIS_ALIGNED, 2)


if __name__ == "__main__":
    unittest.main()

