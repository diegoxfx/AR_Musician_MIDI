# Quest piano detection and SAM 3.1

`unsloth/sam3.1` can segment the visible keybed on the PC. The 672 × 672 Sentis version began execution on Quest 3 but encountered sustained system memory pressure during its first encoder run. Keep the existing standalone detector for now. A Quest implementation of SAM 3.1 would need a lower-memory runtime and a new device test, even for a single initial capture. A complete CT-S200 frame and headset alignment are still needed to measure calibration accuracy.

## Real photographs, SAM labels and compact YOLO

The subsequent [300-photo pilot result](keybed-pilot-300.md), completed locally on 2026-11-11, trained and evaluated a three-class YOLO26n-seg candidate. Validation selected 960 × 960 over 480 × 640 (height × width). Only 2 of 28 test photographs matched all keys without extra predictions, so automatic calibration is not ready. Desktop export parity and Unity import passed; actual Quest execution remains pending because no device was connected. The earlier research and stock-model checks below describe how the pilot was chosen, not the current model's measured performance.

The user selected standalone Quest and real internet photographs, with SAM running locally on the PC to propose labels. Synthetic piano images are outside this experiment. The intended student detects both the playable surface and individual keys, using three instance-segmentation classes: `playable_keybed`, `white_key`, and `black_key`. Fit four ordered perspective corners to the accepted keybed mask. Each individual key retains its own observed mask; a detected outer rectangle alone must not be reported as individual-key detection.

