# Four-corner detection and local refinement

The earlier segmentation candidate often confused dark casing, the front vertical key faces, and the playable surface. Missing end masks could truncate the outline. This experiment separates coarse localization from a small RGB corner refiner. It is a desktop Python experiment, not a change to the Unity practice APK.

## Additional real-photo references

A new 21-photo source ledger was frozen before student inference. Seven photos have independently approved corner-only training labels, eight have independently approved held-out corners and exact visible white-key counts, and six training candidates remain excluded because an endpoint is hidden, blurred or outside the supported orientation. Excluded instrument photos are not negatives. The original 245/27/28 split is unchanged; the local refiner trains on 252 photos after the seven additions.

Two proposed annotations were corrected from native pixels: a CP80 casing shadow was mistaken for its rear-right corner, and a Rhodes rear-left point needed to follow the felt seam. A Steinway endpoint under a fallboard shadow was rejected rather than guessed. The source audit checked Commons metadata, original hashes, normalized pixels, author aliases, capture events and historical image similarity. The public `corners_v4_reviewed.json` records source attribution, licenses, hashes, reviewed coordinates and exclusions; images and private review evidence remain outside Git.

These additions were annotated manually from native photographs and independently reviewed. They are four-corner labels, not new per-key masks. The eight evaluation photographs were reserved before inference and never used for training or checkpoint selection. The eleven difficult photos from the previous experiment are explicitly reused diagnostics, not a fresh holdout.

## Models and selection

The custom YOLO26n-pose head predicts four ordered keypoints: back-left, back-right, front-right and front-left. Training uses the original 245 images at 960 square input, batch four, seed 42 and 40 epochs. The initial keypoint sigma 0.12 trial stopped after 11 epochs when a gradient diagnostic confirmed saturation on wide, thin keyboards. Restarting with sigma 0.50 restored the horizontal gradient. Selection uses actual ordered corner error, not the easier OKS mAP induced by the wider sigma. The selected checkpoint produces 27 valid quads on validation, but none meets the strict four-corner threshold; it is a coarse proposal model.

`corner_refiner.py` trains a compact four-head heatmap network on RGB crops from real training photos. Training centers are jittered around reviewed corners; inference centers come only from the chosen detector's predicted corners. The refiner receives no reference coordinates, instrument profile, key count or MIDI input at inference. Its 128-square crops span 20% of the predicted front edge, with a 48-source-pixel minimum. A local heatmap peak gives subpixel coordinates. Diffuse or excessively distant peaks retain the coarse corner; invalid resulting geometry is rejected. Returned geometry remains `needs_confirmation`.

The refiner is trained for 40 epochs on CPU with seed 42. Every five epochs, validation compares two **global** seed choices: segmentation-derived corners or direct-pose corners. Checkpoint and seed choice maximize strict photo passes, breaking ties with capped worst-corner error. It does not choose the better detector using each test photograph's reference. CPU 0 is excluded; pose training obeyed the existing cooperative GPU budget. A report-serialization failure in the first refiner run was fixed and restarted with the same seed; the independent audit verified identical epoch-five checkpoint bytes across runs.

## Measurements

Every photo, including rejected fits, stays in the denominator. A strict pass requires **all four ordered corners** within **0.25 mean white-key widths** in the source image. That unit is reviewed front-edge length divided by the reviewed visible white-key count. It is not millimetres, 3D alignment, per-key segmentation accuracy, or complete sparse-layout success. The capped mean assigns missing fits an error of four widths and caps each accepted photo's worst error at four, so abstention cannot disappear from the error summary.

The selected refiner has **94,076 parameters** and uses epoch **30** with segmentation-derived seeds. The direct-pose model and its local-refinement variant were not selected. Both final comparison plans were frozen before running either cohort.

| Cohort | Segmentation corners | Selected local refinement | Valid fits before / after |
| --- | ---: | ---: | ---: |
| Development validation, used for selection | 18/27 | **23/27** | 25 / 25 |
| Previously consumed difficult diagnostics | 1/11 | **5/11** | 6 / 6 |
| Eight newly reserved difficult photographs | 0/8 | **0/8** | 2 / 2 |

Validation gained five strict passes without losing a previous pass. However, its capped mean worst-corner error slightly worsened from 0.4830 to 0.4846 widths: the wc-8834177 outlier worsened from 0.548 to 2.036. Selection prioritized strict pass count, so this is not an across-the-board error reduction. Median worst error among fits fell from 0.154 to 0.103.

On the consumed diagnostics, the Philicorda, DGX-520, P-115 and Wohlfahrt gained strict corner passes. Worst error fell from 1.127 to 0.168, 0.302 to 0.195, 1.941 to 0.113 and 3.310 to 0.219 widths respectively. The previously passing Fabesis remained a pass. The P-140S improved from 0.566 to 0.258 but still fails the 0.25 threshold. Five rejected outlines remain rejected. The capped cohort mean improved from 2.488 to 1.917 widths; these are already-used diagnostics, not an unbiased reliability estimate.

