"""Apply an evidence-reviewed overlay to a frozen local keybed source snapshot."""

import argparse
from collections import Counter
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sys
import tempfile
from urllib.parse import urlsplit

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from dataset import SPLITS, indexed, resolve_instances, sha256_file, validate_sources, write_json
from review import review_lock


SHA256 = re.compile(r"[a-f0-9]{64}\Z")
FINAL = {"accepted", "negative", "rejected"}


def json_bytes(path):
    data = path.read_bytes()
    return json.loads(data), hashlib.sha256(data).hexdigest()


def sha_field(value, name):
    if not isinstance(value, str) or not SHA256.fullmatch(value):
        raise ValueError(f"invalid SHA-256: {name}")
    return value


def content_sha(row):
    data = json.dumps(row, sort_keys=True, ensure_ascii=False, separators=(",", ":")).encode()
    return hashlib.sha256(data).hexdigest()


def local_file(directory, name):
    path = (directory / name).resolve()
    if Path(name).is_absolute() or not path.is_relative_to(directory.resolve()) or not path.is_file():
        raise ValueError(f"missing or external local file: {name}")
    return path


def source_overlay(base_manifest, overlay, base_sha, evidence_dir):
    if (base_manifest.get("schema") != 1 or overlay.get("schema") != 1
            or overlay.get("base_manifest_sha256") != base_sha):
        raise ValueError("overlay does not match the base manifest checksum or schema")
    version = overlay["target_version"]
    if not isinstance(version, str) or not re.fullmatch(r"v[1-9][0-9]*", version):
        raise ValueError("invalid overlay target version")
    sources = validate_sources(base_manifest["images"])
    rejected, overrides = overlay["rejected"], overlay["group_overrides"]
    local_evidence = set()
    for changes in (rejected, overrides):
        for key, change in changes.items():
            row = sources.get(key)
            expected = {
                "source_sha256": change["source_sha256"], "group": change["previous_group"],
                "split": change["previous_split"], "source_url": change["source_url"],
            }
            sha_field(expected["source_sha256"], key)
            if not row or any(row.get(field) != value for field, value in expected.items()):
                raise ValueError(f"{key}: overlay does not match the previous image hash, group, split, or URL")
            if not isinstance(change["reason"], str) or not change["reason"].strip():
                raise ValueError(f"{key}: overlay needs a reason")
            evidence = change["evidence"]
            if not isinstance(evidence, list) or not evidence:
                raise ValueError(f"{key}: overlay needs evidence")
            for item in evidence:
                if not isinstance(item, str) or not item.strip():
                    raise ValueError(f"{key}: invalid evidence reference")
                url = urlsplit(item)
                if url.scheme == "https" and url.netloc:
                    continue
                if url.scheme or url.netloc:
                    raise ValueError(f"{key}: evidence URL must use HTTPS")
                local_file(evidence_dir, item)
                local_evidence.add(item)
    for key, change in overrides.items():
        if (not isinstance(change["group"], str) or not change["group"].strip()
                or change["split"] not in SPLITS):
            raise ValueError(f"{key}: invalid group or split override")
    active = validate_sources([
        {**row, **{field: overrides[key][field] for field in ("group", "split")}}
        if key in overrides else row
        for key, row in sources.items() if key not in rejected
    ])
    summary = {
        "images": len(active), "groups": len({row["group"] for row in active.values()}),
        "splits": dict(Counter(row["split"] for row in active.values())),
    }
    if summary != overlay["expected_active"]:
        raise ValueError(f"corrected source counts differ from the approved overlay: {summary}")
    return sources, active, summary, local_evidence


