from contextlib import nullcontext
import importlib.util
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

import numpy as np
from PIL import Image

from dataset import acquire, pack, read_json, resolve_instances, write_json
from fit import SEGMENTATION_TRAINING, describe_output, main as fit_main, train, verify_dataset
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
            row["split"] = "test"
            write_json(review_path, reviews)
            with self.assertRaisesRegex(ValueError, "review split differs"):
                pack(workspace, root / "dataset")
            row["split"] = "train"
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
            model._smart_load.return_value = object

            def check_training(**options):
                self.assertFalse(cache.exists())
                self.assertFalse(options["overlap_mask"])
                (root / "run").mkdir()

            model.train.side_effect = check_training
            weights = root / "stock.pt"
            weights.write_bytes(b"fixture")
            write_json(root / "provenance.json", {"images": []})
            accepted = [(None, None, {"split": split, "group": split, "review": {"status": "accepted"}})
                        for split in ("train", "val", "test")]
            with (patch("fit.verify_dataset", return_value=accepted), patch("fit.ResourceBudget") as budget,
                  patch("fit.yolo", return_value=lambda *_args, **_opts: model)):
                budget.return_value.cpu = {"cpu_0_excluded": True}
                budget.return_value.duty = 0.7
                budget.return_value.gpu_target = 0.8
                train(root, weights, root / "run", "cpu", 640, 1, 1, 42, minimum_positives=3)
            model.train.assert_called_once()
            provenance = read_json(root / "run/training-provenance.json")
            self.assertEqual(provenance["minimum_positive_images_requested"], 3)
            self.assertEqual(provenance["verified_positive_images_by_split"], {"train": 1, "val": 1, "test": 1})

    def test_export_cli_keeps_test_untouched_unless_explicitly_selected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            weights = root / "stock.pt"
            weights.write_bytes(b"fixture")
            command = ["fit.py", "export", str(root), str(weights), str(root / "export")]
            with patch("sys.argv", command), patch("fit.export") as export:
                fit_main()
                self.assertEqual(export.call_args.args[-1], "val")
            with patch("sys.argv", command + ["--smoke-split", "test"]), patch("fit.export") as export:
                fit_main()
                self.assertEqual(export.call_args.args[-1], "test")

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
    def test_long_key_retains_narrow_stem_and_rounded_front_lip(self):
        import cv2

        mask = np.zeros((720, 120), dtype=np.uint8)
        cv2.rectangle(mask, (38, 15), (53, 180), 1, -1)
        cv2.rectangle(mask, (18, 175), (95, 685), 1, -1)
        cv2.rectangle(mask, (22, 685), (91, 690), 1, -1)
        cv2.circle(mask, (23, 684), 7, 1, -1)
        cv2.circle(mask, (90, 684), 7, 1, -1)
        polygon, iou = key_polygon(mask)
        reconstructed = np.zeros_like(mask)
        cv2.fillPoly(reconstructed, [np.asarray(polygon, dtype=np.int32)], 1)
        self.assertGreater(iou, 0.995)
        self.assertLessEqual(np.count_nonzero(mask & (1 - reconstructed)), 5)

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

    @unittest.skipUnless(importlib.util.find_spec("cv2"), "OpenCV is needed for key proposal assembly")
    def test_tiled_dedup_preserves_evidence_and_reports_white_key_gaps(self):
        from review import assemble_candidates
        from tiled_proposals import project, rectify, tiles

        image = Image.new("RGB", (100, 60))
        corners = [[10, 10], [90, 10], [90, 40], [10, 40]]
        rectified, forward, bounds = rectify(image, corners)
        planned = list(tiles(rectified, bounds, count=3))
        self.assertEqual(planned[0][0], 0)
        self.assertEqual(planned[-1][0] + planned[-1][1].width, rectified.width)
        self.assertGreater(planned[0][1].width, planned[1][0])
        np.testing.assert_allclose(project(project(corners, forward), np.linalg.inv(forward)), corners)

        candidates = [
            {"id": f"white-{index}", "class": "white_key", "score": 0.9,
             "polygon": [[x, 11], [x + 9, 11], [x + 9, 39], [x, 39]]}
            for index, x in enumerate((11, 21, 31, 51, 61, 71, 80))
        ]
        candidates += [{**candidates[0], "id": "white-duplicate", "score": 0.6}]
        keys, diagnostics = assemble_candidates(candidates, forward, bounds)
        self.assertEqual([key["proposal"] for key in keys], [key["id"] for key in candidates[:-1]])
        self.assertEqual(keys[0]["polygon"], candidates[0]["polygon"])
        self.assertEqual(diagnostics["deduplicated"], 1)
        self.assertTrue(any("typical spacings" in warning for warning in diagnostics["warnings"]))

    @unittest.skipUnless(importlib.util.find_spec("cv2"), "OpenCV is needed for the review workflow")
    def test_drafts_do_not_mark_images_reviewed_and_invalid_batch_is_atomic(self):
        from review import apply_decisions, prepare

        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            workspace = root / "inventory"
            acquire(fixture(root, ["red", "green"], ["a", "b"], ["train", "val"]), workspace, 320)
            review_path = workspace / "reviews.json"
            pending = review_path.read_bytes()
            pending_second = read_json(review_path)["images"][1]
            rows = read_json(workspace / "inventory.json")["images"]
            proposals = workspace / "proposals"
            proposals.mkdir()
            write_json(proposals / "index.json", {"schema": 1, "images": [
                {"id": rows[0]["id"], "image_sha256": rows[0]["image_sha256"],
                 "keybed_proposal": "bed", "candidates": [
                     {"id": "bed", "class": "playable_keybed", "corners": QUAD},
                     *[{**key, "id": f"key-{key['slot']}", "score": 0.95} for key in KEYS],
                 ]},
            ]})
            prepare(workspace, proposals, root / "drafts", filled=True)
            self.assertEqual(review_path.read_bytes(), pending)
            self.assertTrue((root / "drafts" / f"{rows[0]['id']}.png").is_file())
            first = {"id": rows[0]["id"], "image_sha256": rows[0]["image_sha256"], **positive_review()}
            invalid = {"id": rows[1]["id"], "image_sha256": "stale", **positive_review()}
            with self.assertRaisesRegex(ValueError, "does not match the image checksum"):
                apply_decisions(workspace, [first, invalid])
            self.assertEqual(review_path.read_bytes(), pending)
            stale_split = {**invalid, "image_sha256": rows[1]["image_sha256"], "split": "test"}
            with self.assertRaisesRegex(ValueError, "decision split differs"):
                apply_decisions(workspace, [first, stale_split])
            self.assertEqual(review_path.read_bytes(), pending)
            apply_decisions(workspace, [first])
            self.assertEqual(read_json(review_path)["images"], [first, pending_second])
            corrected_split = {**stale_split, "split": rows[1]["split"]}
            apply_decisions(workspace, [corrected_split])
            self.assertEqual(read_json(review_path)["images"], [first, corrected_split])

    @unittest.skipUnless(importlib.util.find_spec("cv2"), "OpenCV is needed for tiled proposals")
    def test_tiled_proposals_resume_after_one_image_without_repeating_it(self):
        from tiled_proposals import run

        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            workspace = root / "inventory"
            acquire(fixture(root, ["red", "green"], ["a", "b"], ["train", "val"]), workspace, 320)
            checkpoint = root / "checkpoint"
            checkpoint.mkdir()
            (checkpoint / "config.json").write_text("{}", encoding="utf-8")
            output, stop = root / "proposals", root / "stop"
            mock_torch = SimpleNamespace(__version__="test", inference_mode=nullcontext)
            options = (workspace, checkpoint, output, "cpu", 672, 0.25, 0.35, None, set(), None)

            def pause_after_first(*_):
                stop.touch()
                return {"candidates": [], "tile_count": 0}

            with (patch.dict(sys.modules, {"torch": mock_torch}),
                  patch("tiled_proposals.version", return_value="test"),
                  patch("tiled_proposals.Sam"),
                  patch("tiled_proposals.propose_image", side_effect=pause_after_first) as propose):
                run(*options, False, stop)
                self.assertFalse(read_json(output / "index.json")["complete"])
                self.assertEqual(propose.call_count, 1)
                stop.unlink()
                run(*options, True, stop)
                index = read_json(output / "index.json")
                self.assertTrue(index["complete"])
                self.assertEqual([row["id"] for row in index["images"]], ["test-0", "test-1"])
                self.assertEqual(propose.call_count, 2)
                seeded = root / "seeded"
                new_options = (workspace, checkpoint, seeded, *options[3:])
                run(*new_options, False, stop, seeds=[output])
                copied = read_json(seeded / "index.json")
                self.assertTrue(copied["complete"])
                self.assertTrue(all(row.get("seed_provenance") for row in copied["images"]))
                self.assertEqual(propose.call_count, 2)
                # An old contour policy cannot silently reuse cached masks.
                settings = read_json(output / "settings.json")
                del settings["key_polygon_max_error_px"]
                write_json(output / "settings.json", settings)
                with self.assertRaisesRegex(ValueError, "resume settings differ"):
                    run(*options, True, stop)
                other = (workspace, checkpoint, root / "old-seed", *options[3:])
                with self.assertRaisesRegex(ValueError, "seed proposal settings do not match"):
                    run(*other, False, stop, seeds=[output])
                self.assertEqual(propose.call_count, 2)


if __name__ == "__main__":
    unittest.main()
