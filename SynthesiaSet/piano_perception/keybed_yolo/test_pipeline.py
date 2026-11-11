import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch

import numpy as np
from PIL import Image

from dataset import acquire, pack, read_json, resolve_instances, write_json
from fit import SEGMENTATION_TRAINING, describe_output, train, verify_dataset
from geometry import checked_polygon, checked_quad, segment_rows
from propose import key_polygon, quad_from_mask

QUAD = [[6, 8], [90, 10], [93, 40], [4, 42]]
KEYS = [
    {"slot": 0, "class": "white_key", "polygon": [[7, 10], [40, 10], [40, 39], [7, 39]]},
    {"slot": 1, "class": "black_key", "polygon": [[38, 10], [55, 10], [55, 26], [38, 26]]},
    {"slot": 2, "class": "white_key", "polygon": [[54, 11], [88, 11], [88, 38], [54, 38]]},
]


def positive_review(**overrides):
    return {"status": "accepted", "reviewed_by": "fixture", "reason": "complete fixture",
            "keybed": {"corners": QUAD}, "keys": KEYS, "expected_key_count": 3, "expected_white_count": 2,
            **overrides}


def fixture(root, colors, groups, splits):
    manifest = root / "sources.json"
    records = []
    for index, (color, group, split) in enumerate(zip(colors, groups, splits, strict=True)):
        key = f"test-{index}"
        filename = f"{key}.png"
        Image.new("RGB", (100, 60), color).save(root / filename)
        records.append({"id": key, "image_file": filename, "group": group, "split": split,
                        "source_url": f"fixture:{key}", "author": "Test fixture",
                        "license": "CC0", "license_url": "https://creativecommons.org/publicdomain/zero/1.0/"})
    write_json(manifest, {"schema": 1, "images": records})
    return manifest


