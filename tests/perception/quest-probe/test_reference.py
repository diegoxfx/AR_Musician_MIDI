"""Regression tests for Quest detection and mask parity."""

from argparse import Namespace
from contextlib import redirect_stdout
import io
import json
from pathlib import Path
import struct
import tempfile
import unittest

import numpy as np

from reference import CLASSES, PINNED_ULTRALYTICS, compare, json_file, mask_for, operating_point, sha, verified_run, write_float


class ReferenceParityTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        self.reference, self.actual = root / "reference", root / "results/device"
        self.reference.mkdir()
        self.actual.mkdir(parents=True)
        self.report = root / "parity.json"
        self.case = dict(id="sample", left=0, top=0, contentWidth=8, contentHeight=8)
        self.config = dict(height=8, width=8, rows=2, columns=8, protoChannels=2, protoHeight=8,
                           protoWidth=8, confidence=0.25, cases=[self.case])
        self.rows = np.float32([[1, 1, 7, 7, 0.8, 1, 1, 0], [1, 1, 7, 7, 0.8, 1, 0, 1]])
        self.proto = np.full((2, 8, 8), -1, dtype=np.float32)
        self.proto[0, :, :4] = 1
        self.proto[1, :, 4:] = 1
        det_path, proto_path = self.reference / "sample.det.f32", self.reference / "sample.proto.f32"
        write_float(det_path, self.rows)
        write_float(proto_path, self.proto)
        write_float(self.reference / "sample.f32", np.zeros((1, 3, 8, 8)))
        json_file(self.reference / "fixture.json", self.config)
        json_file(self.reference / "provenance.json", dict(scope="test fixture", modelSha256="fixture",
            confidence=0.25, fixtureConfigSha256=sha(self.reference / "fixture.json"), cases=[
            dict(id="sample", sourceWidth=8, sourceHeight=8, ortDetSha256=sha(det_path),
                 inputSha256=sha(self.reference / "sample.f32"), ortProtoSha256=sha(proto_path)),
        ]))

    def write_receipt(self):
        root = self.actual.parent
        json_file(root / "run.json", dict(self.config, backend="CPU", repeats=1, sliceBudgetMs=4))
        json_file(self.actual / "benchmark.json", dict(backend="CPU", interrupted=False,
                  inference=[dict(id="sample", cycle=0, cold=True)]))
        json_file(root / "device-summary.json", dict(outcome="Executed", backend="CPU", modelSha256="fixture",
                  fixtureProvenanceSha256=sha(self.reference / "provenance.json"),
                  fixtureConfigSha256=sha(self.reference / "fixture.json"), runConfigSha256=sha(root / "run.json"),
                  resultFiles=[dict(path=p.relative_to(root).as_posix(), sha256=sha(p))
                               for p in root.rglob("*") if p.is_file() and p.name != "device-summary.json"]))

    def compare_outputs(self, rows):
        write_float(self.actual / "sample.det.f32", rows)
        write_float(self.actual / "sample.proto.f32", self.proto)
        with (self.actual / "sample.masks").open("wb") as output:
            for index, row in enumerate(rows):
                mask = mask_for(row, self.proto, self.config, self.case)
                output.write(struct.pack("<i", index))
                output.write(np.packbits(mask.ravel(), bitorder="little").tobytes())
        self.write_receipt()
        with redirect_stdout(io.StringIO()):
            code = compare(Namespace(reference=self.reference, actual=self.actual, report=self.report))
        return code, json.loads(self.report.read_text(encoding="utf-8"))

    def test_equal_boxes_and_scores_pair_by_mask_in_either_row_order(self):
        for order in ([0, 1], [1, 0]):
            with self.subTest(order=order):
                code, report = self.compare_outputs(self.rows[order])
                self.assertEqual(code, 0)
                self.assertEqual(report["nonemptyComparedMasks"], 2)
                matches = report["cases"][0]["matches"]
                self.assertEqual([(pair["ortRow"], pair["sentisRow"]) for pair in matches],
                                 [(i, order.index(i)) for i in (0, 1)])
                self.assertTrue(all(pair["maskIoU"] == 1.0 for pair in matches))

    def test_device_mask_drift_fails_even_if_decoder_matches_its_own_rows(self):
        code, report = self.compare_outputs(self.rows[[0, 0]])
        case = report["cases"][0]
        self.assertEqual(code, 1)
        self.assertFalse(report["passParity"])
        self.assertEqual(case["worstDecoderIoU"], 1.0)
        self.assertEqual(case["unmatchedOrt"], [1])
        self.assertEqual(len(case["unmatchedSentis"]), 1)

    def test_detached_tensors_cannot_establish_device_parity(self):
        self.compare_outputs(self.rows)
        (self.actual.parent / "device-summary.json").unlink()
        with self.assertRaises(FileNotFoundError):
            compare(Namespace(reference=self.reference, actual=self.actual, report=self.report))

    def test_changed_operating_point_and_stale_model_are_rejected(self):
        self.compare_outputs(self.rows)
        root = self.actual.parent
        receipt_path = root / "device-summary.json"
        original = json.loads(receipt_path.read_text())
        for field, value in (("modelSha256", "different-model"), ("fixtureProvenanceSha256", "other-fixture")):
            with self.subTest(field=field):
                json_file(receipt_path, dict(original, **{field: value}))
                with self.assertRaisesRegex(ValueError, "different fixture"):
                    verified_run(root, self.reference)
        json_file(receipt_path, original)
        config = json.loads((root / "run.json").read_text())
        config["confidence"] = 0.1
        json_file(root / "run.json", config)
        changed = dict(original, runConfigSha256=sha(root / "run.json"))
        changed["resultFiles"] = [dict(item, sha256=sha(root / item["path"])) for item in original["resultFiles"]]
        json_file(receipt_path, changed)
        with self.assertRaisesRegex(ValueError, "different fixture"):
            verified_run(root, self.reference)

    def test_fixture_config_and_result_tampering_fail_closed(self):
        self.compare_outputs(self.rows)
        json_file(self.reference / "fixture.json", dict(self.config, confidence=0.1))
        with self.assertRaisesRegex(ValueError, "fixture configuration hash"):
            verified_run(self.actual.parent, self.reference)
        json_file(self.reference / "fixture.json", self.config)
        with (self.actual / "sample.det.f32").open("ab") as target:
            target.write(b"changed")
        with self.assertRaisesRegex(ValueError, "device result hash"):
            verified_run(self.actual.parent, self.reference)

    def test_missing_receipt_entries_and_interrupted_runs_fail(self):
        self.compare_outputs(self.rows)
        receipt_path = self.actual.parent / "device-summary.json"
        original = json.loads(receipt_path.read_text())
        missing = dict(original, resultFiles=[item for item in original["resultFiles"] if item["path"] != "device/sample.masks"])
        json_file(receipt_path, missing)
        with self.assertRaisesRegex(ValueError, "does not bind"):
            verified_run(self.actual.parent)
        json_file(receipt_path, dict(original, outcome="Interrupted"))
        with self.assertRaisesRegex(ValueError, "did not complete"):
            verified_run(self.actual.parent)


class OperatingPointTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        model = root / "keybed.onnx"
        model.write_bytes(b"bound-test-model")
        json_file(root / "provenance.json", {"test": "reviewed"})
        json_file(root / "export-report.json", {"onnx_sha256": sha(model), "weights_sha256": "weights"})
        self.binding = dict(shape=[480, 640], dataset_provenance_sha256=sha(root / "provenance.json"),
                            weights_sha256="weights", onnx_sha256=sha(model), classes=list(CLASSES),
                            ultralytics=PINNED_ULTRALYTICS)
        selection = root / "selection.json"
        json_file(selection, dict(split="val", binding=self.binding, confidence=0.1))
        self.args = Namespace(dataset=root, model=model, selection=selection, confidence=None)

    def test_reviewed_confidence_is_taken_from_frozen_selection(self):
        cutoff, provenance = operating_point(self.args, 480, 640)
        self.assertEqual(cutoff, 0.1)
        self.assertEqual(provenance["validationSelectionSha256"], sha(self.args.selection))
        self.args.confidence = 0.25
        with self.assertRaisesRegex(ValueError, "forbid"):
            operating_point(self.args, 480, 640)
        self.args.confidence = None
        self.args.selection = None
        with self.assertRaisesRegex(ValueError, "require"):
            operating_point(self.args, 480, 640)

    def test_reviewed_selection_must_match_every_binding_and_be_validation(self):
        for key, value in (("shape", [960, 960]), ("dataset_provenance_sha256", "other"),
                           ("weights_sha256", "other"), ("onnx_sha256", "other"), ("classes", ["wrong"])):
            with self.subTest(key=key):
                json_file(self.args.selection, dict(split="val", binding=dict(self.binding, **{key: value}), confidence=0.1))
                with self.assertRaisesRegex(ValueError, "same data"):
                    operating_point(self.args, 480, 640)
        json_file(self.args.selection, dict(split="test", binding=self.binding, confidence=0.1))
        with self.assertRaisesRegex(ValueError, "validation selection"):
            operating_point(self.args, 480, 640)

    def test_smoke_confidence_remains_explicitly_separate(self):
        self.args.dataset = None
        with self.assertRaisesRegex(ValueError, "requires a reviewed"):
            operating_point(self.args, 480, 640)
        self.args.selection = None
        self.assertEqual(operating_point(self.args, 480, 640)[0], 0.25)
        self.args.confidence = 0.4
        self.assertEqual(operating_point(self.args, 480, 640)[0], 0.4)
        self.args.confidence = float("nan")
        with self.assertRaisesRegex(ValueError, "finite"):
            operating_point(self.args, 480, 640)


if __name__ == "__main__":
    unittest.main()
