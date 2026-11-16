# Sparse-key reconstruction experiment

The initial calibration objective now follows the user's narrower requirement: find the complete playable outline and enough reliable keys to reconstruct approximate positions. It no longer requires YOLO to segment every key correctly. `keybed_layout.py` implements this as a **Python preview candidate**, with the existing weights and contour selector. It is not integrated into the Unity practice app or APK.

## Inputs and behavior

The caller supplies decoded model detections, image/letterbox metadata, a supported four-corner fit, and an explicit instrument profile. The CT-S200 profile has 61 chromatic positions beginning at MIDI note 36, consistent with the application's existing setup. These numbers describe the configured instrument; receiving MIDI messages is not required. Other profiles can supply only a starting pitch class, leaving the absolute MIDI octave unknown. A missing profile or a count without pattern phase produces a rejection rather than invented note identities.

The reconstruction rectifies the playing plane before fitting spacing. It measures reliable white-key masks in their solid front band, avoiding rear-notch centroid bias. At least five distinct white anchors must agree with the configured spacing, span the keyboard, and include central support. A bounded lattice fit handles skipped keys and duplicate detections without interpreting every neighboring detection as a neighboring physical key. Key confidence cutoffs are 0.35 for white and 0.50 for black. Geometric residuals and model scores are evidence, not calibrated probabilities.

The outline must have raw bed-edge support or exactly reproduce the existing white-edge fit from the original model outputs. This second path permits white contours to recover a bed mask truncated at an end. It still needs actual contour evidence; it does not extend an arbitrary partial view to an assumed full keyboard. These checks cannot prove that a consistently truncated detection is complete.

The profile supplies the white/black pattern. Compatible black observations can adjust the corresponding black guides; multiple contradictory black observations can reject the preview. Every guide records whether a detection supports it or its position is estimated, preserving indices into the original detections. Black guides are approximate lines on the white-key plane, not reconstructed raised black-key surfaces.

The API has two result states: `rejected` with a reason, or `needs_confirmation` with guides and evidence. It has no automatic acceptance state. A successful preview still requires checking the instrument profile and four corners. Sparse observations can also fit a nearby incorrect key count or another pattern phase, and visual spacing cannot establish the absolute octave. Invalid configuration values raise an error. Partial views, no outline, insufficient/localized anchors, inconsistent spacing, or incompatible black patterns can produce a rejection. Moving-head accumulation and a user-confirmed calibration lifecycle are not implemented by this module.

## Development comparison

`evaluate_layout.py` scores guide position independently of segmentation IoU. White guide lines are intersected at 80% of the **reviewed** playing-plane depth. Every white center must be within **0.25 rectified mean white-key widths** of the corresponding reviewed key center. The joint check also requires the configured white/black pattern to match and all four corners to satisfy the previous 0.25 mean-white-width tolerance in source pixels. All reviewed photos, including rejected reconstructions, stay in the denominator.

Profiles for this experiment were derived from the reviewed counts and white/black sequence and supplied explicitly. This models an ideally configured instrument, not automatic recognition of its size, starting note or octave. Reference polygons are used only by the scorer, never by reconstruction. The results do not establish black-key depth/height, MIDI identity, millimetre error, or camera-to-world accuracy.

On the existing **27 development/validation photos**:

| Variant | Returned previews | All white guide positions pass | White positions and four corners pass |
| --- | ---: | ---: | ---: |
| Uniform outline + supplied profile, no anchor checks | 25 | 18 | 16 |
| Reconstruction with all qualifying anchors | 25 | 21 | 17 |
| Reconstruction with at most 8 white + 5 black anchors | 23 | 20 | 17 |

The uniform variant is an unvalidated comparison, not an alternate acceptance API. The sparse variant retains the original predictions for outline fitting and its evidence replay; it limits only the anchors used for reconstruction. It does **not** demonstrate that the neural model needed to detect only 13 total objects. Across its 23 previews, 297 guides have detection support and 765 are estimated. In the Korg DW-6000 example, 13 supported guides reconstruct 48 additional positions on a 61-key instrument; the worst white-guide lateral error is 0.119 widths and all four corners pass.

The same **11 already-consumed diagnostic photos** were then replayed after a checksum-bound code/configuration freeze. They are not a new holdout:

| Variant | Returned previews | All white guide positions pass | White positions and four corners pass |
| --- | ---: | ---: | ---: |
| Uniform outline + supplied profile | 6 | 1 | 1 |
| All qualifying anchors | 3 | 2 | 1 |
| At most 8 white + 5 black anchors | 5 | 2 | 1 |

Sparse input can remove contradictory observations: the two additional diagnostic previews are **not** extra correct calibrations. For example, the sparse DGX-520 and P-115 previews have white-position errors exceeding one and nearly two key widths respectively. They remain previews requiring confirmation. The P-140S improves from 0.532 to 0.247 worst white-position error, but its corners still fail the joint check. A twelfth previously selected source still has an ungradable reference and remains unknown; it was not relabeled as a negative or silently counted as a success.

These measurements support sparse reconstruction as a useful component, while showing that the complete outline remains the principal obstacle. They do not establish general reliability. The earlier exact-mask result and this guide-position result measure different things and must not be compared as if they were the same accuracy metric.

## Contour alternatives and next milestone

A separate comparison tried bed-only, bed-first, and a guarded combination of white-supported sides with the bed-mask front. The guarded combination improved strict corners from 18/27 to 20/27 on validation, but recovered no additional strict pass on the consumed diagnostics: both remained 1/11. It was not adopted. Among those same six diagnostic fits, only the already-passing photo has both rear corners within tolerance, so a front-line correction alone cannot fix the others. The original contour selector is unchanged.

The proposed first milestone is at least **90% of independently sampled complete captures with correct approximate guides and outline**, under an explicitly configured instrument profile. Perfect segmentation of every key is no longer part of that initial goal. The present joint result is 17/27 on development and 1/11 on reused diagnostics; neither meets or establishes that reliability target. A further untouched sample and actual CT-S200 Quest captures are still required. Negative scenes, partial views, occlusion, head motion and incorrect profiles need separate false-acceptance evaluation before any automatic acceptance rule.

## Reproduction and device status

```powershell
python -B -m unittest discover -s . -p "test_*.py" -v
python -B evaluate_layout.py <validation-cache.json> <explicit-profiles.json> <new-output-directory>
```

The profile manifest requires `source`, `scope`, and one profile under `profiles` for each cache ID. Each profile specifies `key_count` plus `start_pitch_class` or `first_midi`. The evaluator binds the input cache, supplied profiles, model and source code by SHA-256 in its report. It accepts validation caches or explicitly marked `development-diagnostic` caches; it does not silently treat an unconsumed test split as development data.

The four new modules/tests passed with the existing suite: **82 tests total**. Regressions cover sparse gaps, duplicate/outlier observations, wrong profiles, phase uncertainty, localized evidence, clipping, original detection indices, replayed outline evidence, and asymmetric letterbox/nonuniform source scaling. Independent QA is recorded with the private experiment artifacts.

The model weights and ONNX export are unchanged. No new training or paid API calls were needed. ADB found no attached Quest during this work, so device performance and Unity integration of the reconstruction remain unverified. The installed practice APK is unchanged.
