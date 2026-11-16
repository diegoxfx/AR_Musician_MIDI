"""Synthetic checks for frozen corner comparison and source coordinate replay."""

from dataclasses import asdict
import json
from pathlib import Path
from tempfile import TemporaryDirectory
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import numpy as np
from PIL import Image

from corner_refiner import RefinementConfig
from corner_refiner_evaluate import ARTIFACTS, digest, evaluate, freeze, read, segmentation_seed, verify_plan
from evaluate import Frame


POINTS = [[15, 25], [170, 21], [175, 63], [12, 65]]


class FrozenComparisonTests(unittest.TestCase):
    def setUp(self):
        temporary = TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        packages = patch("corner_refiner_evaluate.versions", return_value={"test-environment": "1"})
        self.addCleanup(packages.stop)
        packages.start()
        self.paths = {name: self.root / name for name in ARTIFACTS}
        for name in ("segmentation", "pose", "refiner"):
            self.paths[name].write_bytes(name.encode())
        self.rows = []
        for index, key in enumerate(("first", "second")):
            image = self.root / f"{key}.png"
            Image.new("RGB", (200, 101), (index * 128, 32, 16)).save(image)
            self.rows.append({
                "id": key, "split": "development-diagnostic", "image": str(image),
                "image_sha256": digest(image), "width": 200, "height": 101, "corners": POINTS,
                "expected_white_count": 30,
            })
        self.write_ledger()
        refiner_source = Path(__file__).with_name("corner_refiner.py")
        self.paths["selection"].write_text(json.dumps({
            "seed_variant": "segmentation", "epoch": 5, "config": asdict(RefinementConfig()),
            "checkpoint_sha256": digest(self.paths["refiner"]), "source_code_sha256": digest(refiner_source),
        }), encoding="utf-8")
        self.plan = self.root / "plan.json"

    def write_ledger(self):
        self.paths["references"].write_text(json.dumps({"schema": 1, "images": self.rows}), encoding="utf-8")

    def test_freeze_binds_cohort_artifacts_sources_and_selection(self):
        with patch("corner_refiner_evaluate.load_models") as load:
            checksum = freeze(self.plan, "consumed-diagnostic", self.paths)
            load.assert_not_called()
        plan, _, rows, config = verify_plan(self.plan, checksum)
        self.assertEqual(plan["case_ids"], ["first", "second"])
        self.assertEqual(plan["refinement"]["seed_variant"], "segmentation")
        self.assertEqual(rows, self.rows)
        self.assertEqual(config, RefinementConfig())
        with self.assertRaisesRegex(ValueError, "already exists"):
            freeze(self.plan, "consumed-diagnostic", self.paths)
        self.paths["pose"].write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "frozen checksum mismatch"):
            verify_plan(self.plan, checksum)
        self.plan.write_text(self.plan.read_text(encoding="utf-8") + " ", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "frozen plan checksum mismatch"):
            verify_plan(self.plan, checksum)

    def test_changed_reference_is_refused_before_any_model_call(self):
        checksum = freeze(self.plan, "consumed-diagnostic", self.paths)
        Path(self.rows[-1]["image"]).write_bytes(b"changed")
        output = self.root / "results"
        with (patch("corner_refiner_evaluate.load_models") as load,
              self.assertRaisesRegex(ValueError, "second: reference image checksum mismatch")):
            evaluate(self.plan, checksum, output)
        load.assert_not_called()
        self.assertFalse(output.exists())

    def test_fresh_scope_requires_explicit_test_split(self):
        with self.assertRaisesRegex(ValueError, "split consistent with evaluation scope"):
            freeze(self.plan, "fresh-holdout", self.paths)
        self.assertFalse(self.plan.exists())

    def test_replay_uses_fixed_seed_and_scores_every_image_even_when_rejected(self):
        checksum = freeze(self.plan, "consumed-diagnostic", self.paths)
        output = self.root / "results"
        good_seed = {"quad": POINTS, "rejection": None}
        no_fit = {"quad": None, "rejection": "no usable fit"}
        keypoints = np.column_stack((POINTS, [0.9] * 4))
        seeds_used = []

        def refine(model, pixels, seed, config):
            self.assertEqual(pixels.shape, (101, 200, 3))
            self.assertIsInstance(config, RefinementConfig)
            seeds_used.append(seed)
            return {"quad": seed, "rejection": "no valid coarse seed" if seed is None else None}

        with (patch("corner_refiner_evaluate.load_models", return_value=(object(), object(), object())),
              patch("corner_refiner_evaluate.segmentation_seed", side_effect=(good_seed, no_fit)),
              patch("corner_refiner_evaluate.pose_seed",
                    return_value=({**good_seed, "object_confidence": 0.8}, keypoints)),
              patch("corner_refiner.refine_quad", side_effect=refine)):
            result = evaluate(self.plan, checksum, output, overlays=True)
        self.assertEqual(seeds_used, [POINTS, None])
        self.assertEqual(result["variants"]["pose"]["strict"], 2)
        self.assertEqual(result["variants"]["refined"]["strict"], 1)
        self.assertEqual(result["variants"]["refined"]["total"], 2)
        self.assertEqual(result["variants"]["refined"]["rejection_reasons"], {"no valid coarse seed": 1})
        self.assertEqual(result["cases"][0]["variants"]["pose"]["errors_white_widths_by_corner"]["back-left"], 0.0)
        self.assertEqual(read(output / "evaluation.json")["plan_sha256"], checksum)
        for name in ("segmentation", "pose", "refined"):
            self.assertEqual(sorted(path.name for path in (output / name).glob("*.png")),
                             ["first.png", "second.png"])
        with self.assertRaisesRegex(ValueError, "already exists"):
            evaluate(self.plan, checksum, output)


class SegmentationSourceGeometryTests(unittest.TestCase):
    def test_content_local_quad_is_mapped_back_after_padding_and_rounded_resize(self):
        frame = Frame(960, 960, 0, 237, 960, 485, 200, 101)
        content_local = frame.to_input(POINTS) - (frame.left, frame.top)
        session = SimpleNamespace(get_inputs=lambda: [SimpleNamespace(name="images")],
                                  run=lambda names, feed: [feed["images"]])
        fitted = SimpleNamespace(quad=content_local, method="geometry", confidence=.8, detection_index=0)
        candidate = SimpleNamespace(fit=fitted, roi_detection_index=1)
        with (patch("calibration.calibration_candidate", return_value=candidate),
              patch("evaluate.unpack_outputs", return_value=(None, None)),
              patch("evaluate.decode", return_value=[])):
            result = segmentation_seed(session, Image.new("RGB", (200, 101)))
        np.testing.assert_allclose(result["quad"], POINTS, rtol=0, atol=1e-12)
        self.assertIsNone(result["rejection"])


if __name__ == "__main__":
    unittest.main()
