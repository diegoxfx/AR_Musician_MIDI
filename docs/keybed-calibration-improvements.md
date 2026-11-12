# Calibration candidate after the 300-photo pilot

The existing YOLO26n-seg weights now have a separately tested key-filtering and corner-fitting candidate. **The strong validation improvement did not transfer adequately to new photos: only 1 of 11 evaluable prospective photos passed complete calibration.** This is a Python evaluation implementation, not a replacement detector in the Quest practice APK. The earlier [300-photo pilot](keybed-pilot-300.md) remains the baseline record.

## Proposed reliability target

For the next pilot milestone, at least **90% of independently sampled, complete-keyboard captures** should pass all three conditions together:

1. Every physical white and black key has exactly one same-class predicted mask matched at IoU ≥ 0.50, with no unmatched key predictions.
2. Every fitted keybed corner is within **0.25 mean white-key widths** of its reviewed position on the upper playing plane.
3. The matched physical slots have no inversions in the inferred left-to-right ordering.

These are proposed development criteria, not a previously agreed or achieved accuracy guarantee. Fit rejections stay in the denominator. Report candidate coverage separately; a small collection of successful fits cannot establish reliability. A fitted quadrilateral does not prove that a calibration is correct, and the API does not automatically approve it.

The corner unit is the reviewed front-edge length divided by the number of white keys, measured in source pixels. It is not a measured millimetre error or a 3D world-alignment result. This static-photo pilot also cannot establish false acceptance on backgrounds, partial views, hand occlusion, moving-head accumulation, temporal stability, or Quest camera-to-world accuracy. Those need separate real-headset evaluation before automatic acceptance.

## Errors and selected changes

The original deployment decoder retains repeated masks for the same physical key. Lower confidence thresholds alone admit still more extras. Some photos also contain printed keyboards on packaging, and some narrow keys have weak or poorly localized masks.

`postprocess.py` filters white and black keys at separate confidence cutoffs, then suppresses overlapping masks **within the same key class**. It does not merge black and white keys. A conservative distance check removes key candidates well outside the strongest surviving keybed's mask window. This check is not clipping to a quadrilateral or inventing a regular key grid.

`keybed_geometry.py` fits supported contour lines and uses the observed white-key row to recover bed ends that the bed mask truncates. Weak bed candidates require white-key corroboration. Image-edge clipping, disconnected foreground and unsupported corners cause a rejection. The remaining front-corner errors often arise because a visible white-key contour includes its vertical front face while the keybed target is the upper playing plane.

`calibration.py` combines these steps without receiving labels, expected key counts or physical note identities. It preserves indices into the decoded detections and reports the geometry selection and distance-filter ROI independently. Geometry uses the raw predictions, including weak bed candidates, rather than the more strongly filtered list.

The frozen candidate uses the unchanged 960 × 960 FP32 end-to-end ONNX export:

| Setting | Value |
| --- | --- |
| Decode floor | 0.001 |
| Instance cutoffs: bed / white / black | 0.30 / 0.125 / 0.15 |
| Same-class mask suppression IoU | 0.30 |
| Containment suppression | Disabled |
| Maximum gap from bed window | 1 estimated bed thickness |
| Geometry bed / white support cutoffs | 0.025 / 0.30 |

The alternate one-to-many head was also tested on validation, with class-aware box NMS and the same cleanup. A bounded 50-setting comparison reached the same 23 complete-key photos, while its best setting reduced unmatched key references/predictions from 52 to 28. The original head is retained because complete-photo coverage did not improve and the alternate export/Quest decoding path has not been verified. No retraining or changes to the model weights were needed for these experiments.

## Validation comparison

These 27 photographs from 24 source groups were used to select the changes. Their results are development evidence, **not independent generalization accuracy**. The original 28-photo test was not reused for tuning or rescored with these variants.

