# Train a keybed and individual-key detector

Train one **YOLO26n-seg** model on real piano photos. Its three instance classes are the complete playable surface, one white key, and one black key. At calibration, fit an ordered quadrilateral to the complete surface mask. Use the individual key masks to refine each key's position and assign physical slots from left to right. The ordered corners are derived from the surface mask. Their order is back-left, back-right, front-right, front-left from the player's viewpoint. The segmentation model produces masks and scores. Key press detection uses a separate input source.

YOLO26n-seg is the first Quest candidate. Ultralytics reports 2.7 million parameters and 9.3 GFLOPs for the fused model at 640 pixels. Its NMS-free export produces detections and mask prototypes. Start with a 640 × 480 inference input and benchmark a higher-resolution rectified crop, such as 1024 × 256, if the individual masks lose narrow keys. YOLO11n-seg provides an export fallback. Measure both mask quality and the device cost before changing the production detector. [YOLO26 results](https://docs.ultralytics.com/models/yolo26), [Exported output formats](https://docs.ultralytics.com/guides/end2end-detection)

## Real photos and group splits

The pilot collected 500 distinct real candidate photographs from Wikimedia Commons. Independent source and instance review qualified 300 complete positive photographs: 245 train, 27 validation and 28 reserved test, in 196, 24 and 23 source groups respectively. The frozen labels contain 300 keybeds, 9,149 white keys and 6,318 black keys. The other 200 candidates are explicitly excluded; pending or rejected keyboards are not background negatives. The local snapshot retains licenses, authors, source and pixel hashes, decisions and the exact independent approval ledger (`e079ae0388c6bd8d8509da7d76d5bc55c35781f86c5c303e602334b8d18e1214`).

- The initial twelve-photo Wikimedia Commons manifest records the photographer, license, source link, exact download URL and SHA-256. It remains an acquisition/review example, separate from the larger pilot result above.
- Budget **300–500 distinct real images** for the first learning and review pilot. If the pilot shows useful signal, plan **2,000–5,000 distinct high-resolution real photos** for a first robust per-key dataset. These are planning estimates. Measure gains as you add varied photos and keep a fresh holdout. One CT-S200 image has up to 61 key instances, but all 61 share the same lighting, perspective and scene.
- Cover different keyboards, backgrounds, lighting and player's-eye angles. Assign one split per piano and capture session or source series. Keep near duplicates, crops, mirrored copies and video bursts together. The inventory detects exact normalized pixel duplicates. Group assignment must capture related photos whose pixels differ.
- When available, reserve real Quest images in independent calibration sessions for domain checks and adaptation. A 20–30% target-domain share is a planning target for the later dataset. The online pilot can start with internet photos.
- Compare complete-keybed corner error in white-key widths, per-key mask precision and recall, and missing, extra or misordered keys. Evaluate false accepts on clipped keyboards and non-instruments separately. Ultralytics' broad recommendation is at least 1,500 images and 10,000 instances per class for robust deployment. It is not a measured threshold for this task. [Data collection guidance](https://docs.ultralytics.com/guides/data-collection-and-annotation)

Additional source leads for manual license, image and duplicate checks include the [Piano Original collection](https://universe.roboflow.com/hworkspace-efglx/piano-original), [Piano Tiles collection](https://universe.roboflow.com/kfirworkspace/piano-tiles-ovqgs) and [Beethoven Relative Position collection](https://universe.roboflow.com/mjs-personal/beethoven-relative-position). Published frame counts can include many shots of one instrument. The existing annotations may need replacement.

## Local workflow

Use Python 3.12 with a compatible PyTorch installation. In a dedicated environment install `ultralytics==8.4.210`, `onnx==1.20.1` and `onnxruntime==1.23.2`. For SAM proposals install `transformers==5.17.0` and use a local SAM 3.1 checkpoint such as [unsloth/sam3.1](https://huggingface.co/unsloth/sam3.1). The observed run used the local checkpoint at revision `cd35389`. The proposal inventory stores actual checkpoint file hashes.

Run these commands in this directory. Use a private output directory and keep images, overlays, model weights and logs outside Git.

```powershell
$root = Join-Path $env:LOCALAPPDATA "AR-MusicianBuild/output/keybed-yolo"
$work = Join-Path $root "commons-inventory"
$data = Join-Path $root "reviewed-dataset"
$sam = "C:/path/to/local/sam3.1/checkpoint"
python -B dataset.py acquire commons_pilot.json $work
python -B propose.py $work $sam
```

The source manifest can also reference an `image_file` relative to the manifest. Supply `source_url`, `author`, `license`, `license_url`, `group`, and `split` for every item. For acquired photos, use `image_url` and optionally pin `source_sha256`. Acquisition corrects EXIF orientation, saves RGB PNGs, verifies checksums and writes a fixed inventory. Create a fresh workspace when changing the source or split manifest.

### Review every instance

Inspect the original image, the labeled masks under `proposals/`, and `proposals/index.json`. SAM can return the case, combine multiple keys, omit keys, duplicate keys or miss a corner. It can also assign masks to the wrong white/black prompt. The geometric check rejects many bad shapes and leaves the semantic decision to review.

Key contours cap polygon simplification error at 0.75 pixels in the mask raster. This prevents long, narrow keys from losing stems or front lips through a perimeter-scaled tolerance. Proposal settings record this cap; tiled runs reject cached seeds or resumes made with the old contour policy. The cap preserves SAM's mask, but does not correct a semantically wrong mask: native-image review is still required.

In a two-photo test at a 672-pixel SAM input, full-image proposals yielded 38 white and 20 black candidates for the Casio CTK-519 and 37 white and 25 black for the Yamaha PSR-220. Both keyboards have 36 white and 25 black keys. With visually corrected keybed corners and five overlapping perspective-corrected crops, SAM proposed 46 white and 31 black masks per image. Deduplication reduced each to 36 white and 25 black. In the rectified raster, the largest pairwise overlap covered 2.6% of one black mask on the Casio and 1.4% on the Yamaha. This was a two-image proposal check using manual corner guides. [Casio source photo](https://commons.wikimedia.org/wiki/File:Keyboard519.JPG), [Yamaha source photo](https://commons.wikimedia.org/wiki/File:Yamaha_PSR-220_(1).jpg)

For a larger review batch, start with a fresh inventory and use overlapping crops. The process fits a candidate keybed, rectifies it with a margin, and runs SAM on each tile. It maps each visible contour back to the original image. The review draft merges likely duplicates and flags unusual spacing, missing-key patterns and substantial mask overlaps for inspection. An optional `--guides` JSON file can supply image-hash-bound corner corrections.

```powershell
$drafts = Join-Path $root "review-drafts"
python -B tiled_proposals.py $work $sam --budget-log (Join-Path $root "sam-budget.jsonl")
python -B review.py prepare $work $drafts --filled
python -B review.py edit $work $drafts
```

Open the editor at the localhost address it prints. Use **Rectified comparison** to check visible seams, endpoints, key order and coverage at native resolution. Use the editor canvas to move vertices, delete a duplicate or add the polygon of a visible missing key. Enter the counts verified from the photo and record the reviewer and decision. The editor writes the decision to `reviews.json` only after validation. The CLI alternative is `python -B review.py apply $work <decisions.json>`, with explicitly reviewed decisions in the same schema.

The tiled command saves one proposal record per image. Review sheets can be built from completed records while inference continues. Use `--stop-file <path>` to pause between images once that path exists. Remove the stop marker and rerun with identical proposal settings and `--resume` to continue. An expanded inventory can reuse exact-image records with `--seed <prior-proposals-directory>` when model settings match. For a small experiment, use repeated `--image-id` options and an alternate `--output` directory. Use the default `proposals` directory for the review and pack workflow. The local CUDA workflow uses `nvidia-smi` for cooperative load checks and cooldowns between crops.

With `review.py prepare --white-corners`, the review draft can also fit a quadrilateral to the proposed white-key contours. Verify the two outer ends, the rear edge where the white keys emerge, and the seam where the front of the white playing surface turns downward. A contour hull can include a visible vertical key face, so adjust the guide to the top playing plane where needed.

Edit the generated `reviews.json`. Keep its `id` and `image_sha256`. Record `reviewed_by` and `reason` on every reviewed item.

- An `accepted` image must show the complete playable surface and individual keys. Set `keybed` to an object with four ordered `corners` or a reviewed `proposal` ID. Record `expected_key_count` and `expected_white_count`. The `keys` array needs one item for each zero-based physical slot, with `slot`, `class` and a reviewed `proposal` ID or `polygon` of pixel-space vertices. If SAM missed or distorted a key, provide a corrected polygon. An explicit polygon overrides the proposed outline while preserving the proposal ID as provenance.
- This pilot packs complete keyboards only. Mark partial, ambiguous, crowded or poorly exposed scenes `rejected`. Keep partial scenes in an excluded diagnostic collection, which may share capture groups or derivatives with training and must not supply independent evaluation metrics. They can later support individual-key training under an explicit partial-label policy that annotates every visible key.
- Use `negative` with `contains_visible_target: false` for reviewed backgrounds with zero target objects. Do not use an empty label file when a keyboard or its keys are visible.

Targets are physical instruments and their playable keys. A clearly printed keyboard on a box, poster, or device screen is a background distractor, not a second physical target. Preserve the original photograph and record the distractor explicitly in the review. A complete printed illustration does not make a cropped physical instrument eligible for this complete-keyboard pilot. Keep reverse-color instruments pending until the physical key roles and corresponding class assignments have been reviewed explicitly; brightness alone cannot determine those roles.

The packer checks complete slots, the white/black counts, edge clipping and nearly identical key polygons. The order and semantic accuracy still require review. Keybed polygons intentionally overlap the per-key masks. Training sets `overlap_mask=False` to preserve these masks separately. The per-key contour can be concave to follow the white key around the raised black key. Disconnected fragments and large mask holes need correction or rejection. [YOLO segmentation dataset format](https://docs.ultralytics.com/datasets/segment)

White-key contours must cover the visible upper playing surface, with one consistent boundary convention across the photo. The vertical front face is not required: uniformly annotated upper surfaces are valid. A short rounded lip may be included, but a tile boundary must not abruptly switch between full deep front faces and upper surfaces. Correct such transitions against the observed seams. Do not blindly clip every key to the fitted quadrilateral: lens distortion, rounded endpoints and a slightly imperfect quad can otherwise remove real playing surface. For raised black keys, follow their visible silhouette and inspect the physical order separately; sorting mixed white/black centroids can invert adjacent slots in oblique views.

```powershell
python -B dataset.py pack $work $data
```

The packer includes review overlays, normalized YOLO labels, provenance and a local `keybed.yaml`. It requires reviewed positive examples in every split. It refuses to overwrite the packed directory.

An explicit split saved in a review must match the current source inventory. Both decision application and packing reject stale assignments, including drafts created before a source-series correction. Regenerate that draft against the corrected inventory before accepting it. Legacy reviews without an explicit split inherit the inventory assignment.

### Optional Decisions annotation-review experiment

`decisions_review.py` runs a bounded, blinded review experiment using the [OpenAI Decisions API](https://developers.openai.com/api/docs/guides/decisions). It sends source/overlay comparison panels to `gpt-6-luna` and requests typed judgments. It never writes training review decisions. The user must authorize a credential source before live use; pass its local path explicitly, and keep it outside tracked files.

```powershell
python -B decisions_review.py prepare <benchmark-manifest.json> <private-trial-directory>
python -B decisions_review.py run <private-trial-directory> --env-file <approved-env-file> --case <calibration-case-id>
# Run all calibration cases before selecting; repeated --case options select a subset.
python -B decisions_review.py select <private-trial-directory>
# Then run the untouched verification cases with the same prepared prompt.
python -B decisions_review.py report <private-trial-directory>
python -B -m unittest test_decisions_review -v
```

The manifest holds checksum-bound source images, candidate labels and independently reviewed reference verdicts. References, filenames, origin, stage, expected counts and reviewer identities never enter the request. Source groups and image IDs cannot cross calibration and verification. Responses are saved individually, checked against the prepared snapshot, and reused on resume. Refused or malformed answers require review. A request receipt prevents automatic paid retries when completion or token usage is unknown. Only calibration selects the probability threshold; verification checks that frozen rule. `--mode regional` prepares a separate experiment with image-specific questions for individual regions.

The 2026-11-11 diagnostic used 48 label cases over 32 real photos: each stage contained 12 accepted annotations, six observed SAM errors, and six deliberately altered annotations. The altered labels were isolated benchmark controls, never training labels. The global-prompt calibration rejected all 12 valid annotations. On the 24 untouched verification cases, its raw overall answer accepted five of 12 valid annotations and rejected ten of 12 invalid ones (seven false rejections, two false acceptances). No useful zero-false-acceptance threshold existed on calibration, so the frozen routing rule sent all cases for review. A four-case regional calibration ablation rejected both valid examples and incorrectly accepted the natural missing-white-key example. This experiment does **not** justify automatically accepting training labels or inspecting only the API's rejections.

The 52 requests used 138,073 uncached input tokens and about 64 seconds of summed request latency. At the documented Decisions base price of $0.10 per million input tokens and no output charge, the estimated base cost was $0.0138073; this is not a billing statement and excludes any regional premium. The balanced sample does not estimate natural error prevalence. Keep provenance/source QA separate: image-only comparison cannot establish whether an apparently complete keyboard was reconstructed in a retouched photo.

A separate blind trial gave `gpt-6.1-terra` at `xhigh` eight fixed annotation versions: four previously approved and four with independently documented material errors. It received anonymized original photos and geometry, without the reference verdicts. Terra approved all four erroneous versions, approved two valid versions and marked the other two uncertain. The observed review took 154 seconds; tokens and cost were not measured. This small diagnostic does not support using that procedure for final mask approval. It does not measure code-review quality or establish general model reliability. The eight benchmark copies stayed separate from canonical training decisions.

### Train, select and export

Download a local [official YOLO26n-seg checkpoint](https://github.com/ultralytics/assets/releases/download/v8.4.0/yolo26n-seg.pt). COCO weights provide transfer learning, and the three classes are learned from the reviewed dataset. Run training only after enough complete labels are available to compare on holdout.

```powershell
$run = Join-Path $root "yolo26n-seg-run"
$out = Join-Path $root "export-480x640"
$val = Join-Path $root "val-480x640"
$test = Join-Path $root "test-480x640"
python -B fit.py train $data (Join-Path $root "yolo26n-seg.pt") $run --epochs 50 --size 960 --batch 4 --minimum-positives 300
$weights = Join-Path $run "weights/best.pt"
$onnx = Join-Path $out "keybed.onnx"
python -B fit.py export $data $weights $out --height 480 --width 640 --smoke-split val
python -B evaluate.py select $data $weights $val --height 480 --width 640 --onnx $onnx
# Run this only after choosing the export shape and checkpoint on validation.
python -B evaluate.py test $data $weights $test --height 480 --width 640 --onnx $onnx --selection (Join-Path $val "selection.json")
python -B -m unittest discover -s . -p "test_*.py" -v
```

Use the 960-pixel training baseline to preserve narrower keys. Training reports the verified image and group counts, dataset and checkpoint hashes, and the validation learning curve in `results.csv`. Checkpoint selection uses the one-to-one head that the Quest export uses. Compare the 480 × 640 export to a 960 × 960 reference on validation to quantify the quality cost of the smaller input.

The export uses batch 1, RGB float32, centered letterboxing and ONNX opset 17. It checks the graph and runs finite-output smoke checks on the specified split with ONNX Runtime CPU. The evaluation selects one confidence on validation by macro mask F1 at IoU 0.50. Test requires that selection for the same dataset, checkpoint, shape and export. The report includes box and mask mAP, per-class count errors, missing and extra instances, corner error in mean white-key widths, physical-order inversions and PyTorch-to-ONNX parity. Order is measured in the fitted keybed coordinate system when at least two matched keys are present. The reviewed corners provide a separate diagnostic. Review the per-image overlays and the contact sheet before interpreting aggregate metrics. Confirm import and execution on Sentis 2.4.1, then profile calibration latency, frame pacing and PSS on Quest. [Sentis operators](https://docs.unity3d.com/Packages/com.unity.ai.inference@2.4/manual/supported-operators.html)

The stock COCO checkpoint exported at 480 × 640 with the pinned package and produced finite tensors on the CTK-519 photo in ONNX Runtime CPU. The graph was 11,123,174 bytes with detection shape `[1, 300, 38]` and prototype shape `[1, 32, 120, 160]`.

A provisional 2026-11-11 run trained the three classes for 50 epochs on a frozen subset of 147 reviewed photos: 131 train, five validation and 11 reserved test. Pending reviews were explicitly excluded from this subset, never converted to background negatives. The smaller run supported annotation assistance; the final pilot uses the 300 approved photographs described above. Test was not used for checkpoint selection, export smoke checks or annotation assistance.

On the five validation photos, mask mAP50–95 was 0.791 at 960 × 960 and 0.710 at 480 × 640. The selected thresholds were 0.30 and 0.10 respectively; the smaller input produced 46 extra white-key detections versus 11 at 960. No validation photo had every key correct and physically ordered. These few photos do not establish generalization to a headset camera. The 480 × 640 ONNX passed PyTorch–ONNX Runtime parity on 283 nonempty mask pairs and imported into Inference Engine 2.4.1 with FP32 weights. Unity import is not device execution: actual Quest timing, memory, mask parity and camera-to-world alignment remain unverified for this model.

Image terms remain attached to the photos and derivatives. Ultralytics supplies the pretrained models and resulting fine-tuned models under AGPL-3.0 by default or an Enterprise license. Check distribution obligations before bundling weights with an APK. [Commons reuse terms](https://commons.wikimedia.org/wiki/Commons:Reusing_content_outside_Wikimedia), [Ultralytics licensing](https://www.ultralytics.com/license)