def verify_inventory(inventory, sources, manifest_dir):
    rows = validate_sources(inventory["images"])
    if inventory.get("schema") != 1 or rows.keys() != sources.keys() or inventory["duplicates"]:
        raise ValueError("base inventory is not a one-to-one normalization of the base manifest")
    normalized_fields = {"width", "height", "pixel_sha256"}
    pixels = set()
    for key, row in rows.items():
        original = sources[key]
        if any(row.get(field) != value for field, value in original.items() if field not in normalized_fields):
            raise ValueError(f"{key}: base inventory metadata disagrees with the base manifest")
        path = local_file(manifest_dir, original["image_file"])
        if sha256_file(path) != sha_field(original["source_sha256"], f"{key} source"):
            raise ValueError(f"{key}: original source checksum changed")
        sha_field(row["image_sha256"], f"{key} normalized image")
        pixel_sha = sha_field(row["pixel_sha256"], f"{key} decoded pixels")
        if pixel_sha in pixels:
            raise ValueError(f"{key}: repeated normalized pixel checksum")
        pixels.add(pixel_sha)
    return rows


def copy_checked(source, target, expected_sha):
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, target)
    if sha256_file(target) != expected_sha:
        raise ValueError(f"copied file checksum changed: {source.name}")


def proposal_snapshot(base_dir, target, old_rows, active):
    directory = base_dir / "proposals"
    settings, settings_sha = json_bytes(directory / "settings.json")
    index, index_sha = json_bytes(directory / "index.json")
    indexed_records = indexed(index["images"])
    if (not index.get("complete") or {k: v for k, v in index.items() if k not in {"complete", "images"}} != settings
            or indexed_records.keys() != old_rows.keys()
            or settings.get("image_sha256") != {key: row["image_sha256"] for key, row in old_rows.items()}):
        raise ValueError("base SAM index or settings disagree with the base inventory")
    checksums = {}
    for key, record in indexed_records.items():
        path = directory / "records" / f"{key}.json"
        standalone, digest = json_bytes(path)
        if standalone != record or record.get("image_sha256") != old_rows[key]["image_sha256"]:
            raise ValueError(f"{key}: SAM record does not match the index and normalized image")
        if key in active:
            copy_checked(path, target / "records" / path.name, digest)
            checksums[key] = digest
    new_settings = {**settings, "image_sha256": {key: old_rows[key]["image_sha256"] for key in active}}
    write_json(target / "settings.json", new_settings)
    write_json(target / "index.json", {
        **new_settings, "complete": True, "images": [indexed_records[key] for key in active],
    })
    return indexed_records, {
        "base_settings_sha256": settings_sha, "base_index_sha256": index_sha,
        "active_record_sha256": checksums,
    }


def migrate_reviews(document, old_rows, new_rows, proposals):
    previous = indexed(document["images"])
    if document.get("schema") != 1 or previous.keys() != old_rows.keys():
        raise ValueError("base review IDs or schema do not match the inventory")
    reviews, excluded = {}, {}
    for key, decision in previous.items():
        old = old_rows[key]
        new = new_rows.get(key)
        status = decision["status"]
        if decision.get("image_sha256") != old["image_sha256"]:
            raise ValueError(f"{key}: review does not match the base normalized image checksum")
        if decision.get("split", old["split"]) != old["split"]:
            raise ValueError(f"{key}: review split disagrees with its base inventory")
        if status in FINAL:
            if not all(isinstance(decision.get(field), str) and decision[field].strip()
                       for field in ("reviewed_by", "reason")):
                raise ValueError(f"{key}: explicit review needs a reviewer and reason")
            proposal = proposals.get(key)
            if proposal and proposal.get("image_sha256") != old["image_sha256"]:
                raise ValueError(f"{key}: review proposal does not match the base image checksum")
            try:
                resolve_instances(old, decision, proposal)
            except ValueError as error:
                raise ValueError(f"{key}: invalid explicit base review: {error}") from error
        elif status != "pending" or "keybed" in decision or "keys" in decision:
            raise ValueError(f"{key}: invalid pending base review")
        if new is None:
            if status in FINAL:
                excluded[key] = decision
        else:
            if any(new[field] != old[field] for field in ("image_sha256", "pixel_sha256")):
                raise ValueError(f"{key}: normalized image or decoded pixels changed during review transfer")
            reviews[key] = {**decision, "split": new["split"]}
    return reviews, excluded