As reviewed on 2026-11-10, **YOLO26n-seg** is the first candidate. Ultralytics reports 2.7 million fused parameters and 9.3 GFLOPs at 640 pixels for the released COCO segmentation model. These are reference-model figures, not measurements of a trained piano model on Quest. YOLO27 remains unreleased. YOLO11n-seg is a compatibility fallback if the exact YOLO26 ONNX export does not pass Sentis import and device execution. A pose-only student addresses the corners but does not meet the complete per-key segmentation objective. [YOLO26 segmentation](https://docs.ultralytics.com/tasks/segment/), [YOLO27 availability](https://docs.ultralytics.com/models/yolo27/), [YOLO11](https://docs.ultralytics.com/models/yolo11/)

The pilot budget is **300–500 distinct, reviewed photographs**. A broader initial target is **2,000–5,000**, increased according to held-out errors rather than treated as a guaranteed accuracy threshold. These are project planning estimates. Ultralytics' general guidance starts transfer-learning experiments with a few hundred labeled objects, and recommends at least 1,500 images and 10,000 instances per class for robust use; that is not a task-specific sample-size calculation. Hundreds of keys labeled in adjacent video frames do not provide hundreds of independent scenes. [Official dataset guidance](https://docs.ultralytics.com/guides/data-collection-and-annotation/)

Use photographs with enough original resolution to inspect the key boundaries, different piano models, viewpoints, lighting and backgrounds. Preserve provenance, group related photographs before splitting, and keep a separately reviewed test set. SAM candidates require checking for merged keys, duplicate masks, missing keys and case masks. Every accepted training photograph needs complete labels for all three target classes. The keybed mask intentionally overlaps the individual-key masks; training must preserve that overlap. Real Quest captures are a separate deployment-domain evaluation, not a prerequisite for collecting internet images.

Start with a fixed 640 × 480 scene input and measure key-boundary error. If the full-scene masks lose narrow black keys or white-key seams, compare a higher-resolution or rectified keybed crop before increasing model size. Run inference during setup and preserve the calibrated geometry in world space afterward. Both ONNX numerical parity and actual Quest memory, latency and overlay alignment remain required before replacing the production model. The new data tooling does not itself enable YOLO in the shipped app.

A local export smoke test with Ultralytics 8.4.210 and the official COCO YOLO26n-seg checkpoint produced an 11,123,174-byte ONNX opset 17 file at input `[1,3,480,640]`. Its NMS-free outputs were `[1,300,38]` detection rows and `[1,32,120,160]` mask prototypes, and ONNX Runtime CPU returned finite values on a real Casio photograph. This checks the stock graph and tooling only; it is not a trained three-class piano model or a Sentis/Quest execution result. The twelve-photo Commons sample exercises acquisition and SAM review. In two complete keyboard photos, individual-key proposals included duplicate white keys and missed black keys, so neither set is accepted as complete per-key training labels.

## Initial framing and depth

1. During setup, move back until the camera can see both ends of the playable keys. Look slightly down so the front and back of the keybed are visible. Hold still for the capture, check the outline, and confirm it. If the outline clips or lands on the case, reframe or use manual setup.
2. After confirmation, move back to the playing position. The app deactivates automatic capture and uses the confirmed rectangle in Unity world space for the overlay. Ordinary head motion therefore needs no new segmentation. Recheck alignment after recentering, tracking loss, or moving the instrument. The current session geometry uses Unity world coordinates. Persistent relocalization across launches would require an anchor workflow. [Meta spatial anchors](https://developers.meta.com/vr/documentation/unity/unity-spatial-anchors-overview/)
3. If the complete keybed cannot fit in one view, manual setup already collects three ordered world-space points. You can look toward each corner separately when placing the points. For automatic multi-view calibration, collect overlapping left, middle and right views with each RGB frame's timestamp, camera pose and intrinsics. Project matched keybed evidence into a shared 3D frame using depth, check that the views agree on the same plane, and require both endpoints before accepting a rectangle. This fusion is a separate feature.

Quest 3 has a dedicated depth sensor. Meta exposes estimated per-eye depth maps with projection and pose metadata via the Depth API, and MRUK provides live depth raycasts with hit position, normal and status. The Passthrough Camera API exposes RGB and the RGB camera's own timestamp, pose and intrinsics. The public APIs used here expose depth maps and rays. A raw LiDAR point-cloud stream is unavailable in this path. RGB and depth have their own viewpoints and timing, so depth sampling must respect their projection and poses. MRUK 81's `ViewportPointToRay` uses the current RGB frame timestamp. An asynchronous detector would need to retain that frame's calibration data before processing. [Quest hardware](https://developers.meta.com/vr/blog/build-the-next-generation-of-vr-mr-with-meta-quest-3/), [OpenXR Depth API](https://developers.meta.com/vr/documentation/native/android/mobile-depth/), [MRUK environment raycasting](https://developers.meta.com/vr/documentation/unity/unity-mr-utility-kit-environment-raycast/), [MRUK 81 camera API](https://developers.meta.com/vr/reference/mruk/v81/class_meta_x_r_passthrough_camera_access/)

## What the current detector showed

One saved Quest capture produced four `(x, y, heatmap peak)` tuples in the model's pixel coordinates. The model estimates the white-key surface in the order top-left, top-right, bottom-right, bottom-left. The baseline was recorded in `last-kpts.tensor` during the 1.1.1 setup investigation.

| Corner | x | y | Heatmap peak |
| --- | ---: | ---: | ---: |
| Top-left | 47 | 132 | 0.301474 |
| Top-right | 301 | 116 | 0.592472 |
| Bottom-right | 319 | 141 | 0.031148 |
| Bottom-left | 28 | 165 | 0.637529 |

The bottom-right estimate landed on the last image column with the weakest peak. The paired RGB tensor was unavailable and the Quest was disconnected during this review, so the frame contents and the cause of that estimate could not be verified. The recorded output does show why this candidate needs a retry before placing the overlay.

A separate paired capture from the connected Quest shows the right end of the CT-S200 cut off by the camera frame. The baseline found `(47,156)`, `(309,132)`, `(319,170)` and `(39,199)`. The third corner had a peak of `0.058786`. On the same frame, the SAM 3.1 `piano keys` prompt selected the visible playable strip at 1008 pixels (score `0.651`) and at 672 pixels (top score about `0.67`). Both masks reached the right image edge. The 672-pixel model also returned a lower-scoring mask (about `0.49`) covering the case. The `electronic piano keyboard` prompt favored the case at both sizes. This capture confirms clipping and shows the need to distinguish case masks from playable-key masks. Measuring error on the rightmost corner requires a view in which it is visible. The RGB and mask comparisons were processed locally.

The setup now checks all four corners for clipping, confidence and a usable quadrilateral. The `0.1` minimum heatmap peak is an Inspector setting and an initial heuristic. Tune it against successful and unsuccessful device captures. The app still needs the first three spatial raycasts to define a 3D surface, followed by corner adjustment and confirmation. The manual setup remains available if the camera cannot see the complete keybed. [Meta camera-to-world guidance](https://developers.meta.com/vr/documentation/unity/unity-pca-documentation/)

## Standalone feasibility

- Unsloth provides the Transformers image model at 1.68 GB in BF16 or about 0.99–1.05 GB in INT8. The 5.75 GiB Quest app PSS allowance makes those weight sizes plausible for a prototype. Measure the whole app while weights load and inference runs, including activation buffers, CPU and GPU copies, Unity, rendering and depth. On the PC, the exact BF16 checkpoint at revision `cd35389` loaded in 2.11 seconds. Its first forward took 17.99 seconds and two subsequent forwards took 0.331 and 0.335 seconds on an RTX 5060 Ti with PyTorch CUDA SDPA. Peak CUDA allocator usage was 1.94 GiB allocated and 2.12 GiB reserved. The input was generated rectangles and the outputs were finite. These measurements cover PC execution only. [Unsloth checkpoint](https://huggingface.co/unsloth/sam3.1), [Meta app memory limit](https://developers.meta.com/vr/essentials/memory-ram/), [Meta memory profiling](https://developers.meta.com/vr/blog/getting-a-handle-on-meta-quest-memory-usage/)
- A published ONNX conversion of upstream SAM 3.1 offers a 929 MB image encoder and 123 MB finder at opset 23. Its C# desktop host uses ONNX Runtime with CPU or CUDA. For this keyboard-specific flow, the text embedding for a fixed prompt can be computed at export time, so the large text encoder need not load on Quest. An export still needs operator and precision checks in the intended backend. Unity documents Sentis GPUCompute and CPU on Quest, with no NPU backend, and recommends async readback and scheduling work across frames. Android ARM64 ONNX Runtime is a separate local option if the graph fails Sentis import. [SAM 3.1 ONNX conversion](https://huggingface.co/smile-forge/sam3.1-onnx), [Unity model import](https://docs.unity3d.com/Packages/com.unity.ai.inference@2.4/manual/import-a-model-file.html), [Unity operator support](https://docs.unity3d.com/Packages/com.unity.ai.inference@2.4/manual/supported-operators.html), [Meta Inference Engine guidance](https://developers.meta.com/vr/documentation/unity/unity-pca-sentis/), [ONNX Runtime Android](https://onnxruntime.ai/docs/tutorials/mobile/)
- Qualcomm reports about 1.67 seconds for the vision backbone and 0.40 seconds for the head of its original SAM 3 export on a Snapdragon 8 Gen 3 NPU. The recipe uses Workbench and provides no directly downloadable precompiled graph. Qualcomm's QAIRT SDK also lists XR2 Gen 2 Android and provides local ONNX-to-QNN conversion, so a developer can investigate the backend without a hosted run. This would require a native runtime, graph compatibility and a device smoke test on Horizon OS. Meta's 3.1 H100 speedup measures many-object video tracking. [Qualcomm SAM 3 measurements](https://huggingface.co/qualcomm/Segment-Anything-Model-3), [Qualcomm export recipe](https://github.com/qualcomm/ai-hub-models/blob/main/src/qai_hub_models/models/sam3/README.md), [QAIRT supported targets](https://docs.qualcomm.com/nav/home/QNN_general_overview.html?product=924033590759186372), [Local QNN conversion](https://docs.qualcomm.com/nav/home/onnx_to_qnn_tutorial_linux_host.html?product=924033590759186372), [Meta SAM 3.1 release](https://github.com/facebookresearch/sam3/blob/main/RELEASE_SAM3p1.md)
- A good instrument mask can still include the case or omit some keys. A detector replacement has to produce four ordered playable-surface corners and pass the existing spatial projection and confirmation. A long-running or remote inference path must use the camera pose that goes with the captured frame. [Meta passthrough camera API](https://developers.meta.com/vr/documentation/unity/unity-pca-documentation/)

The first Quest prototype should load SAM only during calibration and release its inference resources once the user confirms. Use an isolated headset build to measure cold load, capture-to-outline latency, maximum app PSS, and dropped frames while moving the head. Compare against the current build using the same headset and piano. A small point- or box-prompted model such as SAM 2.1 Tiny is a fallback experiment if the 3.1 graph cannot meet these requirements. [Qualcomm SAM 2.1 Tiny model](https://huggingface.co/qualcomm/Segment-Anything-Model-2)

### Export of the exact Unsloth checkpoint

The local prototype exports the image encoder and mask finder from `unsloth/sam3.1@cd35389` as two fixed-size FP16 ONNX opset 18 graphs. It bakes in the `piano keys` text embedding. The split PyTorch outputs matched the unsplit FP16 model exactly on the synthetic test frame, and both ONNX graphs passed the ONNX checker.

The 1008 × 1008 encoder is 946 MB and the finder is 98 MB. The original encoder trace contains two `If` nodes that Sentis 2.4.1 does not handle. Both conditions are constant false. Folding them into their identity branches produced a graph that passed the checker. Every remaining operator name is present in the local Sentis 2.4.1 converter source. This is a source-level inventory, so import, backend execution and frame-time behavior still need testing.

The same Sentis package converts ONNX FP16 weights to FP32 constants at import. The two graphs' floating-point initializers would occupy about 1.79 GiB at FP32, before intermediate buffers and rendering. Shape inference shows six 1008-size finder attention outputs with shape `[1, 8, 5184, 5184]`. Each would occupy about 0.80 GiB if materialized at FP32. Encoder global attention has 16 heads, which gives about 1.60 GiB for one FP32 attention score tensor at that resolution. These tensor sizes depend on the graph and are separate from the app's peak PSS, which also depends on kernel fusion, buffer reuse and runtime allocations. [Unity ONNX operator documentation](https://docs.unity3d.com/Packages/com.unity.ai.inference@2.4/manual/supported-operators.html)

The same weights also exported at 672 × 672. The 934 MB encoder and 93 MB finder passed the checker after folding the same two constant branches. The split again matched the unsplit FP16 model at that input size. The finder attention outputs fall to about 0.16 GiB each at FP32, and an encoder global-attention score tensor falls to about 0.32 GiB. The smaller input is the first candidate to profile on Quest. Compare its corner alignment to the 1008-pixel reference on real keyboard frames before adopting it.

### Unity desktop probe at 672 × 672

An isolated Unity 6000.2.8f1 project imported both graphs in Sentis 2.4.1 and executed them with `GPUCompute` on Direct3D 11 and an RTX 5060 Ti. The `ModelAsset` load took 2.915 seconds after asset import. Forward plus output readback took 25.256 seconds on the first run and 0.626 seconds on the second. The import reported that it defaulted `LayerNormalization.axis` to `-1`, which matches the axis on all 121 normalization nodes in the exported graphs.

On a synthetic keyboard-like drawing, all output tensors were finite and had the expected size. Mask-logit signs agreed with FP16 PyTorch for 99.918% of the 7,372,800 values. Only 56,793 reference values were foreground (0.770%), so an all-background result would already agree on 99.230%; sign agreement alone does not establish useful mask accuracy. The mean absolute mask-logit difference was 0.324 and the maximum was 51.36. Normalized box coordinates had mean absolute difference 0.00197 and maximum 0.10449 across 200 queries. These are numerical checks on one synthetic frame. Detection and alignment still need real Quest frames and foreground-mask evaluation.

With cached assets and shaders, a fresh Editor process loaded the models in 1.488 seconds and ran forward plus readback in 1.595 and 0.528 seconds. External phase samples recorded these Windows Editor maxima:

| Phase | Working set | Private bytes |
| --- | ---: | ---: |
| Startup | 1.09 GiB | 1.29 GiB |
| Model load | 4.89 GiB | 5.24 GiB |
| Forward and readback | 6.66 GiB | 10.03 GiB |

These measurements include the Editor and use Windows memory metrics. The Quest app has a separate 5.75 GiB PSS limit. The desktop footprint warrants a separate lightweight Android probe that measures PSS and frame times on the physical headset before integrating SAM in the standalone release. The private probe directory is `%LOCALAPPDATA%\AR-MusicianBuild\output\redesign\sam-evaluation`. It contains the model exports, scripts, operator inventory and the `unity-probe` logs and memory samples.

### Quest 3 device probe at 672 × 672

The connected Quest 3 reported a Snapdragon XR2 Gen 2 (`SXR2230P`), Adreno 740, Android 14 / API 34, and `7,943,084 KiB` system RAM. The private `com.armusician.samprobe` ARM64 test app ran Vulkan compute in a 2D panel using a synthetic input. The encoder and finder used Sentis Float16 weight packing, with 941 MB and 80 MB serialized files respectively. The probe loaded the encoder first and was designed to release it before loading the finder. It scheduled up to 12 layers or about 4 ms of CPU scheduling per batch, then yielded a frame. Output readback was asynchronous.

| Metric | Measured on Quest 3 |
| --- | ---: |
| Idle probe PSS | 240,380 KiB (0.23 GiB) |
| Encoder load and worker creation | 2.897 s |
| Highest watchdog-sampled probe PSS during encoder execution | 3,562,347 KiB (3.40 GiB) |
| System available memory before run | 2,294,016 KiB (2.19 GiB) |
| Lowest watchdog-sampled system available memory | 293,056 KiB (286 MiB) |

Android signaled low memory several times. The external watchdog force-stopped the probe after two samples below 384 MiB of available system memory. The final memory sample was 26.28 seconds after the trigger, and the encoder still had not finished. The reported 3.40 GiB peak is sampled app PSS, below Meta's 5.75 GiB per-app ceiling. Shared system memory was the limiting observation on this run. No full forward, warm run, finder parity or MR frame time was measured. The original Quest app remained installed. The exact immediate post-stop memory reading was not retained in the saved evidence.

The measured Sentis path needs a substantial memory reduction before another standalone trial. A native Android execution provider that preserves low-precision weights and avoids large materialized attention buffers is a separate candidate. Any such port still needs the same device checks and keybed geometry validation. Device logs and memory samples are in `unity-probe\android` under the private probe directory.

A later Android `am_pss` event recorded about 3,631,773 KiB for the probe; the watchdog table is not an exhaustive process peak. The saved system kill event occurred about 31 seconds after `RUN 1 BEGIN`. A separate device check at 22:27:51 local time found both apps installed, no running probe process, and 3,528,628 KiB system memory available. That later check confirms the probe stopped; it is not an immediate post-stop memory measurement.

## Local comparison on the same frame

1. With the Quest connected over ADB, enter automatic piano setup and capture with X. Pull the following pair after each capture, using a separate local directory per attempt. The app overwrites these files on later inferences, including bongos. Check timestamps to confirm both came from the intended piano capture.

   ```powershell
   adb pull /sdcard/Android/data/com.armusician.midi/files/input.tensor .
   adb pull /sdcard/Android/data/com.armusician.midi/files/kpts.tensor .
   ```

2. Use a Python environment with NumPy and Pillow to recover the model image and its four baseline corners. The default Unity tensor layout is top-left RGB in CHW order.

   ```python
   import numpy as np
   from PIL import Image, ImageDraw

   rgb = np.fromfile("input.tensor", dtype="<f4").reshape(3, 240, 320).transpose(1, 2, 0)
   image = Image.fromarray(np.rint(rgb.clip(0, 1) * 255).astype(np.uint8))
   image.save("input.png")
   baseline = image.copy()
   draw = ImageDraw.Draw(baseline)
   for i, (x, y, peak) in enumerate(np.fromfile("kpts.tensor", dtype="<f4").reshape(4, 3)):
       draw.ellipse((x - 3, y - 3, x + 3, y + 3), outline="red", width=2)
       print(i, x, y, peak)
   baseline.save("baseline.png")
   ```

3. Run the Unsloth model card's CUDA example locally on `input.png`. Try `piano keys` and `electronic piano keyboard` as separate prompts. Compare masks against manually marked playable corners before choosing a mask-to-quad method. Keep the camera frames local. [Unsloth inference example](https://huggingface.co/unsloth/sam3.1#usage)
4. Include well-framed, angled, dim, partly occluded and cropped captures. Compare corner error relative to white-key width and record rejected or incorrectly accepted captures. On Quest, separately verify the depth hits and overlay alignment on low, middle and high keys. Compare capture-to-confirmation time and peak app memory if a replacement reaches the headset.

If 2D corners are already accurate and the overlay is displaced, measure the spatial projection before changing models. Exact edge rays can hit outside the keybed. Testing depth samples inside the keybed and projecting the validated corner rays onto a consistent fitted plane would isolate that failure mode. If 2D corners are inaccurate, use verified real Quest annotations to improve a compact corner detector. A PC segmentation run can help label those images.