The new eight-photo cohort exposes a significant limitation. Four pictures have no bed candidate above the geometry floor, two fail edge/row support, and the two returned outlines both exceed three white-key widths even after refinement. Those photos include distant room views, shallow keybeds, reflections and a strongly oblique synthesizer. A local patch adjustment cannot reliably recover an absent or badly misplaced outline. The direct-pose model also records zero strict passes on both final cohorts. The evidence supports better **local correction**, but rejects automatic-calibration readiness and does not meet the 90% target.

The next development priority is full-outline localization at smaller image scales and shallow viewing angles, with more independently reviewed training photos from those conditions. New-data capture/training and any multi-scale proposal method need another prospective evaluation; the eight photos above are now consumed evaluation evidence and must not be described as a future untouched holdout.

Across the eight final-cohort images that actually reached the refiner, its CPU-only median incremental time was approximately 17 ms with two Torch threads. Most rejected cases skip the network; their near-zero times are not counted in that figure. This is a desktop diagnostic, not Quest performance.

Selected artifact identities:

- Pose weights: `eb41d004d68d386f5b3fa2b321795203faa1f6d774ae460bdf9b176c1332caf8`.
- Refiner weights: `7149e3887d2a17ad3f3bcf01baff22e482fb63beee2bb1ffe340d3c4f11515aa`.
- Augmented dataset manifest: `0bcb4c84fe49c2ec7143fac6dc2e42bb508f40a19f848e20577a29c6095a7a2e`.
- Existing segmentation ONNX is unchanged: `47cfe4044cc9cb24ff1c4266a8e327c8b7fc8c24a79c2204c214f78eee7d7edc`.


## Reproduction and limitations

The pose packer checks independent training approvals, preserves original split rows, binds source bytes and rejects both identical files and decoded-pixel duplicates, including re-encoded validation photos. Evaluation freezes model/checkpoint, selection, source code, package versions, references and cohort IDs before inference; any changed binding fails. It retains all raw ordered corner proposals and rejects, and provides paired overlays.

Run from `SynthesiaSet/piano_perception/keybed_yolo` with the pinned ML dependencies. Paths below are placeholders for local, checksum-bound artifacts. Start the process with CPU 0 excluded; `ResourceBudget` maintains the existing CPU/GPU policy.

```text
python corner_pose.py prepare <reviewed-segmentation-dataset> <pose-dataset>
python corner_pose.py train <pose-dataset> <official-yolo26n-pose.pt> <pose-run> --epochs 40 --size 960 --batch 4
python corner_pose.py evaluate <pose-dataset> <pose-run/weights/best.pt> <pose-validation>
python corner_pose_data.py <pose-dataset> <approved-train-only-ledger.json> <augmented-dataset>
python corner_refiner.py <augmented-dataset> <validation-seeds.json> <refiner-run> --epochs 40
python corner_refiner_evaluate.py freeze <plan.json> --scope fresh-holdout --references <reviewed-corners.json> --segmentation <keybed.onnx> --pose <pose-best.pt> --refiner <refiner-run/best.pt> --selection <refiner-run/selection.json>
python corner_refiner_evaluate.py evaluate <plan.json> <new-output-directory> --plan-sha256 <freeze-output> --overlays
python -B -m unittest discover -s . -p "test_*.py" -v
```

The training seed manifest has `split: val`, and a `variants` object containing `segmentation` and `pose`. Each variant binds its prediction report by `source_sha256` and maps every validation ID in `quads` to a predicted source-pixel quadrilateral or null. Build these entries from the actual detector reports, not reviewed corners. The independent audit checked every seed against its originating report. The evaluation reference ledger is schema 1 with per-image paths/hashes, dimensions, split, reviewed corners and `expected_white_count`. Training additions require an independently approved corner-only ledger; the public source ledger itself is not a ready-to-run approvals file.

All **107 tests** passed, including coordinate/padding round trips, malformed seeds, diffuse and oversized corrections, invalid final geometry, real JSON serialization, rejection denominators, split preservation, decoded-pixel duplicate exclusion, immutable model/reference/source bindings, and frozen evaluation replay. Independent source and annotation QA approved all 15 admitted references. Final independent model/evidence QA is recorded with the private artifacts.


This is a selected-on-validation experiment with a small new evaluation cohort, not a claim of general reliability. The initial product target remains at least 90% of independently sampled complete captures with correct outline and approximate guides under an explicitly configured instrument. Partial views, motion, negative scenes, false acceptance, wrong instrument profiles and Quest world alignment need separate evidence. The new models have not been exported to or profiled on Quest, and ADB found no connected headset during this round. The installed APK and its production detector remain unchanged.
