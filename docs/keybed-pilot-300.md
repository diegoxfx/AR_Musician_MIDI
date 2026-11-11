# Real-photo piano pilot: 300 reviewed images

The 2026-11-11 pilot produced a trained YOLO26n-seg candidate for a playable keybed and individual white/black keys. **It is not ready to replace automatic piano calibration.** The held-out test has good aggregate instance detection, but only 2 of 28 photographs have every key matched without extra predictions. The production app still uses its existing detector and manual setup.

## Data and training

500 distinct real Commons photographs were collected with source/license provenance. SAM proposals were corrected against the photographs and independently reviewed. Exactly 300 positive photographs were approved; 175 pending and 25 rejected candidates were excluded, never treated as background negatives. No synthetic piano images were used for training.

| Split | Photos | Source groups | White keys | Black keys |
| --- | ---: | ---: | ---: | ---: |
| Train | 245 | 196 | 7,517 | 5,192 |
| Validation | 27 | 24 | 791 | 545 |
| Test | 28 | 23 | 841 | 581 |

Each positive also has one reviewed four-corner keybed. All 15,467 key masks retain physical slot order and original-image coordinates. Independent freeze QA verified the exact approval rows, packed images, label bytes and source groups. The warm-start chain used earlier 147- and 233-photo subsets; no final validation or test source appeared in either predecessor's training split, and final test sources were absent from prior validation.

The final run used Ultralytics 8.4.210, 960-pixel training input, batch 4, seed 42, nonoverlapping instance-mask storage and the end-to-end head. It completed 50 epochs in 2,169 seconds. `best.pt` was selected at epoch **30**, the unique maximum of the trainer's combined box/mask validation fitness. The final epoch was not automatically selected. Logical CPU 0 remained excluded; the normal CPU profile and cooperative GPU policy were retained. GPU duty scheduling is not a hard instantaneous utilization guarantee.

## Validation choice and untouched test configuration

Both fixed FP32 ONNX exports were compared on the same 27 validation photos. Each confidence was selected on validation by macro mask F1 at IoU 0.50, with count-error and confidence tie breaks. The 960 × 960 model and its threshold of 0.30 were frozen before the final test evaluation. The 480 × 640 configuration was not evaluated on the final test.

| Fixed input (height × width) | Confidence | Validation mask mAP50–95 | All keys matched, no extras | Quadrilateral fitted |
| --- | ---: | ---: | ---: | ---: |
| 480 × 640 | 0.20 | 0.6337 | 1 / 27 | 8 / 27 |
| 960 × 960, selected | 0.30 | 0.7731 | 5 / 27 | 22 / 27 |

These are fixed-shape evaluations using the deployment decoder, not the trainer's potentially different validation preprocessing and decoder. A successful quadrilateral fit means the geometric check returned four corners; it does not establish that those corners are accurate enough for calibration.

## Test result at 960 × 960

The frozen configuration was evaluated on 28 test photos from 23 source groups. Box mAP50 / mAP50–95 was **0.9631 / 0.7546**; mask mAP50 / mAP50–95 was **0.9611 / 0.7443**. These are detection metrics, not the percentage of fully correct keyboards.

mAP uses the ranked predictions with confidence greater than 0.001 across the stated IoU thresholds. The counts, corner fits and order checks below use the frozen operating threshold of 0.30.

| Target | Reviewed instances | Matched at mask IoU ≥ 0.50 | Unmatched references | Unmatched predictions |
| --- | ---: | ---: | ---: | ---: |
| Keybed | 28 | 26 | 2 | 5 |
| White key | 841 | 775 | 66 | 74 |
| Black key | 581 | 569 | 12 | 26 |

An unmatched reference/prediction pair can represent one poorly localized mask; these columns do not imply that every unmatched prediction is a literal duplicate key. Visual inspection also found actual omissions, extra instances and incorrect keybed boundaries, especially in oblique or low-contrast photographs.

- Only **2 / 28** photos matched every white and black key without extras. Requiring a fitted keybed and no inversions in the projected key-centroid order reduced this to **1 / 28**.
- Four-corner fitting succeeded on **24 / 28** photos. Among those fits, mean error over the four corners averaged **0.635 white-key widths**; its median was **0.323**. The worst corner reached **6.914 white-key widths**. These errors are too large to accept alignment automatically.
- Three fitted cases had projected centroid-order inversions. This is a mapping heuristic: raised black keys and perspective can invert 2D centroids even when physical key identities are correct. It is not an independent physical slot recognizer.
- There are **zero negative/background photos** in this pilot. Background false acceptance, hand occlusion, moving-head accumulation and partial-keyboard recovery remain unmeasured.

No further shape, confidence or checkpoint tuning was performed on these test results. A future changed model or mapping rule needs a fresh independent evaluation set.

## Export and Quest readiness

PyTorch–ONNX Runtime parity passed on all 27 validation photos for both sizes: 1,471 nonempty mask pairs at 480 × 640 and 1,453 at 960 × 960. The chosen model also passed all 28 test cases, with 1,475 nonempty mask pairs. This establishes desktop export consistency, not accuracy against labels.

The selected ONNX imported successfully in Unity 6000.2.8f1 / Inference Engine 2.4.1, retaining FP32 weights. The serialized Sentis model is 11,285,156 bytes. Fresh fixtures for all 28 test photos bind the selected threshold, model, dataset and configuration hashes. The [isolated Quest probe](../tests/perception/quest-probe/README.md) has an existing verified APK and validates device receipts before reporting parity or timings.

**Quest execution remains pending:** ADB listed no connected device at the final preparation check. Unity import, host simulation of the probe runner and ONNX CPU execution do not establish Quest latency, memory, mask parity, frame pacing or camera-to-world alignment. No new detector was installed into the practice app.

An additional unscored CPU diagnostic used the previously captured CT-S200 image (320 × 240, cropped at the right). It produced 37 white-key, 25 black-key and one keybed candidates. These are prediction counts, not verified correctness. The quadrilateral fitter rejected the mask for touching the image edge; a complete calibration was not inferred. Upsampling this old image does not add camera detail.

## Artifact identities

Large datasets, models, fixtures, APKs, private review evidence and the user's camera capture remain outside Git. Local artifacts use the prefix `pilot-reviewed-300-e079ae0388c6`.

| Artifact | SHA-256 |
| --- | --- |
| Approval ledger | `e079ae0388c6bd8d8509da7d76d5bc55c35781f86c5c303e602334b8d18e1214` |
| Packed dataset provenance | `711b37dedee217ea5f660bc0dcfc5aa723cb4f650a61fba093594d8989ef1919` |
| Best checkpoint | `0446607fbe398fdd4bcf4614c46615303d1ef4bda47b5cd79264d526487b992e` |
| Selected ONNX | `47cfe4044cc9cb24ff1c4266a8e327c8b7fc8c24a79c2204c214f78eee7d7edc` |
| Validation threshold selection | `1b675879ac0505e8648305c2332c4cfab62829cd73dd4fe8c75e2cec1a040d26` |
| Imported Sentis model | `1613f041dd987744fa09e644bdb0eefe9023d35933251bea029cf1ee9bef5d92` |

The next useful step is the isolated Quest runtime measurement, followed by targeted camera data and a more reliable keybed/physical-slot mapping method. This pilot demonstrates learnable per-key signal; it does not meet the app's complete-calibration requirement.
