"""Regression tests for calibration-oriented evaluation."""

from argparse import Namespace
from contextlib import contextmanager, nullcontext
from pathlib import Path
import tempfile
import unittest
from unittest.mock import MagicMock, patch

import numpy as np
from PIL import Image

from dataset import sha256_file, write_json
from evaluate import (
    Frame, Instance, Mask, box_overlaps, confidence_matches, corners_and_order, counts_at,
    decode, evaluate, load_selection, make_overlay, parity, pick_confidence, reference_instances,
    summarize, summarize_counts,
)
from fit import BudgetedBatches, budgeted_trainer
from geometry import CLASSES


class RecordingBudget:
    def __init__(self, events):
        self.events = events

    @contextmanager
    def operation(self):
        self.events.append("start")
        try:
            yield
        finally:
            self.events.append("rest")


class EvaluationTests(unittest.TestCase):
    def test_decode_clips_padding_and_excludes_exact_cutoff(self):
        frame = Frame(8, 8, 0, 2, 8, 4, 16, 8)
        rows = np.float32([[1.25, -1, 6, 7, 0.8, 1, 1], [1, 2, 2, 3, 0.25, 0, 1]])
        result = decode(rows, np.ones((1, 2, 2), np.float32), frame, 0.25)
        self.assertEqual(len(result), 1)
        self.assertEqual((result[0].mask.x, result[0].mask.y), (2, 2))
        self.assertEqual(result[0].mask.area, 16)

    def test_overlay_tolerates_inverted_box_while_counting_empty_prediction(self):
        frame = Frame(8, 8, 0, 0, 8, 8, 8, 8)
        rows = np.float32([[5, 1, 3, 6, 0.8, 1, 1]])
        result = decode(rows, np.ones((1, 2, 2), np.float32), frame, 0.25)
        self.assertEqual(result[0].mask.area, 0)
        classes = counts_at([], result, {}, 0.25)
        self.assertEqual(classes["white_key"]["extra"], 1)
        case = {"id": "test", "classes": classes, "corners": {"fitted": None}}
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / "overlay.png"
            make_overlay(output, np.zeros((1, 3, 8, 8)), [], result, {}, case, frame)
            with Image.open(output) as image:
                image.verify()

    def test_correct_counts_can_hide_duplicates_and_missing_masks(self):
        mask = Mask.create(0, 0, np.ones((2, 2), dtype=bool))
        gt = [Instance(1, np.array([0, 0, 2, 2]), mask), Instance(1, np.array([3, 0, 5, 2]), mask)]
        pred = [Instance(1, gt[0].box, mask, 0.8), Instance(1, gt[0].box, mask, 0.6)]
        matches = confidence_matches(box_overlaps(gt, pred), 0.5)
        self.assertEqual(counts_at(gt, pred, matches, 0.5)["white_key"], {
            "expected": 2, "predicted": 2, "matched": 1, "missing": 1, "extra": 1, "count_error": 0,
        })
        self.assertEqual(counts_at(gt, pred, matches, 0.7)["white_key"]["extra"], 0)

    def test_matching_never_claims_another_class(self):
        mask = Mask.create(0, 0, np.ones((2, 2), dtype=bool))
        box = np.array([0, 0, 2, 2])
        self.assertEqual(confidence_matches(box_overlaps(
            [Instance(1, box, mask)], [Instance(2, box, mask)],
        ), 0.5), {})

    def test_corners_are_normalized_by_front_edge_white_key_width(self):
        frame = Frame(100, 200, 0, 25, 200, 50, 400, 100)
        polygon = [[20, 20], [380, 20], [380, 80], [20, 80]]
        row = {"instances": [
            {"class": "playable_keybed", "polygon": polygon},
            {"class": "white_key", "polygon": [[20, 21], [40, 21], [40, 79], [20, 79]], "slot": 0},
        ]}
        refs = reference_instances(row, frame)
        delta = np.array([5, 0, 5, 0])
        mask = refs[0].mask
        moved = Instance(0, refs[0].box + delta, Mask.create(mask.x + 5, mask.y, mask.values))
        outcome = corners_and_order(row, frame, refs, [moved], {})
        self.assertAlmostEqual(outcome["white_width_error"]["front-left"], 10 / 360)
        self.assertIsNone(outcome["order_inversions"])

    def test_deployment_order_requires_fitted_bed_and_uses_its_perspective(self):
        frame = Frame(200, 200, 0, 0, 200, 200, 200, 200)
        polygon = [[10, 40], [190, 40], [190, 110], [10, 110]]
        row = {"instances": [
            {"class": "playable_keybed", "polygon": polygon},
            {"class": "white_key", "polygon": [[60, 48], [66, 48], [66, 52], [60, 52]], "slot": 0},
            {"class": "black_key", "polygon": [[79, 97], [85, 97], [85, 101], [79, 101]], "slot": 1},
        ]}
        refs = reference_instances(row, frame)
        keys = refs[1:]
        matches = {0: (1, 1.0), 1: (2, 1.0)}
        no_fit = corners_and_order(row, frame, refs, keys, matches)
        self.assertIsNone(no_fit["order_inversions"])
        self.assertEqual(no_fit["ground_truth_quad_order"]["matched_slot_order"], [0, 1])
        case = {"group": "g", "review_status": "accepted", "corners": no_fit,
                "classes": counts_at(refs, keys, matches, 0.5),
                "matched_mask_ious": {name: [] for name in CLASSES}}
        with patch("evaluate.average_precision", return_value={}):
            report = summarize(Namespace(onnx=None), {}, "test", 0.5, [case], [], [])
        self.assertEqual(report["order"]["positive_images_unobserved"], 1)
        self.assertEqual(report["order"]["positive_images_with_all_keys_correct_and_ordered"], 0)

        predicted_bed = reference_instances({"instances": [
            {"class": "playable_keybed", "polygon": [[10, 40], [145, 40], [190, 110], [55, 110]]},
        ]}, frame)[0]
        observed = corners_and_order(row, frame, refs, [*keys, predicted_bed], matches)
        self.assertEqual(observed["matched_slot_order"], [1, 0])
        self.assertEqual(observed["order_inversions"], 1)
        self.assertEqual(observed["ground_truth_quad_order"]["order_inversions"], 0)

    def test_confidence_ties_choose_the_more_conservative_threshold(self):
        common = summarize_counts([counts_at([], [], {}, 0.2)])
        records = [{"confidence": 0.2, "classes": common}, {"confidence": 0.5, "classes": common}]
        self.assertEqual(pick_confidence(records)["confidence"], 0.5)

    def test_parity_rejects_unmatched_predictions_and_changed_prototypes(self):
        mask = Mask.create(0, 0, np.ones((2, 2), dtype=bool))
        pred = Instance(1, np.array([0, 0, 2, 2]), mask, 0.8)
        proto = np.ones((1, 2, 2), dtype=np.float32)
        self.assertTrue(parity([pred], [pred], proto, proto)["pass"])
        self.assertFalse(parity([pred], [], proto, proto)["pass"])
        self.assertFalse(parity([pred], [pred], proto, proto + 0.01)["pass"])

    def test_identical_empty_rows_are_equal_for_parity_only(self):
        mask = Mask.create(0, 0, np.zeros((2, 2), dtype=bool))
        pred = Instance(1, np.array([0, 0, 0, 2]), mask, 0.8)
        proto = np.ones((1, 2, 2), dtype=np.float32)
        self.assertEqual(box_overlaps([pred], [pred])[0, 0], 0)
        result = parity([pred], [pred], proto, proto)
        self.assertTrue(result["pass"])
        self.assertEqual(result["nonempty_mask_pairs"], 0)

    def test_parity_matches_tied_same_box_rows_in_either_mask_order(self):
        first = Mask.create(0, 0, np.eye(3, dtype=bool))
        second = Mask.create(0, 0, np.fliplr(first.values))
        box = np.array([0, 0, 3, 3])
        left, right = Instance(1, box, first, 0.8), Instance(1, box, second, 0.8)
        proto = np.ones((1, 2, 2), dtype=np.float32)
        result = parity([left, right], [right, left], proto, proto)
        self.assertTrue(result["pass"])
        self.assertEqual(result["lowest_mask_iou"], 1.0)
        self.assertFalse(parity([left, right], [left, left], proto, proto)["pass"])

    def test_test_split_rejects_mismatched_validation_selection_before_loading(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            selection = root / "selection.json"
            write_json(selection, {"split": "val", "binding": {"shape": [640, 640]}, "confidence": 0.1})
            args = Namespace(command="test", output=root / "output", selection=selection)
            with patch("evaluate.binding_for", return_value={"shape": [480, 640]}):
                with self.assertRaisesRegex(ValueError, "test requires validation selection"):
                    evaluate(args)

    def test_changed_selection_prevents_publication_and_initial_hash_identifies_used_bytes(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            image = root / "source.png"
            Image.new("RGB", (32, 32)).save(image)
            selection = root / "selection.json"
            original = {"split": "val", "binding": {}, "confidence": 0.1}
            write_json(selection, original)
            initial_hash = sha256_file(selection)
            self.assertEqual(load_selection(selection, {}), (0.1, initial_hash))
            row = {"id": "case", "group": "g", "split": "test", "review": {"status": "negative"}, "instances": []}
            args = Namespace(command="test", dataset=root, weights=root / "keybed.pt", output=root / "output",
                             selection=selection, height=32, width=32, onnx=None)
            model = MagicMock()
            model.names = dict(enumerate(CLASSES))
            model.model.float.return_value.eval.return_value.fuse.return_value.return_value = ([], None)
            outputs = np.empty((0, 7), dtype=np.float32), np.ones((1, 8, 8), dtype=np.float32)
            with (patch("evaluate.binding_for", return_value={}),
                  patch("evaluate.verify_dataset", return_value=[(image, None, row)]),
                  patch("evaluate.ResourceBudget") as budget,
                  patch("evaluate.yolo", return_value=lambda *_a, **_kw: model),
                  patch("evaluate.unpack_outputs", return_value=outputs),
                  patch("evaluate.summarize", return_value={}),
                  patch("evaluate.make_contact_sheet", side_effect=lambda *_: write_json(
                      selection, {**original, "confidence": 0.2},
                  ))):
                budget.return_value.cpu = {"affinity": [1]}
                budget.return_value.torch.inference_mode.return_value = nullcontext()
                with self.assertRaisesRegex(ValueError, "validation selection changed"):
                    evaluate(args)
            self.assertFalse(args.output.exists())
            self.assertEqual(load_selection(selection, {}), (0.2, sha256_file(selection)))

    def test_validation_budgets_setup_batches_and_aggregation(self):
        class Trainer:
            def _validate(self, setup):
                events.append(setup)
                validator = Namespace(dataloader=range(2))
                for validator.batch_i in validator.dataloader:
                    batches.begin(validator)  # on_val_batch_start
                    events.append("validate batch")
                    batches.end_validation_batch(validator)  # on_val_batch_end
                events.append("aggregate losses")
                return self.label_loss_items({"box": 1.0}, prefix="val")

            def validate(self):
                return self._validate("allocate loss tensors")

            def final_eval(self):
                return self._validate("load and warm up checkpoint")

            def label_loss_items(self, loss_items, prefix):
                events.append("report loss")
                return {f"{prefix}/box": loss_items["box"]}

        for method, setup in (("validate", "allocate loss tensors"), ("final_eval", "load and warm up checkpoint")):
            with self.subTest(method=method):
                events = []
                batches = BudgetedBatches(RecordingBudget(events))
                trainer = budgeted_trainer(Trainer, batches)()
                self.assertEqual(getattr(trainer, method)(), {"val/box": 1.0})
                self.assertEqual(events, ["start", setup, "rest",
                                          "start", "validate batch", "rest",
                                          "start", "validate batch", "rest",
                                          "start", "aggregate losses", "report loss", "rest"])
                self.assertIsNone(batches.active)

    def test_training_maintenance_is_budgeted_and_failure_releases_phase(self):
        events = []
        batches = BudgetedBatches(RecordingBudget(events))

        class Trainer:
            def save_model(self):
                events.append("save EMA and optimizer")
                return True

            def _handle_nan_recovery(self, epoch):
                events.append(f"recover epoch {epoch}")
                self._clear_memory(0.5)
                raise RuntimeError("failed restore")

            def _clear_memory(self, threshold):
                events.append(f"clear memory at {threshold}")

        trainer = budgeted_trainer(Trainer, batches)()
        self.assertTrue(trainer.save_model())
        with self.assertRaisesRegex(RuntimeError, "failed restore"):
            trainer._handle_nan_recovery(4)
        self.assertIsNone(batches.active)
        self.assertEqual(events, ["start", "save EMA and optimizer", "rest",
                                  "start", "recover epoch 4", "clear memory at 0.5", "rest"])

    def test_failed_validation_batch_is_closed_once(self):
        events = []
        batches = BudgetedBatches(RecordingBudget(events))

        class Trainer:
            def validate(self):
                batches.begin(self)
                raise RuntimeError("interrupted batch")

        with self.assertRaisesRegex(RuntimeError, "interrupted batch"):
            budgeted_trainer(Trainer, batches)().validate()
        self.assertIsNone(batches.active)
        self.assertEqual(events, ["start", "rest", "start", "rest"])


if __name__ == "__main__":
    unittest.main()