| Metric, all 27 validation photos | Original | Candidate |
| --- | ---: | ---: |
| Complete keys, strict corners and correct order together | 1 / 27 | **17 / 27** |
| All keys matched, no extra key predictions | 5 / 27 | 23 / 27 |
| All four corners within the stated tolerance | 2 / 27 | 18 / 27 |
| Returned quadrilateral | 22 / 27 | 25 / 27 |
| White keys: unmatched references / predictions | 33 / 98 | 24 / 19 |
| Black keys: unmatched references / predictions | 13 / 38 | 8 / 1 |
| Unmatched overlapping key predictions at IoU ≥ 0.50 | 75 | 0 |
| Mean per-photo corner error among returned fits | 0.513 | 0.123 |
| Worst single corner among returned fits | 3.834 | 0.548 |

Corner errors are in mean white-key widths. An unmatched reference and prediction can represent one poorly localized key, so the count columns are not necessarily literal missing and duplicate keys. The overlap metric is a defined duplicate proxy; zero does not mean zero extra detections. The candidate still has 20 unmatched key predictions.

The joint validation result is **63.0%, below the proposed 90% milestone**. Of the ten remaining joint failures, six have exact keys but fail the corner tolerance, one passes the corners but fails the keys, and three fail both. Two rejected captures account for 20 of 24 white-key misses, 18 of 19 white-key extras, and all eight black-key misses. The single original joint success remains successful.

## Prospective comparison after freezing the changes

Twelve further original photographs from twelve source groups were selected without looking at YOLO outputs. Source QA checked them against the 500 canonical photos, earlier challenge/preview images, authors, capture events and perceptual hashes. Two preliminary choices were replaced for source eligibility before inference. A timestamp correction preserved the original metadata and image bytes.

SAM proposals were corrected against native photographs and independently reviewed. Reference QA found and corrected cast shadows included in black masks and a white front-face boundary. Eleven photos received complete labels with 520 white and 361 black keys. The twelfth, the John Rudd, remains in the source ledger as **ungradable**: overexposure hides several white-key boundaries even in the original JPEG. It is not a negative image or a model failure. Neither it nor any scored photo was replaced after seeing predictions.

The model, code, settings, source inventory, references and QA reports were checksum-bound before the paired comparison. No settings or annotations were changed after the scores were inspected. All twelve images ran through the unchanged ONNX model; only the eleven with independently approved references were scored.

| Metric, the same 11 new evaluable photos | Original | Candidate |
| --- | ---: | ---: |
| Complete keys, strict corners and correct order together | 0 / 11 | **1 / 11** |
| All keys matched, no extra key predictions | 0 / 11 | 2 / 11 |
| All four corners within the stated tolerance | 0 / 11 | 1 / 11 |
| Returned quadrilateral | 7 / 11 | 6 / 11 |
| White keys: unmatched references / predictions | 143 / 86 | 117 / 86 |
| Black keys: unmatched references / predictions | 38 / 40 | 30 / 15 |
| Unmatched overlapping key predictions at IoU ≥ 0.50 | 54 | 0 |
| Mean corner error on the **same six** photos fitted by both | 1.584 | 0.597 |

The candidate has one confirmed complete success among the twelve selected sources, ten scored failures and one unknown reference outcome. The conditional 1/11 result is a small-sample observation, not an estimate of Quest or general piano accuracy. Do not combine it with the selected-on-validation result to advertise a single accuracy percentage. It remains far below the proposed 90% milestone.

Suppression removed the defined overlapping duplicates, but 101 unmatched key predictions remain. Most remaining white-key errors occur in the oblique Yamaha P-150, Scharf & Hauk and Yamaha P-60 scenes. The Philicorda has every key matched yet still misses the corner tolerance. Five of six returned candidate quads fail the strict corner check, so the current geometric guards cannot serve as an automatic acceptance rule.

Independent inspection found fragmented or shifted white masks in the P-150 and substantial missing coverage at the low-note end of the P-60. In the latter, 16 white and eight black references had no same-class retained candidate even at mask IoU 0.10. Across the full set, even before postprocessing, 62 white and 19 black references have no same-class decoded mask reaching IoU 0.50 at the 0.001 decode floor. A diagnostic that clips white predictions and references at the reviewed front edge gained only 15 net white matches against 117 white misses. That diagnostic uses ground truth and is not deployable; it did not replace any frozen score. Front-face conventions explain part of the error, but cannot explain the generalization failure.