class PipelineTests(unittest.TestCase):
    def test_segment_encoding_and_rejecting_bad_corners(self):
        quad = checked_quad(QUAD, 100, 60)
        fields = segment_rows([{"class": "playable_keybed", "polygon": quad}], 100, 60).split()
        self.assertEqual(len(fields), 9)
        self.assertEqual(fields[:3], ["0", "0.06000000", "0.13333333"])
        for invalid in (QUAD[::-1], [[0, 8], *QUAD[1:]], [QUAD[0], QUAD[2], QUAD[1], QUAD[3]]):
            with self.assertRaises(ValueError):
                checked_quad(invalid, 100, 60)

    def test_key_polygons_can_be_concave_but_not_self_crossing(self):
        l_shape = [[3, 5], [8, 5], [8, 15], [15, 15], [15, 25], [3, 25]]
        self.assertEqual(checked_polygon(l_shape, 40, 40), [tuple(point) for point in l_shape])
        with self.assertRaisesRegex(ValueError, "edges cross"):
            checked_polygon([[3, 3], [20, 20], [4, 20], [16, 3]], 40, 40)

    def test_source_group_and_duplicate_cannot_leak_across_splits(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            output = root / "inventory"
            manifest = fixture(root, ["red", "blue"], ["same", "same"], ["train", "val"])
            with self.assertRaisesRegex(ValueError, "inconsistent splits"):
                acquire(manifest, output, 320)
            manifest = fixture(root, ["red", "red"], ["first", "second"], ["train", "val"])
            with self.assertRaisesRegex(ValueError, "duplicate pixels"):
                acquire(manifest, output, 320)
            self.assertFalse(output.exists())

    def test_review_required_and_dataset_is_checked(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            workspace, output = root / "inventory", root / "dataset"
            manifest = fixture(root, ["red", "blue", "green", "white"], ["a", "b", "c", "d"],
                               ["train", "val", "test", "test"])
            acquire(manifest, workspace, 320)
            with self.assertRaisesRegex(ValueError, "review is pending"):
                pack(workspace, output)
            review_path = workspace / "reviews.json"
            reviews = read_json(review_path)
            for review in reviews["images"][:3]:
                review.update(positive_review())
            reviews["images"][3].update(status="negative", reason="no keybed", reviewed_by="fixture",
                                        contains_visible_target=False)
            write_json(review_path, reviews)
            pack(workspace, output)
            self.assertEqual(len(verify_dataset(output)), 4)
            self.assertEqual((output / "labels/test/test-3.txt").read_text(), "")
            self.assertEqual(
                [line.split()[0] for line in (output / "labels/test/test-2.txt").read_text().splitlines()],
                ["0", "1", "2", "1"],
            )
            nested = output / "images/train/nested"
            nested.mkdir()
            Image.new("RGB", (100, 60), "purple").save(nested / "unreviewed.png")
            with self.assertRaisesRegex(ValueError, "images: packed contents differ"):
                verify_dataset(output)
            (nested / "unreviewed.png").unlink()
            (output / "labels/test/test-2.txt").write_text("", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "differs from reviewed provenance"):
                verify_dataset(output)

    def test_stale_review_and_ineligible_proposal_fail(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            workspace = root / "inventory"
            acquire(fixture(root, ["red"], ["a"], ["train"]), workspace, 320)
            review_path = workspace / "reviews.json"
            reviews = read_json(review_path)
            row = reviews["images"][0]
            image_hash = row["image_sha256"]
            row.update(positive_review(keybed={"proposal": "p1-1"}, image_sha256="stale"))
            write_json(review_path, reviews)
            with self.assertRaisesRegex(ValueError, "review does not match"):
                pack(workspace, root / "dataset")
            row["image_sha256"] = image_hash
            write_json(review_path, reviews)
            (workspace / "proposals").mkdir()
            write_json(workspace / "proposals/index.json", {"images": [
                {"id": row["id"], "image_sha256": image_hash,
                 "candidates": [{"id": "p1-1", "class": "playable_keybed", "rejection": "clipped"}]}
            ]})
            with self.assertRaisesRegex(ValueError, "expected four ordered corners"):
                pack(workspace, root / "dataset")

    def test_overlapping_keybed_and_keys_are_preserved_but_duplicate_or_missing_keys_fail(self):
        row = {"width": 100, "height": 60}
        self.assertFalse(SEGMENTATION_TRAINING["overlap_mask"])
        self.assertEqual(len(resolve_instances(row, positive_review(), None)), 4)
        duplicate = {**KEYS[0], "slot": 2}
        with self.assertRaisesRegex(ValueError, "duplicate polygons"):
            resolve_instances(row, positive_review(keys=[*KEYS[:2], duplicate]), None)
        with self.assertRaisesRegex(ValueError, "do not cover the complete"):
            resolve_instances(row, positive_review(keys=KEYS[:2]), None)
        with self.assertRaisesRegex(ValueError, "confirmation of no visible target"):
            resolve_instances(row, {"status": "negative"}, None)

    def test_training_refreshes_derived_caches_and_preserves_overlapping_masks(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "labels").mkdir()
            cache = root / "labels/train.cache"
            cache.write_bytes(b"stale")
            model = Mock()

            def check_training(**options):
                self.assertFalse(cache.exists())
                self.assertFalse(options["overlap_mask"])
                (root / "run").mkdir()

            model.train.side_effect = check_training
            weights = root / "stock.pt"
            weights.write_bytes(b"fixture")
            write_json(root / "provenance.json", {"images": []})
            with patch("fit.verify_dataset"), patch("fit.yolo", return_value=lambda *_args, **_opts: model):
                train(root, weights, root / "run", "cpu", 640, 1, 1, 42)
            model.train.assert_called_once()

    @unittest.skipUnless(importlib.util.find_spec("cv2"), "OpenCV is needed for mask conversion")
    def test_mask_quad_rejects_clipping(self):
        mask = np.zeros((60, 100), dtype=bool)
        mask[10:30, 8:90] = True
        quad, iou = quad_from_mask(mask)
        self.assertEqual(quad, [(8, 10), (89, 10), (89, 29), (8, 29)])
        self.assertEqual(iou, 1)
        self.assertEqual(key_polygon(mask)[1], 1)
        mask[10, 0] = True
        with self.assertRaisesRegex(ValueError, "touches an image edge"):
            quad_from_mask(mask)

    @unittest.skipUnless(importlib.util.find_spec("cv2"), "OpenCV is needed for mask conversion")
    def test_angled_keybed_uses_long_edges_instead_of_y_sorted_vertices(self):
        import cv2

        corners = [[15, 12], [157, 58], [150, 75], [8, 30]]
        mask = np.zeros((100, 180), dtype=np.uint8)
        cv2.fillConvexPoly(mask, np.asarray(corners, dtype=np.int32), 1)
        quad, iou = quad_from_mask(mask.astype(bool))
        np.testing.assert_allclose(quad, corners, atol=2)
        self.assertGreater(iou, 0.9)
        with self.assertRaisesRegex(ValueError, "must be the long keybed edges"):
            checked_quad(corners[-1:] + corners[:-1], 180, 100)

    def test_both_export_layouts_require_mask_prototypes(self):
        prototype = np.empty((1, 32, 120, 160))
        end_to_end = np.empty((1, 300, 38))
        raw = np.empty((1, 39, 6300))
        self.assertTrue(describe_output([end_to_end, prototype]).startswith("end_to_end"))
        self.assertTrue(describe_output([prototype, raw]).startswith("raw_channels"))
        with self.assertRaisesRegex(ValueError, "expected detections and mask prototypes"):
            describe_output([end_to_end])


if __name__ == "__main__":
    unittest.main()