def snapshot(base_manifest, base_dir, overlay_path, expected_sha, destination, qa_dir):
    overlay, overlay_sha = json_bytes(overlay_path)
    if overlay_sha != sha_field(expected_sha, "approved overlay"):
        raise ValueError("overlay does not match the approved checksum")
    manifest, manifest_sha = json_bytes(base_manifest)
    evidence_dir = overlay_path.parent.resolve()
    sources, active, summary, evidence = source_overlay(manifest, overlay, manifest_sha, evidence_dir)
    version = overlay["target_version"]
    manifest_out = base_manifest.with_name(f"manifest-{version}.json")
    for output in (destination, qa_dir, manifest_out):
        if os.path.lexists(output):
            raise ValueError(f"refusing to overwrite snapshot output: {output}")
    if destination.parent != qa_dir.parent or manifest_out.parent.parent != destination.parent:
        raise ValueError("expected inventory and QA directories beside the sources directory")
    if len({destination, qa_dir, base_dir, manifest_out.parent}) != 4:
        raise ValueError("snapshot paths must be distinct")
    if destination.parent.is_relative_to(evidence_dir):
        raise ValueError("the overlay must be in a dedicated QA directory")
    inventory, inventory_sha = json_bytes(base_dir / "inventory.json")
    old_rows = verify_inventory(inventory, sources, base_manifest.parent)
    new_rows = {
        key: {**old_rows[key], **{field: active[key][field] for field in ("group", "split")}} for key in active
    }
    with tempfile.TemporaryDirectory(prefix=f".source-{version}-", dir=destination.parent) as staging:
        staging = Path(staging)
        staged, audit, staged_manifest = staging / "inventory", staging / "qa", staging / manifest_out.name
        audit.mkdir()
        for key, row in old_rows.items():
            path = base_dir / "images" / f"{key}.png"
            if key in active:
                copy_checked(path, staged / "images" / path.name, row["image_sha256"])
            elif sha256_file(path) != row["image_sha256"]:
                raise ValueError(f"{key}: excluded image checksum changed")
        proposals, proposal_audit = proposal_snapshot(base_dir, staged / "proposals", old_rows, active)
        write_json(staged_manifest, {
            **manifest, "images": list(active.values()),
            "source_review": "Source overlay applied. Complete instance labels require explicit image review.",
            "reviewed_source_overlay": {"base_manifest_sha256": manifest_sha, "sha256": overlay_sha},
        })
        manifest_out_sha = sha256_file(staged_manifest)
        write_json(staged / "inventory.json", {**inventory, "images": list(new_rows.values())})
        copy_checked(overlay_path, audit / overlay_path.name, overlay_sha)
        evidence_checksums = {}
        # The overlay lists minimum decision evidence; keep the full adjacent QA
        # packet too, including contact sheets and the scripts that produced it.
        packet = {
            path.relative_to(evidence_dir).as_posix() for path in evidence_dir.rglob("*")
            if path.is_file() and path != overlay_path
        }
        for name in sorted(evidence | packet):
            path = local_file(evidence_dir, name)
            digest = sha256_file(path)
            copy_checked(path, audit / "evidence" / name, digest)
            evidence_checksums[name] = digest
        review_path = base_dir / "reviews.json"
        base_reviews, reviews_sha = json_bytes(review_path)
        transferred, excluded = migrate_reviews(base_reviews, old_rows, new_rows, proposals)
        write_json(staged / "reviews.json", {
            "schema": 1, "images": list(transferred.values()),
            "source_overlay_transfer": {
                "base_reviews_sha256": reviews_sha,
                "last_transferred_sha256": {key: content_sha(row) for key, row in transferred.items()},
                "excluded_explicit_reviews": excluded,
            },
        })
        status_counts = dict(Counter(row["status"] for row in transferred.values()))
        write_json(audit / "source-audit.json", {
            "schema": 1, "target_version": version, "summary": summary,
            "overlay_sha256": overlay_sha, "local_evidence_sha256": evidence_checksums,
            "local_evidence_directory": "evidence",
            "base_manifest_sha256": manifest_sha, "base_inventory_sha256": inventory_sha,
            "base_reviews_sha256": reviews_sha, "target_manifest_sha256": manifest_out_sha,
            "target_inventory_sha256": sha256_file(staged / "inventory.json"),
            "snapshot_reviews_sha256": sha256_file(staged / "reviews.json"),
            "review_status_at_snapshot": status_counts, "proposals": proposal_audit,
            "excluded": {
                key: {"decision": entry, "source": sources[key], "normalized_image": old_rows[key],
                      "previous_explicit_review": excluded.get(key)}
                for key, entry in overlay["rejected"].items()
            },
            "group_overrides": {
                key: {**entry, "applied_to_active": key in active}
                for key, entry in overlay["group_overrides"].items()
            },
            "limits": overlay["limits"],
        })
        if sha256_file(review_path) != reviews_sha:
            raise ValueError("base reviews changed during snapshot; retry at a quiet review boundary")
        publish_snapshot(staged, audit, staged_manifest, destination, qa_dir, manifest_out)
    return {"manifest_sha256": manifest_out_sha, "active": summary, "reviews": status_counts}