There is a substantial source-scale difference: only 14 of 245 training photos and two of 27 validation photos have 88 keys, whereas six of the eleven new photos have 88 keys and two have 85. Median mean-white-key width after 960-pixel resizing is 24.59 pixels in validation versus 15.59 in the new set. This is a plausible contributor to the gap, not an isolated causal experiment. Low-resolution originals, oblique views, shadows and full-face versus playing-plane geometry also need attention.

The next accuracy work should prioritize full-length and oblique real keyboards at the actual Quest camera scale, including CT-S200 captures; preserve more pixels per key through a bed crop or overlapping inference regions; and supervise the four upper-plane corners explicitly rather than relying on the full visible key silhouette. Ultralytics documents [keypoint estimation](https://docs.ultralytics.com/tasks/pose/) and [sliced inference](https://docs.ultralytics.com/guides/sahi-tiled-inference/) as available mechanisms; their accuracy and runtime benefit for this application remain hypotheses. Each change needs validation selection and a further untouched evaluation set. These are next experiments, not features or improvements claimed by this batch. Additional epochs on the same distribution have not been shown to fix the observed failures.

## Independent verification and reproduction

Fresh independent code QA approved the frozen candidate and comparison. All 67 ML tests passed, including asymmetric letterboxing and nonuniform source scaling checks. Independent pixel-set overlaps, matching and homography calculations reproduced every validation case. The baseline reproduced the original evaluator, and two fresh ONNX runs reproduced cached tensors bit-for-bit. Cached tensors, image identities, model and dataset provenance were verified.

A separate fresh evaluator audit approved the prospective evidence and rejected model readiness. It verified the pre-inference freezes, all twelve source/reference/cache identities, exact recalculation of every scored case, and the paired six-photo corner comparison. It inspected native prediction overlays for the P-150, P-60, Philicorda and Fabesis, and independently confirmed that maximum-cardinality matching would not recover additional candidate matches at the stated IoU. This approval validates the disappointing result; it is not approval to deploy automatic calibration.

From `SynthesiaSet/piano_perception/keybed_yolo`, with the pinned dependencies and resource policy already configured:

```powershell
python -B -m unittest discover -s . -p "test_*.py" -v
python -B evaluate_calibration.py <validation-cache-manifest.json> <new-report-directory>
```

The replay manifest contains only accepted validation rows, original review instances, `Frame` metadata, model/dataset hashes and checksum-bound `rows`/`protos` NPZ files. The command rejects non-validation data and writes to a new directory. It does not run a parameter search. Prospective data must be evaluated under a separately frozen protocol, not mislabeled as validation to bypass that guard.

| Artifact | SHA-256 |
| --- | --- |
| Unchanged ONNX model | `47cfe4044cc9cb24ff1c4266a8e327c8b7fc8c24a79c2204c214f78eee7d7edc` |
| Original dataset provenance | `711b37dedee217ea5f660bc0dcfc5aa723cb4f650a61fba093594d8989ef1919` |
| Validation cache manifest | `d5598e7b49968ccf3335938230d0c5981bf31bed8c7377850c01f6fa0f1e5028` |
| Prospective source manifest | `7f86f19d5af845360b0d4f4872e5fc71a18fcca21d7d2417ebe509d9588a2c6a` |
| Frozen twelve-reference ledger | `08c13d466a14e2c3b0618c6e0cebd2a8af95e88e3cd833f03f97a1750a1186fc` |
| Prospective configuration/reference freeze | `9d90339f192acac0126b8e9a086a6f0f8f7878fc6096e0ca710d1f7524f57075` |
| Prospective evaluation report | `c433a62d738baf279de203c71d60311948ffba2163ae218d9fee62b12c8c2646` |

The Quest was not visible to ADB during this work. Device latency, memory, and parity for the new postprocessing are not established. The practice APK still contains its existing detector.
