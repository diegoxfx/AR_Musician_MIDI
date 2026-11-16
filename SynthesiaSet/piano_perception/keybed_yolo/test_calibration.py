"""Integration checks for prediction-only calibration and validation scoring."""

from dataclasses import asdict, replace
from pathlib import Path
import tempfile
import unittest

import numpy as np

from calibration import calibration_candidate
from dataset import write_json
from evaluate import Frame, Mask, reference_instances
from evaluate_calibration import evaluate_cache, score_variant, summarize_variant


FRAME = Frame(300, 500, 0, 30, 500, 240, 500, 240)
QUAD = [[60, 75], [430, 75], [430, 155], [60, 155]]


def example():
    edges = np.rint(np.linspace(60, 430, 21)).astype(int).tolist()
    row = {
        "id": "example", "group": "example", "split": "val", "review": {"status": "accepted"},
        "instances": [
            {"class": "playable_keybed", "polygon": QUAD},
            *({"class": "white_key", "slot": index,
               "polygon": [[left, 75], [right - 1, 75], [right - 1, 155], [left, 155]]}
              for index, (left, right) in enumerate(zip(edges[:-1], edges[1:], strict=True))),
        ],
    }
    references = reference_instances(row, FRAME)
    bed = references[0]
    fragments = np.zeros_like(bed.mask.values)
    fragments[:, :30] = fragments[:, -30:] = 1
    stronger = replace(bed, score=.95, slot=None, mask=Mask.create(bed.mask.x, bed.mask.y, fragments))
    weak = replace(bed, score=.11, slot=None)
    whites = [replace(item, score=.20 if item.slot == 10 else .85, slot=None) for item in references[:0:-1]]
    duplicate = replace(whites[1], score=.60)
    predictions = [stronger, weak, *whites, duplicate]
    return row, references, predictions


class CalibrationTests(unittest.TestCase):
    def test_geometry_inspects_raw_predictions_and_preserves_bed_and_key_indices(self):
        row, references, predictions = example()
        candidate = calibration_candidate(predictions, FRAME)
        self.assertEqual(candidate.roi_detection_index, 0)
        self.assertIsNone(candidate.fit_failure)
        self.assertEqual(candidate.fit.detection_index, 1)
        self.assertEqual(candidate.fit.method, "white-key-edges")
        np.testing.assert_allclose(candidate.fit.quad, QUAD, atol=2)
        self.assertNotIn(1, candidate.kept_detection_indices)
        self.assertNotIn(len(predictions) - 1, candidate.kept_detection_indices)
        self.assertEqual(candidate.key_detection_order, tuple(range(21, 1, -1)))

        baseline = score_variant(row, FRAME, references, predictions)
        self.assertEqual(baseline["duplicate_predictions"]["white_key"], 1)
        self.assertEqual(baseline["classes"]["white_key"]["count_error"], 0)
        self.assertFalse(baseline["exact_keys"])
        improved = score_variant(row, FRAME, references, predictions, candidate)
        self.assertEqual(improved["matched_slot_order"], list(range(20)))
        self.assertTrue(improved["exact_keys_strict_corners_and_ordered"])

    def test_rejections_and_slot_inversions_do_not_pass_the_joint_check(self):
        row, references, predictions = example()
        candidate = calibration_candidate(predictions, FRAME)
        passed = score_variant(row, FRAME, references, predictions, candidate)
        rejected = score_variant(row, FRAME, references, predictions, replace(
            candidate, fit=None, fit_failure="insufficient support", key_detection_order=None,
        ))
        reversed_order = score_variant(row, FRAME, references, predictions, replace(
            candidate, key_detection_order=candidate.key_detection_order[::-1],
        ))
        self.assertTrue(rejected["exact_keys"])
        self.assertFalse(rejected["strict_corners"])
        self.assertIsNone(rejected["order_inversions"])
        self.assertGreater(reversed_order["order_inversions"], 0)
        self.assertTrue(reversed_order["strict_corners"])
        self.assertFalse(reversed_order["exact_keys_strict_corners_and_ordered"])
        summary = summarize_variant([passed, rejected, reversed_order], roi_enabled=True)
        self.assertEqual(summary["strict_corners"], {"passed": 2, "total": 3, "rate": 2 / 3})
        self.assertEqual(summary["geometry"]["rejected"], 1)
        self.assertEqual(summary["geometry"]["rejection_reasons"], {"insufficient support": 1})
        self.assertEqual(summary["exact_keys_strict_corners_and_ordered"],
                         {"passed": 1, "total": 3, "rate": 1 / 3})

    def test_strict_corners_uses_the_worst_corner(self):
        row, references, predictions = example()
        candidate = calibration_candidate(predictions, FRAME)
        quad = [*candidate.fit.quad]
        quad[0] = (QUAD[0][0] + 6, QUAD[0][1])
        moved = replace(candidate, fit=replace(candidate.fit, quad=tuple(quad)))
        outcome = score_variant(row, FRAME, references, predictions, moved)
        errors = outcome["corners"]["white_width_error"]
        self.assertLess(np.mean(list(errors.values())), .25)
        self.assertGreater(errors["back-left"], .25)
        self.assertFalse(outcome["strict_corners"])

    def test_cache_refuses_a_test_row_before_opening_any_predictions(self):
        row, _, _ = example()
        with tempfile.TemporaryDirectory() as directory:
            manifest = Path(directory) / "manifest.json"
            write_json(manifest, {"split": "val", "cases": [{"row": row}, {"row": {"split": "test"}}]})
            with self.assertRaisesRegex(ValueError, "validation-only"):
                evaluate_cache(manifest)

    def test_cache_verifies_the_prediction_bytes(self):
        row, _, _ = example()
        with tempfile.TemporaryDirectory() as directory:
            manifest = Path(directory) / "manifest.json"
            manifest.with_name("example.npz").write_bytes(b"changed")
            write_json(manifest, {"split": "val", "input_shape": [1, 3, FRAME.height, FRAME.width], "cases": [
                {"id": row["id"], "group": row["group"], "row": row, "frame": asdict(FRAME),
                 "predictions": "example.npz", "sha256": "0" * 64},
            ]})
            with self.assertRaisesRegex(ValueError, "cached prediction hash mismatch"):
                evaluate_cache(manifest)


if __name__ == "__main__":
    unittest.main()
