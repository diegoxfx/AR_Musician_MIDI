"""Regression checks for the four-corner pose data and scoring contracts."""

import json
from importlib.util import find_spec
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

import numpy as np
from PIL import Image, PngImagePlugin

from corner_pose import (
    KPT_SIGMAS, fitness, paths, pose_input, pose_label, pose_yaml, score_pose, source_keypoints, summarize,
    top_pose, verify_pose_dataset,
)
from corner_pose_data import LABEL_SCOPE, augment
from dataset import sha256, sha256_file, write_json
from fit import yolo
from geometry import CORNER_NAMES


def reference(key="piano", split="val"):
    return {
        "id": key, "group": key, "split": split, "width": 200, "height": 100,
        "corners": [[20, 20], [170, 24], [180, 60], [20, 60]], "white_keys": 16,
    }


class PoseScoringTests(unittest.TestCase):
    def test_encoding_has_one_class_ordered_four_visible_points(self):
        values = [float(value) for value in pose_label(reference()).split()]
        self.assertEqual(values[:5], [0, 0.5, 0.4, 0.8, 0.4])
        self.assertEqual(values[5:], [0.1, 0.2, 2, 0.85, 0.24, 2, 0.9, 0.6, 2, 0.1, 0.6, 2])

    def test_source_coordinates_use_both_scales_and_never_clamp(self):
        actual = source_keypoints(
            [[1.0, 14, 0.6], [8, 19, 0.7], [18, 22, 0.9], [3, 20, 0.8]],
            ((0.5, 0.25), (2, 10)),
        )
        np.testing.assert_allclose(actual, [[-4, 8, 0.6], [24, 18, 0.7], [64, 24, 0.9], [4, 20, 0.8]])

    def test_pose_input_is_full_frame_and_records_rounded_resize(self):
        image = Image.new("RGB", (99, 137), (24, 48, 72))
        pixels, ratio_pad = pose_input(image, 128)
        self.assertEqual(pixels.shape, (1, 3, 128, 128))
        self.assertEqual(ratio_pad, ((128 / 137, 93 / 99), (17, 0)))
        np.testing.assert_allclose(pixels[0, :, 0, 16], [114 / 255] * 3)
        np.testing.assert_allclose(pixels[0, :, 0, 17], [24 / 255, 48 / 255, 72 / 255])
        np.testing.assert_allclose(pixels[0, :, 127, 109], [24 / 255, 48 / 255, 72 / 255])
        np.testing.assert_allclose(pixels[0, :, 127, 110], [114 / 255] * 3)

    def test_top_pose_is_selected_without_reference_and_respects_floor(self):
        outputs = np.zeros((1, 2, 18))
        outputs[0, :, 4] = [0.6, 0.8]
        points = np.column_stack((reference()["corners"], [0.1, 0.2, 0.3, 0.4]))
        outputs[0, 1, 6:] = points.flatten()
        best, confidence = top_pose(outputs, ((1, 1), (0, 0)))
        np.testing.assert_allclose(best, points)
        self.assertEqual(confidence, 0.8)
        outputs[0, :, 4] = 0.0005
        self.assertEqual(top_pose(outputs, ((1, 1), (0, 0))), (None, 0.0005))
        with self.assertRaisesRegex(ValueError, "end-to-end pose"):
            top_pose(np.ones((1, 18, 300)), ((1, 1), (0, 0)))

    def test_corner_max_is_in_white_widths_and_keeps_order(self):
        row = reference()
        points = np.column_stack((row["corners"], [0.9] * 4))
        points[2, 0] += 2.5
        case = score_pose(row, points, 0.8)
        self.assertEqual(case["max_error_white_widths"], 0.25)
        self.assertEqual(case["errors_white_widths"]["front-right"], 0.25)
        self.assertTrue(case["strict"])
        points[2, 0] += 0.01
        self.assertFalse(score_pose(row, points, 0.8)["strict"])
        shifted = score_pose(row, np.roll(points, 1, axis=0), 0.8)
        self.assertFalse(shifted["strict"])
        self.assertIsNotNone(shifted["rejection"])
        self.assertEqual(shifted["ordered_corners"][0], list(points[-1, :2]))
        self.assertEqual(tuple(shifted["errors_white_widths"]), CORNER_NAMES)

    def test_rejections_stay_in_denominator(self):
        row = reference()
        points = np.column_stack((row["corners"], [0.9] * 4))
        cases = [score_pose(row, points, 0.8), score_pose(reference("lost"), None, None)]
        report = summarize(cases)
        self.assertEqual(report["total"], 2)
        self.assertEqual(report["strict"], 1)
        self.assertEqual(report["valid_quad"], 1)
        self.assertEqual(report["mean_capped_max_error_white_widths"], 2)
        self.assertGreater(fitness(report), fitness({"strict": 0, "mean_capped_max_error_white_widths": 0}))

    @unittest.skipUnless(find_spec("ultralytics"), "requires pinned training dependencies")
    def test_full_width_corners_receive_gradient_from_center_initialization(self):
        import torch

        with TemporaryDirectory() as config:
            yolo(Path(config))
            from ultralytics.utils.loss import KeypointLoss

        # An approximately 768 by 85 pixel bed, viewed on the stride-32 head.
        targets = torch.tensor([[[-12., -4 / 3], [12., -4 / 3], [12., 4 / 3], [-12., 4 / 3]]])
        visible = torch.ones((1, 4), dtype=torch.bool)
        area = torch.tensor([[64.]])

        def horizontal_gradient(sigmas):
            offsets = torch.zeros_like(targets, requires_grad=True)
            loss = KeypointLoss(torch.tensor(sigmas))(offsets, targets, visible, area) * 12
            return torch.autograd.grad(loss, offsets)[0][..., 0].abs().min().item()

        self.assertLess(horizontal_gradient((0.12,) * 4), 1e-6)
        self.assertGreater(horizontal_gradient(KPT_SIGMAS), 0.05)