def publish_snapshot(staged, audit, manifest, destination, qa_dir, manifest_out):
    # Publish complete directories with one rename each, then the manifest as the commit marker.
    if not all(output == output.resolve() and output.parent == destination.parent
               for output in (destination, qa_dir)):
        raise ValueError("snapshot directories must resolve inside the pilot directory")
    if not all(source == source.resolve() and source.parent.parent == destination.parent
               for source in (staged, audit)):
        raise ValueError("staged snapshot directories must resolve inside the pilot directory")
    lock = destination.with_name(f".{destination.name}.snapshot.lock")
    fd = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
    moved = []
    try:
        for source, output in ((staged, destination), (audit, qa_dir)):
            if os.path.lexists(output):
                raise ValueError(f"refusing to overwrite snapshot output: {output}")
            source.rename(output)
            moved.append((output, source))
        os.link(manifest, manifest_out)
    except BaseException:
        for output, source in reversed(moved):
            output.rename(source)
        raise
    finally:
        os.close(fd)
        lock.unlink()


def verify_review_index(workspace, rows, record_checksums, label, expected_index_sha=None):
    proposal_dir = workspace / "proposals"
    index_path = proposal_dir / "index.json"
    document, index_sha = json_bytes(index_path)
    if expected_index_sha is not None and index_sha != expected_index_sha:
        raise ValueError(f"{label} SAM index checksum differs from the audited snapshot")
    proposals = indexed(document["images"])
    image_hashes = {key: row["image_sha256"] for key, row in rows.items()}
    if (document.get("complete") is not True or proposals.keys() != rows.keys()
            or document.get("image_sha256") != image_hashes):
        raise ValueError(f"{label} SAM index does not match the inventory")
    for key, expected in record_checksums.items():
        path = proposal_dir / "records" / f"{key}.json"
        record, digest = json_bytes(path)
        if digest != expected:
            raise ValueError(f"{key}: {label} SAM record checksum differs from the audited snapshot")
        if proposals[key] != record or record.get("image_sha256") != image_hashes[key]:
            raise ValueError(f"{key}: {label} SAM index does not match the audited record")
    return proposals, index_path, index_sha


