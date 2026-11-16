# Wikimedia Commons photo sourcing

Keep the catalog, review decisions, photos, and contact sheets in the private pilot directory. The catalog records the file page, author, published license, original URL and dimensions, and discovery categories. The downloader records the URL and SHA-256 of the actual bytes used. A Commons thumbnail retains the framing of its source photo at lower resolution.

```powershell
$photos = Join-Path $env:LOCALAPPDATA "AR-MusicianBuild/output/redesign/keybed-yolo/pilot-v1/sources"
python -B acquisition/commons.py $photos discover --depth 1 --limit 34
python -B acquisition/commons.py $photos previews --limit 330
python -B acquisition/commons.py $photos sheets --stop 330
```

Use additional `discover --roots "PortaTone" "Yamaha synthesizers" "Roland synthesizers" "Korg synthesizers"` calls to broaden the catalog. Discovery appends new files to the local catalog and caches the category API responses. Reuse the same catalog when recording decisions by number. Previews and contact sheets are local review aids.

In a text file, write each selected contact-sheet number followed by `full` or `challenge`. An optional third field overrides the default grouping by photographer. Use the same group for photos of the same piano, capture session, or source series. For example, `174 full korg-rk100s-2014-namm` and `1361 full korg-rk100s-2014-namm` assign different views of a single exhibition instrument to one group. Assign these numbers using the current catalog. Select genuine photographs with a single complete, visibly separable keybed as `full`. Keep cropped, obstructed, and multiple-keyboard photos as challenge cases if they add value. Select one version of each shot and retain varied views of an instrument only where they add useful perspective or lighting variation.

```powershell
python -B acquisition/commons.py $photos collect (Join-Path $photos "review.txt")
python -B acquisition/commons.py $photos publish
python -B dataset.py acquire (Join-Path $photos "manifest.json") (Join-Path $photos "../inventory")
```

`publish` removes repeated decoded pixels and holds out additional close perceptual matches, including horizontal mirrors. It records those pairs in `duplicate-review.json`. Visual review and conservative grouping still matter for crops, alternate encodings, and multi-angle series. Splits use the selected full-photo groups. Preserve the published manifest when starting an annotation run since expanding or regrouping the source catalog can change split assignments.

For an extension of a frozen pilot, publish with `--version v2 --base-manifest <manifest-v1.json>`. Existing photo hashes, groups and split assignments must match the base manifest. Acquire the extension into a new workspace for label review.

Source review establishes photo suitability for annotation. Complete instance labels, keyboard counts and geometry still need review before packing for training. Preserve frozen source snapshots and record later suitability failures as rejected label-review decisions. Keep `challenge-manifest.json` as an excluded diagnostic collection: it can share photographers, capture sessions and derivative photos with training, so it must not supply independent evaluation metrics. A future held-out challenge benchmark needs its own source-derivative and group audit before use.

Before freezing the training dataset, apply verified corrections to source suitability or cross-split capture groups in a new snapshot. An overlay must identify its base manifest by SHA-256, identify each changed photo by source SHA-256 and prior group and split, and cite review evidence. Keep the original overlay and its local evidence together in a dedicated private directory. Pause writes to the base reviews while taking the snapshot, then make all subsequent decisions in the new inventory.

```powershell
$pilot = Split-Path $photos
python -B acquisition/reviewed_overlay.py snapshot (Join-Path $photos "manifest-v2.json") (Join-Path $pilot "source-inventory-v2") $overlay $approvedOverlaySha256 (Join-Path $pilot "source-inventory-v3") (Join-Path $pilot "source-qa-v3")
```

The command requires a fully normalized, local base inventory with complete per-image SAM records. It checks source, normalized image, proposal and review identities, refuses to overwrite a snapshot, and publishes the new manifest in the same directory as the base. The QA directory keeps the approved overlay, evidence hashes, every source exclusion with its prior explicit review (if one exists), and all group overrides, including overrides for excluded photos. Snapshot counts measure source candidates; separately rejected label reviews remain excluded from model training.

If a delayed decision reaches the base after the snapshot, run `reviewed_overlay.py refresh-reviews <base-inventory> <new-inventory> <audit-directory>` at a quiet review boundary. The transfer checks the normalized image and SAM hashes and uses the corrected split. It reports any local review it preserved instead of replacing it.
