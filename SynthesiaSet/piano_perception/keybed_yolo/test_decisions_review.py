import copy
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from urllib.error import HTTPError

import decisions_review as trial


def response(p=0.99, choice="pass"):
    return {"answers": [{"type": "choice", "name": name, "choice": choice, "confidence": 0.9,
                         "probabilities": [{"value": "pass", "probability": p},
                                           {"value": "fail", "probability": 1-p},
                                           {"value": "uncertain", "probability": 0}]}
                        for name in trial.CRITERIA]}


class DecisionsTrialTests(unittest.TestCase):
    def test_malformed_paid_responses_route_to_review_without_crashing(self):
        for broken in (None, [], {"answers": [None]}, {"answers": [{"name": [], "type": "choice"}]},
                       {"answers": [{"name": "instances", "type": "choice", "probabilities": None}]},
                       {"answers": [{"name": "instances", "type": "choice", "probabilities": [None]*3}]}):
            self.assertFalse(trial.parse_scores(broken)["valid"])

    def test_regional_criteria_need_every_region(self):
        result = response()
        result["answers"] = result["answers"][:2]
        for answer, name in zip(result["answers"], ("keybed", "region_1")):
            answer["name"] = name
        self.assertTrue(trial.parse_scores(result, ["keybed", "region_1"])["valid"])
        self.assertFalse(trial.parse_scores(result, ["keybed", "region_1", "region_2"])["valid"])

    def test_refusal_missing_and_duplicate_answers_require_review(self):
        good = response()
        self.assertTrue(trial.parse_scores(good)["valid"])
        for changed in (
            {"answers": good["answers"][:-1]},
            {"answers": good["answers"] + [good["answers"][0]]},
            {"answers": [{"type": "refusal", "name": "instances"}] + good["answers"][1:]},
        ):
            self.assertFalse(trial.parse_scores(changed)["valid"])
            self.assertFalse(trial.accepted(trial.parse_scores(changed), 0.9))

    def test_invalid_probabilities_fail_closed(self):
        for value in (float("nan"), float("inf"), True, -0.2, "0.9"):
            changed = response()
            changed["answers"][0]["probabilities"][0]["probability"] = value
            self.assertFalse(trial.parse_scores(changed)["valid"])

    def test_threshold_uses_bad_examples_and_can_disable_automatic_pass(self):
        def row(verdict, p):
            return {"reference_verdict": verdict, "scores": trial.parse_scores(response(p)), "origin": "natural"}
        threshold, _ = trial.choose_threshold([row("pass", 0.96), row("fail", 0.85)])
        self.assertEqual(threshold, 0.95)
        threshold, _ = trial.choose_threshold([row("pass", 0.90), row("fail", 0.99)])
        self.assertIsNone(threshold)

    def test_payload_blinds_reference_metadata_and_detects_changed_panels(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            image = root / "fake.png"
            image.write_bytes(b"test panel")
            case = {"case_id": "negative-natural", "reference_verdict": "fail", "stage": "verification",
                    "panels": [{"file": "fake.png", "sha256": trial.sha256_file(image)}]}
            prepared = {"prompt": {"instructions": trial.INSTRUCTIONS, "criteria": trial.CRITERIA, "choices": trial.CHOICES},
                        "manifest": {"secret_reference": "BAD MASK"}}
            text = str(trial.request_payload(prepared, case, root))
            self.assertNotIn("negative-natural", text)
            self.assertNotIn("verification", text)
            self.assertNotIn("BAD MASK", text)
            image.write_bytes(b"changed")
            with self.assertRaisesRegex(ValueError, "rendered image changed"):
                trial.request_payload(prepared, case, root)

    def test_groups_and_identical_images_cannot_cross_benchmark_stages(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "fixture"
            path.write_bytes(b"fixture")
            row = {"case_id": "first", "stage": "calibration", "group": "g", "image_id": "img", "reference_verdict": "pass"}
            for prefix in ("image", "label"):
                row[prefix+"_path"] = str(path)
                row[prefix+"_sha256"] = trial.sha256_file(path)
            second = {**row, "case_id": "second", "stage": "verification"}
            with self.assertRaisesRegex(ValueError, "leaks across stages"):
                trial.validate_manifest({"cases": [row, second]})
            second["group"] = "different"
            with self.assertRaisesRegex(ValueError, "leaks across stages"):
                trial.validate_manifest({"cases": [row, second]})

    def test_http_error_never_exposes_response_credential_fragment(self):
        error = HTTPError(trial.ENDPOINT, 401, "bad", {}, io.BytesIO(b"never-print-this-key-fragment"))
        with patch.object(trial, "build_opener") as opener:
            opener.return_value.open.side_effect = error
            with self.assertRaises(RuntimeError) as context:
                trial.call_api({}, "fixture-key-not-a-real-secret")
        self.assertEqual(str(context.exception), "Decisions HTTP 401; no automatic retry was made")

    def test_key_loader_rejects_empty_and_duplicate_without_echo(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / ".env"
            for text in ("OPENAI_API_KEY=\n", "OPENAI_API_KEY=test1\nOPENAI_API_KEY=test2\n"):
                path.write_text(text)
                with self.assertRaisesRegex(ValueError, "one nonempty"):
                    trial.load_key(path)

    def test_billed_malformed_answer_is_saved_and_never_repeated(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "image.png").write_bytes(b"fixture panel")
            prepared = {"prompt": {"instructions": trial.INSTRUCTIONS, "criteria": trial.CRITERIA, "choices": trial.CHOICES},
                        "cases": [{"case_id": "one", "panels": [{"file": "image.png", "sha256": trial.sha256_file(root/"image.png")}]}]}
            trial.atomic_json(root/"prepared.json", prepared)
            paid = {"answers": [None], "model": trial.MODEL, "usage": {"input_tokens": 123}, "elapsed_seconds": 1}
            with patch.object(trial, "load_key", return_value="fixture"), patch.object(trial, "call_api", return_value=paid) as api:
                trial.run(root, root/"unused.env")
                trial.run(root, root/"unused.env")
                self.assertEqual(api.call_count, 1)
            saved = trial.read_json(root/"results/one.json")
            self.assertFalse(saved["scores"]["valid"])
            self.assertEqual(saved["response"]["usage"]["input_tokens"], 123)
            (root/"attempts/uncertain.json").write_text('{}')
            with patch.object(trial, "call_api") as api:
                with self.assertRaisesRegex(ValueError, "prior request"):
                    trial.run(root, root/"unused.env")
                api.assert_not_called()


if __name__ == "__main__":
    unittest.main()
