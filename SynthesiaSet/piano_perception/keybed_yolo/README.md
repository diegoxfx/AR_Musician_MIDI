# Train a keybed and individual-key detector

Train one **YOLO26n-seg** model on real piano photos. Its three instance classes are the complete playable surface, one white key, and one black key. At calibration, fit an ordered quadrilateral to the complete surface mask. Use the individual key masks to refine each key's position and assign physical slots from left to right. The ordered corners are derived from the surface mask. Their order is back-left, back-right, front-right, front-left from the player's viewpoint. The segmentation model produces masks and scores. Key press detection uses a separate input source.

YOLO26n-seg is the first Quest candidate. Ultralytics reports 2.7 million parameters and 9.3 GFLOPs for the fused model at 640 pixels. Its NMS-free export produces detections and mask prototypes. Start with a 640 × 480 inference input and benchmark a higher-resolution rectified crop, such as 1024 × 256, if the individual masks lose narrow keys. YOLO11n-seg provides an export fallback. Measure both mask quality and the device cost before changing the production detector. [YOLO26 results](https://docs.ultralytics.com/models/yolo26), [Exported output formats](https://docs.ultralytics.com/guides/end2end-detection)

## Real photos and group splits

- The twelve-photo Wikimedia Commons manifest records the photographer, license, source link, exact download URL and SHA-256. It exercises acquisition and review. It is too small to establish model quality.
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

In a two-photo test at a 672-pixel SAM input, the Casio CTK-519 photo yielded 38 white-key and 20 black-key shape candidates. The Yamaha PSR-220 yielded 37 and 25. Each complete photographed keyboard has 36 white and 25 black keys. The Casio output visibly missed black keys at the right, and the Yamaha output included duplicate masks for one white key. These counts describe proposals before correction and do not measure segmentation accuracy. [Casio source photo](https://commons.wikimedia.org/wiki/File:Keyboard519.JPG), [Yamaha source photo](https://commons.wikimedia.org/wiki/File:Yamaha_PSR-220_(1).jpg)

Edit the generated `reviews.json`. Keep its `id` and `image_sha256`. Record `reviewed_by` and `reason` on every reviewed item.

- An `accepted` image must show the complete playable surface and individual keys. Set `keybed` to an object with four ordered `corners` or a reviewed `proposal` ID. Record `expected_key_count` and `expected_white_count`. The `keys` array needs one item for each zero-based physical slot, with `slot`, `class` and a reviewed `proposal` ID or `polygon` of pixel-space vertices. If SAM missed or distorted a key, provide a corrected polygon. An explicit polygon overrides the proposed outline while preserving the proposal ID as provenance.
- This pilot packs complete keyboards only. Mark partial, ambiguous, crowded or poorly exposed scenes `rejected`. Keep partial scenes in a separate calibration challenge set. They can later support individual-key training under an explicit partial-label policy that annotates every visible key.
- Use `negative` with `contains_visible_target: false` for reviewed backgrounds with zero target objects. Do not use an empty label file when a keyboard or its keys are visible.

The packer checks complete slots, the white/black counts, edge clipping and nearly identical key polygons. The order and semantic accuracy still require review. Keybed polygons intentionally overlap the per-key masks. Training sets `overlap_mask=False` to preserve these masks separately. The per-key contour can be concave to follow the white key around the raised black key. Disconnected fragments and large mask holes need correction or rejection. [YOLO segmentation dataset format](https://docs.ultralytics.com/datasets/segment)

```powershell
python -B dataset.py pack $work $data
```

The packer includes review overlays, normalized YOLO labels, provenance and a local `keybed.yaml`. It requires reviewed positive examples in every split. It refuses to overwrite the packed directory.

### Train and export

Download a local [official YOLO26n-seg checkpoint](https://github.com/ultralytics/assets/releases/download/v8.4.0/yolo26n-seg.pt). COCO weights provide transfer learning, and the three classes are learned from the reviewed dataset. Run training only after enough complete labels are available to compare on holdout.

```powershell
$run = Join-Path $root "yolo26n-seg-run"
$out = Join-Path $root "export-480x640"
python -B fit.py train $data (Join-Path $root "yolo26n-seg.pt") $run --epochs 50 --size 640
python -B fit.py export $data (Join-Path $run "weights/best.pt") $out --height 480 --width 640
python -B -m unittest discover -s . -p "test_pipeline.py" -v
```

The export uses batch 1, RGB float32, centered letterboxing and ONNX opset 17. It checks the graph and runs finite-output smoke checks on the held-out photos with ONNX Runtime CPU. The report records actual input/output tensors and operators. It does not assess localization quality or test Sentis or Quest. Use the operator report and confirm import and execution on Sentis 2.4.1, then profile calibration latency, frame pacing and PSS on Quest. [Sentis operators](https://docs.unity3d.com/Packages/com.unity.ai.inference@2.4/manual/supported-operators.html)

The stock COCO checkpoint exported at 480 × 640 with the pinned package and produced finite tensors on the CTK-519 photo in ONNX Runtime CPU. The graph was 11,123,174 bytes with detection shape `[1, 300, 38]` and prototype shape `[1, 32, 120, 160]`. The three-class fine-tuned model remains pending complete reviewed labels.

Image terms remain attached to the photos and derivatives. Ultralytics supplies the pretrained models and resulting fine-tuned models under AGPL-3.0 by default or an Enterprise license. Check distribution obligations before bundling weights with an APK. [Commons reuse terms](https://commons.wikimedia.org/wiki/Commons:Reusing_content_outside_Wikimedia), [Ultralytics licensing](https://www.ultralytics.com/license)