def refresh_reviews(base_dir, destination, audit_dir):
    audit, _ = json_bytes(audit_dir / "source-audit.json")
    base_inventory, base_sha = json_bytes(base_dir / "inventory.json")
    inventory, target_sha = json_bytes(destination / "inventory.json")
    if base_sha != audit["base_inventory_sha256"] or target_sha != audit["target_inventory_sha256"]:
        raise ValueError("review transfer inventories do not match the audited snapshot")
    old_rows, new_rows = indexed(base_inventory["images"]), indexed(inventory["images"])
    proposal_audit = audit["proposals"]
    record_checksums = proposal_audit["active_record_sha256"]
    if record_checksums.keys() != new_rows.keys() or not new_rows.keys() <= old_rows.keys():
        raise ValueError("audited SAM record IDs do not match the corrected inventory")
    with review_lock(destination):
        base_path = base_dir / "reviews.json"
        base_reviews, reviews_sha = json_bytes(base_path)
        path = destination / "reviews.json"
        current, _ = json_bytes(path)
        current_rows = indexed(current["images"])
        provenance = current["source_overlay_transfer"]
        baseline = provenance["last_transferred_sha256"]
        if current_rows.keys() != new_rows.keys() or baseline.keys() != new_rows.keys():
            raise ValueError("target review IDs do not match the corrected inventory")
        # Packing resolves proposal references through index.json. Validate both
        # indexes against the audited per-image records before resolving reviews.
        proposals, base_index_path, base_index_sha = verify_review_index(
            base_dir, old_rows, record_checksums, "base", proposal_audit["base_index_sha256"],
        )
        _, target_index_path, target_index_sha = verify_review_index(
            destination, new_rows, record_checksums, "target",
        )
        decisions, excluded = migrate_reviews(base_reviews, old_rows, new_rows, proposals)
        changed, preserved = [], []
        for key, desired in decisions.items():
            decision = current_rows[key]
            if decision.get("image_sha256") != new_rows[key]["image_sha256"]:
                raise ValueError(f"{key}: target review checksum does not match its normalized image")
            desired_hash = content_sha(desired)
            if desired_hash == baseline[key] or desired["status"] not in FINAL:
                continue
            if content_sha(decision) != baseline[key]:
                preserved.append(key)
                continue
            if sha256_file(destination / "images" / f"{key}.png") != desired["image_sha256"]:
                raise ValueError(f"{key}: target normalized image changed")
            current_rows[key] = desired
            baseline[key] = desired_hash
            changed.append(key)
        if not changed and reviews_sha == provenance["base_reviews_sha256"]:
            return {"transferred": changed, "preserved_local_decisions": preserved}
        provenance["base_reviews_sha256"] = reviews_sha
        provenance["excluded_explicit_reviews"].update(excluded)
        with tempfile.TemporaryDirectory(dir=destination) as staging:
            target = Path(staging) / "reviews.json"
            write_json(target, {**current, "images": list(current_rows.values())})
            if sha256_file(base_path) != reviews_sha:
                raise ValueError("base reviews changed during transfer; retry at a quiet review boundary")
            for label, path_to_index, digest in (
                ("base", base_index_path, base_index_sha),
                ("target", target_index_path, target_index_sha),
            ):
                if sha256_file(path_to_index) != digest:
                    raise ValueError(f"{label} SAM index changed during review transfer")
            target.replace(path)
        return {"transferred": changed, "preserved_local_decisions": preserved}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    subcommands = parser.add_subparsers(dest="command", required=True)
    create = subcommands.add_parser("snapshot")
    create.add_argument("base_manifest", type=Path)
    create.add_argument("base_inventory", type=Path)
    create.add_argument("overlay", type=Path)
    create.add_argument("overlay_sha256")
    create.add_argument("new_inventory", type=Path)
    create.add_argument("audit_directory", type=Path)
    refresh = subcommands.add_parser("refresh-reviews")
    refresh.add_argument("base_inventory", type=Path)
    refresh.add_argument("new_inventory", type=Path)
    refresh.add_argument("audit_directory", type=Path)
    args = parser.parse_args()
    if args.command == "snapshot":
        result = snapshot(
            args.base_manifest.resolve(), args.base_inventory.resolve(), args.overlay.resolve(),
            args.overlay_sha256, args.new_inventory.resolve(), args.audit_directory.resolve(),
        )
    else:
        result = refresh_reviews(args.base_inventory.resolve(), args.new_inventory.resolve(), args.audit_directory.resolve())
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