class PosePackingTests(unittest.TestCase):
    def setUp(self):
        self.temporary = TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.base = self.root / "base"
        self.canonical = []
        for index, split in enumerate(("train", "val", "test")):
            row = self.source_row(split, split, index)
            image, label = paths(self.base, row)
            image.parent.mkdir(parents=True)
            label.parent.mkdir(parents=True)
            source = Image.new("RGB", (row["width"], row["height"]), (index, index, index))
            source.save(image)
            prefix = f"{source.width}x{source.height}\0".encode()
            row.update(image_sha256=sha256_file(image), pixel_sha256=sha256(prefix + source.tobytes()))
            label.write_text(pose_label(row), encoding="utf-8")
            row["label_sha256"] = sha256_file(label)
            self.canonical.append(row)
        (self.base / "corners.yaml").write_text(pose_yaml(self.base), encoding="utf-8")
        write_json(self.base / "pose-provenance.json", {
            "schema": 1, "corner_order": CORNER_NAMES, "images": self.canonical,
        })
        self.extra = self.source_row("extra", "train", 9)
        extra_path = self.root / "extra.png"
        Image.new("RGB", (200, 100), (9, 9, 9)).save(extra_path)
        self.extra.pop("white_keys")
        self.extra.update(image=str(extra_path), image_sha256=sha256_file(extra_path), labelled_by="author-a")
        self.extra["review"] = {
            "status": "accepted", "reviewed_by": "reviewer-b", "reason": "checked native pixels",
            "label_scope": LABEL_SCOPE,
        }

    @staticmethod
    def source_row(key, split, index):
        return {
            **reference(key, split), "source_url": f"https://example.com/{index}",
            "author": f"source-{index}", "license": "CC0",
            "license_url": "https://creativecommons.org/publicdomain/zero/1.0/",
            "image_file": f"photos/{key}.png",
        }

    def approvals(self, row):
        ledger = self.root / "approvals.json"
        write_json(ledger, {"schema": 1, "images": [row]})
        return ledger

    def test_additions_are_train_only_and_keep_original_split_bytes(self):
        output = self.root / "extended"
        ledger = self.approvals(self.extra)
        self.assertEqual(augment(self.base, ledger, output), {"train": 2, "val": 1, "test": 1})
        rows = verify_pose_dataset(output)
        self.assertEqual(rows[:3], self.canonical)
        self.assertNotIn("white_keys", rows[-1])
        self.assertNotIn("test:", (output / "corners.yaml").read_text(encoding="utf-8"))
        manifest = json.loads((output / "pose-provenance.json").read_text(encoding="utf-8"))
        self.assertEqual(manifest["extra_training_approvals_sha256"], sha256_file(ledger))
        self.assertEqual(manifest["extra_training_image_count"], 1)

    def test_additions_cannot_include_held_out_images_or_source_groups(self):
        output = self.root / "extended"
        with self.assertRaisesRegex(ValueError, "only training photos"):
            augment(self.base, self.approvals({**self.extra, "split": "test", "image": "unread"}), output)
        with self.assertRaisesRegex(ValueError, "inconsistent splits"):
            augment(self.base, self.approvals({**self.extra, "group": "val"}), output)
        self.assertFalse(output.exists())

    def test_additions_cannot_reencode_a_validation_image_as_training(self):
        validation = next(row for row in self.canonical if row["split"] == "val")
        validation_path, _ = paths(self.base, validation)
        reencoded = self.root / "validation-with-metadata.png"
        metadata = PngImagePlugin.PngInfo()
        metadata.add_text("Comment", "Different file, same RGB pixels")
        with Image.open(validation_path) as source:
            source.save(reencoded, pnginfo=metadata, compress_level=1)
        reencoded_hash = sha256_file(reencoded)
        self.assertNotEqual(validation["image_sha256"], reencoded_hash)

        output = self.root / "extended"
        ledger = self.approvals({**self.extra, "image": str(reencoded), "image_sha256": reencoded_hash})
        with self.assertRaisesRegex(ValueError, "duplicate pixel_sha256"):
            augment(self.base, ledger, output)
        self.assertFalse(output.exists())

    def test_additions_require_independent_review_and_unchanged_bytes(self):
        output = self.root / "extended"
        review = {**self.extra["review"], "reviewed_by": "author-a"}
        with self.assertRaisesRegex(ValueError, "cannot approve"):
            augment(self.base, self.approvals({**self.extra, "review": review}), output)
        with self.assertRaisesRegex(ValueError, "checksum differs"):
            augment(self.base, self.approvals({**self.extra, "image_sha256": "changed"}), output)
        self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
