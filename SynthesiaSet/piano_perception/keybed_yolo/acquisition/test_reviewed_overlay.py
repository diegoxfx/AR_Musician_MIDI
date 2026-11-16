import copy
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from PIL import Image

from reviewed_overlay import refresh_reviews, snapshot
from dataset import read_json, sha256, sha256_file, write_json


class ReviewedOverlayTest(unittest.TestCase):
    def setUp(self):
        root = self.enterContext(tempfile.TemporaryDirectory())
        self.pilot = Path(root).resolve()
        sources = self.pilot / "sources"
        photos = sources / "photos"
        photos.mkdir(parents=True)
        self.base = self.pilot / "inventory-v2"
        self.dest = self.pilot / "inventory-v3"
        self.audit = self.pilot / "source-qa-v3"
        self.output_manifest = sources / "manifest-v3.json"
        for directory in ("images", "proposals/records"):
            (self.base / directory).mkdir(parents=True)
        self.manifest = sources / "manifest-v2.json"
        reviews, source_rows, inventory_rows, records = [], [], [], []
        for idx, key in enumerate(("alpha", "bravo", "charlie", "delta"), 1):
            original = photos / f"{key}.png"
            image = Image.new("RGB", (32, 32), (idx, 0, 0))
            image.save(original)
            split = "train" if key != "charlie" else "test"
            pixel_hash = sha256(b"32x32\0" + image.tobytes())
            source = {
                "id": key, "group": key, "split": split, "image_file": f"photos/{original.name}",
                "source_url": f"https://example.com/{key}", "author": "Photographer",
                "license": "CC0", "license_url": "https://example.com/cc0", "width": 32, "height": 32,
                "source_sha256": sha256_file(original), "pixel_sha256": pixel_hash,
            }
            normalized = self.base / "images" / original.name
            normalized.write_bytes(original.read_bytes())
            image_hash = sha256_file(normalized)
            source_rows.append(source)
            inventory_rows.append({**source, "image_sha256": image_hash})
            review = {"id": key, "image_sha256": image_hash, "status": "pending"}
            if key == "delta":
                review.update(status="rejected", reason="Previously reviewed.", reviewed_by="Test reviewer")
            reviews.append(review)
            record = {"id": key, "image_sha256": image_hash, "candidates": []}
            if key == "alpha":
                record["candidates"] = [
                    {"id": "bed", "class": "playable_keybed", "corners": [[2, 5], [28, 5], [28, 21], [2, 21]]},
                    {"id": "white", "class": "white_key", "polygon": [[3, 6], [10, 6], [10, 20], [3, 20]]},
                    {"id": "black", "class": "black_key", "polygon": [[11, 6], [14, 6], [14, 14], [11, 14]]},
                ]
            records.append(record)
            write_json(self.base / "proposals" / "records" / f"{key}.json", record)
        self.reviews_path = self.base / "reviews.json"
        write_json(self.reviews_path, {"schema": 1, "images": reviews})
        write_json(self.base / "inventory.json", {"schema": 1, "images": inventory_rows, "duplicates": []})
        write_json(self.manifest, {"schema": 1, "images": source_rows})
        settings = {"schema": 1, "image_sha256": {row["id"]: row["image_sha256"] for row in records}}
        write_json(self.base / "proposals" / "settings.json", settings)
        write_json(self.base / "proposals" / "index.json", {**settings, "complete": True, "images": records})

        proof = self.pilot / "private-proof"
        proof.mkdir()
        (proof / "sheet.jpg").write_bytes(b"fixture evidence")
        self.overlay_path = proof / "overlay.json"

        def change(row):
            return {
                "source_sha256": row["source_sha256"], "previous_group": row["group"],
                "previous_split": row["split"], "source_url": row["source_url"],
                "reason": "Source QA finding.", "evidence": ["sheet.jpg"],
            }

        self.overlay = {
            "schema": 1, "target_version": "v3", "base_manifest_sha256": sha256_file(self.manifest),
            "rejected": {"delta": change(source_rows[3])},
            "group_overrides": {
                "bravo": {**change(source_rows[1]), "group": "charlie", "split": "test"},
                "delta": {**change(source_rows[3]), "group": "charlie", "split": "test"},
            },
            "expected_active": {"images": 3, "groups": 2, "splits": {"train": 1, "test": 2}},
            "limits": [],
        }

    def create(self, expected_sha=None):
        write_json(self.overlay_path, self.overlay)
        return snapshot(self.manifest, self.base, self.overlay_path,
                        expected_sha or sha256_file(self.overlay_path), self.dest, self.audit)

    def assert_unpublished(self):
        self.assertFalse(self.dest.exists())
        self.assertFalse(self.audit.exists())
        self.assertFalse(self.output_manifest.exists())

    def accept_alpha_from_proposals(self):
        reviews = read_json(self.reviews_path)
        reviews["images"][0].update(
            status="accepted", reason="Late review of all visible instances.", reviewed_by="Base reviewer",
            keybed={"proposal": "bed"}, expected_key_count=2, expected_white_count=1,
            keys=[{"slot": 0, "class": "white_key", "proposal": "white"},
                  {"slot": 1, "class": "black_key", "proposal": "black"}],
        )
        write_json(self.reviews_path, reviews)

    def change_index_white_polygon(self, path):
        index = read_json(path)
        index["images"][0]["candidates"][1]["polygon"][0][0] += 1
        write_json(path, index)

    def test_reject_wrong_approved_overlay_and_base(self):
        with self.assertRaisesRegex(ValueError, "approved checksum"):
            self.create("0" * 64)
        self.assert_unpublished()
        self.overlay["base_manifest_sha256"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "base manifest checksum"):
            self.create()
        self.assert_unpublished()

    def test_reject_wrong_image_and_missing_evidence(self):
        original = copy.deepcopy(self.overlay)
        self.overlay["group_overrides"]["bravo"]["source_sha256"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "previous image hash"):
            self.create()
        self.assert_unpublished()
        self.overlay = original
        self.overlay["group_overrides"]["bravo"]["evidence"] = ["missing.jpg"]
        with self.assertRaisesRegex(ValueError, "missing or external local file"):
            self.create()
        self.assert_unpublished()

    def test_reject_conflicting_group(self):
        self.overlay["group_overrides"]["bravo"]["split"] = "train"
        with self.assertRaisesRegex(ValueError, "inconsistent splits"):
            self.create()
        self.assert_unpublished()

    def test_reject_review_hash_mismatch(self):
        reviews = read_json(self.reviews_path)
        reviews["images"][3]["image_sha256"] = "0" * 64
        write_json(self.reviews_path, reviews)
        with self.assertRaisesRegex(ValueError, "review does not match"):
            self.create()
        self.assert_unpublished()

    def test_snapshot_audits_excluded_reviews_and_preserves_bytes(self):
        hashes = {path: sha256_file(path) for path in (self.manifest, self.reviews_path, self.base / "inventory.json")}
        self.create()
        self.assertEqual({path: sha256_file(path) for path in hashes}, hashes)
        audit = read_json(self.audit / "source-audit.json")
        self.assertEqual(audit["excluded"]["delta"]["previous_explicit_review"]["status"], "rejected")
        self.assertFalse(audit["group_overrides"]["delta"]["applied_to_active"])
        self.assertTrue(audit["group_overrides"]["bravo"]["applied_to_active"])
        self.assertEqual(sha256_file(self.output_manifest), audit["target_manifest_sha256"])
        self.assertEqual(sha256_file(self.audit / "evidence" / "sheet.jpg"), audit["local_evidence_sha256"]["sheet.jpg"])
        for subdir, ext in (("images", ".png"), ("proposals/records", ".json")):
            for key in ("alpha", "bravo", "charlie"):
                path = Path(subdir) / (key + ext)
                self.assertEqual((self.base / path).read_bytes(), (self.dest / path).read_bytes())
            self.assertFalse((self.dest / subdir / ("delta" + ext)).exists())
        self.assertEqual([r["split"] for r in read_json(self.dest / "reviews.json")["images"]],
                         ["train", "test", "test"])
        with self.assertRaisesRegex(ValueError, "refusing to overwrite"):
            self.create()

    def test_refresh_transfers_latest_by_hash_without_overwriting_local(self):
        self.create()
        base = read_json(self.reviews_path)
        target_path = self.dest / "reviews.json"
        target = read_json(target_path)
        target["images"][1].update(status="rejected", reason="Newer local decision.", reviewed_by="Local reviewer")
        write_json(target_path, target)
        for row in base["images"][:2]:
            row.update(status="rejected", reason="Late base decision.", reviewed_by="Base reviewer")
        write_json(self.reviews_path, base)

        result = refresh_reviews(self.base, self.dest, self.audit)
        self.assertEqual(result, {"transferred": ["alpha"], "preserved_local_decisions": ["bravo"]})
        decisions = read_json(target_path)["images"]
        self.assertEqual(decisions[0]["reason"], "Late base decision.")
        self.assertEqual(decisions[1]["reason"], "Newer local decision.")
        self.assertEqual(decisions[1]["split"], "test")
        digest = sha256_file(target_path)
        base["images"][0]["image_sha256"] = "0" * 64
        write_json(self.reviews_path, base)
        with self.assertRaisesRegex(ValueError, "review does not match"):
            refresh_reviews(self.base, self.dest, self.audit)
        self.assertEqual(sha256_file(target_path), digest)

    def test_refresh_rejects_index_only_polygon_changes_in_either_workspace(self):
        self.create()
        self.accept_alpha_from_proposals()
        target_reviews = self.dest / "reviews.json"
        original_review_bytes = target_reviews.read_bytes()
        base_review_bytes = self.reviews_path.read_bytes()
        for workspace, message in (
            (self.base, "base SAM index checksum differs"),
            (self.dest, "alpha: target SAM index does not match the audited record"),
        ):
            with self.subTest(workspace=workspace.name):
                path = workspace / "proposals" / "index.json"
                record = workspace / "proposals" / "records" / "alpha.json"
                original_index_bytes = path.read_bytes()
                original_record_bytes = record.read_bytes()
                self.change_index_white_polygon(path)
                try:
                    with self.assertRaisesRegex(ValueError, message):
                        refresh_reviews(self.base, self.dest, self.audit)
                    self.assertEqual(target_reviews.read_bytes(), original_review_bytes)
                    self.assertEqual(self.reviews_path.read_bytes(), base_review_bytes)
                    self.assertEqual(record.read_bytes(), original_record_bytes)
                finally:
                    path.write_bytes(original_index_bytes)
        self.assertEqual(refresh_reviews(self.base, self.dest, self.audit)["transferred"], ["alpha"])
        self.assertEqual(read_json(target_reviews)["images"][0]["status"], "accepted")

    def test_refresh_rejects_concurrent_index_change_before_review_replace(self):
        self.create()
        self.accept_alpha_from_proposals()
        target_reviews = self.dest / "reviews.json"
        original_review_bytes = target_reviews.read_bytes()
        for workspace, label in ((self.base, "base"), (self.dest, "target")):
            with self.subTest(workspace=workspace.name):
                path = workspace / "proposals" / "index.json"
                original_index_bytes = path.read_bytes()

                def change_index_while_staging(staged_reviews, document):
                    write_json(staged_reviews, document)
                    self.change_index_white_polygon(path)

                try:
                    with patch("reviewed_overlay.write_json", side_effect=change_index_while_staging):
                        with self.assertRaisesRegex(ValueError, f"{label} SAM index changed during review transfer"):
                            refresh_reviews(self.base, self.dest, self.audit)
                    self.assertEqual(target_reviews.read_bytes(), original_review_bytes)
                finally:
                    path.write_bytes(original_index_bytes)


if __name__ == "__main__":
    unittest.main()
