# Isolated Quest YOLO benchmark

This Unity 6000.2.8f1 / Inference Engine 2.4.1 Android panel exercises the fixed float32, batch-1, NMS-free YOLO26 segmentation export. Its package is `com.armusician.yoloprobe`. The APK does not contain model weights or photos and does not share data with `com.armusician.midi`. It does not use passthrough or OpenXR; its frame measurements describe the test panel, so they cannot establish pacing or alignment in the production app.

Build into a private work directory outside this source tree. Use the local resource guard when the host is sharing CPU time with other workloads. Import preserves FP32 weights and saves a serialized Sentis graph plus hashes of the ONNX, Sentis model, and import log. Build once and use `-ImportOnly` for each additional model; leave the original inputs and existing reports intact.

```powershell
$probe = 'C:/private/keybed-yolo/quest-probe'
$editor = 'C:/path/to/6000.2.8f1/Editor/Unity.exe'
$onnx = 'C:/private/keybed-yolo/pilot/export/keybed.onnx'
$sentis = "$probe/models/pilot.sentis"
./Build-QuestProbe.ps1 -Unity $editor -Workspace $probe -Onnx $onnx -Sentis $sentis
```

Use a Python environment with NumPy, OpenCV, Pillow, SciPy and ONNX Runtime. Prepare fixtures from the **reviewed packed dataset** so the harness verifies the original file hash and uses only the held-out test split. For a compatibility smoke test with the stock COCO graph, supply `--smoke-image PATH [PATH ...]` instead of `--dataset`; it carries no piano accuracy evidence. `--max-cases N` takes the first N test case IDs in sorted order and is useful for a short smoke run. Omit it for the full test split. Model accuracy against labels is evaluated separately.

```powershell
python -B ./reference.py prepare --model $onnx --dataset 'C:/private/keybed-yolo/pilot/dataset' --selection 'C:/private/keybed-yolo/pilot/val-480x640/selection.json' --output "$probe/fixtures"
```

Reviewed fixtures require the frozen validation selection for the exact dataset, checkpoint, ONNX and input shape. They inherit its confidence threshold and reject a manual `--confidence` override. Smoke fixtures alone accept `--confidence` (default 0.25). Fixture provenance binds the selection and configuration hashes; regenerate older fixtures that lack this binding.

Use the target Quest's ADB serial, the Unity editor's Android SDK tools, and a new results directory per backend. Keep the headset awake and the panel visible. Install the isolated package once and omit `-Install` for runs using the same APK. The runner verifies the package ID and on-device APK, model, config and input hashes before triggering work.

```powershell
$sdk = Join-Path (Split-Path $editor) 'Data/PlaybackEngines/AndroidPlayer/SDK'
$adb = Join-Path $sdk 'platform-tools/adb.exe'
$aapt = Get-ChildItem (Join-Path $sdk 'build-tools') -Filter aapt.exe -Recurse -File |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
$common = @{
    Adb = $adb; Aapt = $aapt; Serial = 'QUEST_ADB_SERIAL'
    Apk = "$probe/android/AR-Musician-YOLO-Probe.apk"
    Model = $sentis; Fixture = "$probe/fixtures"
}
./Run-QuestProbe.ps1 @common -Backend GPUCompute -Results "$probe/results/gpu" -Install
./Run-QuestProbe.ps1 @common -Backend CPU -Results "$probe/results/cpu"

python -B ./reference.py compare --reference "$probe/fixtures" --actual "$probe/results/gpu/device" --report "$probe/results/gpu/parity.json"
python -B ./reference.py summarize --results "$probe/results/gpu" --report "$probe/results/gpu/timing.json"
```

Repeat both report commands for the CPU results. The run records model-load time, worker creation, fixture read time, time to schedule the graph across frames, async readback, mask decoding, and total inference time from scheduling through decoding. Fixture preparation happens on the host, so capture, image conversion and resizing are outside the device timing. Cold means first inference in a fresh app process; it excludes Android installation and startup. Warm uses the same worker, with repeated passes over the selected photos. Both backend selections use a default 4 ms scheduling budget per frame; a single layer may exceed it and GPUCompute may run individual operators on the CPU. Frame deltas come from Unity's panel updates. PSS is sampled via ADB and can miss brief peaks or device allocations absent from app PSS. A paused panel is marked interrupted.

Keep the entire results directory: comparison requires the parent `device-summary.json` receipt and `run.json`, checks the model, fixture, operating point and output hashes, and rejects incomplete or interrupted runs. Detached tensor files cannot establish a device result. Timing reports enforce the same receipt and cycle checks.

Device and host receive the same centered, 114-padded RGB CHW float32 tensor. A detection row gives XYXY, confidence, class ID, and coefficients for the mask prototypes. The decoder computes mask logits, bilinearly upsamples with half-pixel coordinates, crops to the box and the exact content rectangle, then thresholds logits at zero. The saved masks use the input canvas; `left`, `top`, `contentWidth`, `contentHeight`, and original dimensions map the unpadded coordinates back to the photo. The parity report compares float32 prototypes, confidence-filtered same-class boxes, and decoded binary masks against ONNX Runtime. It finds the largest one-to-one assignment among same-class rows that meet the box, confidence and mask tolerances. Rows with equal boxes and scores can therefore be matched by their masks in either TopK order. A no-mask run can check execution and prototypes but cannot establish mask parity.

This benchmark does not confirm individual key completeness, boundary error against annotations, or any camera-to-world placement. For deployment, measure those on reviewed photographs and actual headset camera captures, then profile the production scene separately.
