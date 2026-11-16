"""Pixel transforms, abstention and data isolation for local corner heatmaps."""

import json
import unittest

import numpy as np
import torch

from corner_refiner import (CornerHeatmaps, RefinementConfig, TrainingCrops, crop_image,
                            crop_transform, heatmap_point, inverse_point, refine_quad,
                            score, summary, validate_seeds)


class FixedMaps(torch.nn.Module):
    def __init__(self, positions=None):
        super().__init__()
        self.positions = positions

    def forward(self, image):
        batch, _, height, width = image.shape
        result = torch.zeros((batch, 4, height, width))
        if self.positions:
            for role, (x, y) in enumerate(self.positions):
                result[:, role, y, x] = 25
        return result


class RefinerTests(unittest.TestCase):
    def setUp(self):
        self.image = np.zeros((100, 200, 3), dtype=np.uint8)
        self.quad = [[30, 25], [160, 25], [170, 70], [20, 70]]
        self.row = {"id": "piano", "split": "train", "corners": self.quad, "white_keys": 15}

    def test_fractional_pixel_transform_roundtrip_and_border(self):
        matrix = crop_transform([11.25, -2.5], 80, 128)
        np.testing.assert_allclose(matrix @ [11.25, -2.5, 1], [63.5, 63.5])
        point = [-6.75, 17.125]
        np.testing.assert_allclose(inverse_point(matrix @ np.r_[point, 1], matrix), point)
        crop, _ = crop_image(self.image, [0, 0], 48, 128)
        self.assertEqual(crop.shape, (128, 128, 3))
        np.testing.assert_equal(crop[0, 0], [114] * 3)
        np.testing.assert_equal(crop[-1, -1], [0] * 3)

    def test_each_role_uses_its_own_prediction_and_source_inverse(self):
        positions = [(61, 62), (66, 62), (66, 66), (61, 66)]
        result = refine_quad(FixedMaps(positions), self.image, self.quad)
        self.assertIsNone(result['rejection'])
        self.assertEqual(result['retained_seed'], [False] * 4)
        expected = np.asarray(self.quad) + (np.asarray(positions) - 63.5) * 48 / 128
        np.testing.assert_allclose(result['quad'], expected, atol=1e-6)
        self.assertEqual(result['status'], 'needs_confirmation')
        self.assertEqual(json.loads(json.dumps(result)), result)

    def test_diffuse_or_far_predictions_retain_coarse_corners(self):
        for model in (FixedMaps(), FixedMaps([(0, 0)] * 4)):
            result = refine_quad(model, self.image, self.quad)
            self.assertEqual(result['quad'], self.quad)
            self.assertEqual(result['retained_seed'], [True] * 4)

    def test_no_seed_and_invalid_geometry_are_not_scored_as_fits(self):
        self.assertIsNone(refine_quad(None, self.image, None)['quad'])
        bad = [self.quad[0], self.quad[2], self.quad[1], self.quad[3]]
        self.assertIsNone(refine_quad(None, self.image, bad)['quad'])
        boundary = [[1, 25], [160, 25], [170, 70], [1, 70]]
        result = refine_quad(FixedMaps([(38, 64), (64, 64), (64, 64), (38, 64)]), self.image, boundary)
        self.assertIsNone(result['quad'])
        self.assertIn('refined geometry', result['rejection'])

    def test_local_expectation_does_not_average_distant_modes(self):
        logits = np.full((128, 128), -20.)
        logits[25, 30] = 10
        logits[100, 100] = 9.9
        point, peak = heatmap_point(logits)
        np.testing.assert_allclose(point, [30, 25], atol=1e-6)
        self.assertGreater(peak, .5)

    def test_train_crops_exclude_heldout_and_are_epoch_deterministic(self):
        rows = [self.row, {**self.row, 'id': 'val', 'split': 'val'},
                {**self.row, 'id': 'test', 'split': 'test'}]
        data = TrainingCrops(rows, {'piano': self.image}, seed=71)
        self.assertEqual(len(data), 4)
        image, role, heatmap = data[2]
        self.assertEqual(role, 2)
        self.assertEqual(image.shape, (3, 128, 128))
        self.assertAlmostEqual(float(heatmap.sum()), 1, places=6)
        np.testing.assert_array_equal(data[2][2], heatmap)
        data.epoch += 1
        self.assertFalse(np.array_equal(data[2][2], heatmap))
        y, x = np.unravel_index(heatmap.argmax(), heatmap.shape)
        self.assertTrue(20 < x < 107 and 20 < y < 107)

    def test_validation_seed_manifest_requires_all_val_images_only(self):
        rows = [self.row, {**self.row, 'id': 'val', 'split': 'val'}]
        manifest = {'split': 'val', 'variants': {'one': {'quads': {'val': None}, 'source_sha256': 'a' * 64}}}
        validate_seeds(manifest, rows)
        manifest['variants']['one']['quads']['piano'] = self.quad
        with self.assertRaises(ValueError):
            validate_seeds(manifest, rows)

    def test_rejections_remain_in_denominator_and_threshold_is_per_key(self):
        good = score(self.row, {'quad': self.quad})
        absent = score({**self.row, 'id': 'absent'}, {'quad': None})
        shifted = np.asarray(self.quad, dtype=float)
        shifted[1, 0] += 2.5
        at_limit = score(self.row, {'quad': shifted.tolist()})
        self.assertTrue(at_limit['strict'])
        shifted[1, 0] += .01
        self.assertFalse(score(self.row, {'quad': shifted.tolist()})['strict'])
        report = summary([good, absent])
        self.assertEqual((report['total'], report['fitted'], report['strict']), (2, 1, 1))
        self.assertEqual(report['mean_capped_max_error'], 2)

    def test_network_output_preserves_heatmap_size_and_has_finite_gradients(self):
        model = CornerHeatmaps()
        result = model(torch.zeros(1, 3, 32, 32))
        self.assertEqual(tuple(result.shape), (1, 4, 32, 32))
        result[0, 2, 12, 19].backward()
        self.assertTrue(all(parameter.grad is not None and torch.isfinite(parameter.grad).all()
                            for parameter in model.parameters()))
        with self.assertRaises(ValueError):
            RefinementConfig(maximum_move_fraction=.75)


if __name__ == '__main__':
    unittest.main()
