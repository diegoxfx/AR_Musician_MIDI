using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Unity.InferenceEngine;
using UnityEngine;
using Debug = UnityEngine.Debug;

public sealed class YoloQuestProbe : MonoBehaviour
{
    [Serializable]
    private sealed class Case
    {
        public string id;
        public int left, top, contentWidth, contentHeight;
    }

    [Serializable]
    private sealed class Config
    {
        public string backend, detectionName, prototypeName;
        public int height, width, rows, columns, protoChannels, protoHeight, protoWidth, classes, repeats;
        public float confidence, sliceBudgetMs;
        public Case[] cases;
    }

    [Serializable]
    private sealed class Frames
    {
        public int count, over25Ms, over50Ms;
        public float p50Ms, p95Ms, maxMs;
    }

    [Serializable]
    private sealed class Inference
    {
        public string id;
        public bool cold;
        public int cycle, acceptedRows, nonemptyMasks;
        public double inputMs, scheduleWallMs, scheduleMainMs, readbackMs, decodeMs, inferenceMs;
        public Frames frames;
    }

    [Serializable]
    private sealed class Report
    {
        public string backend, gpu, graphicsApi, device, os, unity;
        public bool interrupted;
        public double modelLoadMs, workerCreateMs;
        public Frames idleFrames;
        public List<Inference> inference = new();
    }

    private const double TimeoutSeconds = 90;
    private readonly List<float> frameTimes = new(200);
    private string status = "Starting";
    private bool measuring, running, interrupted;

    private void Update()
    {
        if (measuring) frameTimes.Add(Time.unscaledDeltaTime * 1000f);
    }

    private void OnApplicationPause(bool paused)
    {
        if (running && paused)
        {
            interrupted = true;
            Log("INTERRUPTED Android paused the panel");
        }
    }

    private void OnGUI() =>
        GUI.Label(new Rect(24, 24, Screen.width - 48, 80), "Standalone YOLO probe | " + status);

    private IEnumerator Start()
    {
        Application.targetFrameRate = 60;
        Application.runInBackground = true;
        string folder = Application.persistentDataPath;
        string flag = Path.Combine(folder, "run.flag");
        if (File.Exists(flag)) File.Delete(flag);
        status = "Waiting for host";
        Log($"READY path={folder} device={SystemInfo.deviceModel} gpu={SystemInfo.graphicsDeviceName} api={SystemInfo.graphicsDeviceType}");
        while (!File.Exists(flag)) yield return null;
        File.Delete(flag);
        running = true;
        using var run = Run(folder);
        while (true)
        {
            bool pending;
            try { pending = run.MoveNext(); }
            catch (Exception failure)
            {
                status = "Failed";
                Log("FAILED " + failure);
                break;
            }
            if (!pending) break;
            yield return run.Current;
        }
        running = measuring = false;
    }

    private IEnumerator<object> Run(string folder)
    {
        var cfg = JsonUtility.FromJson<Config>(File.ReadAllText(Path.Combine(folder, "run.json")));
        if (cfg.height < 32 || cfg.width < 32 || cfg.rows < 1 || cfg.rows > 300 ||
            cfg.protoChannels < 1 || cfg.columns != 6 + cfg.protoChannels ||
            cfg.protoHeight < 1 || cfg.protoWidth < 1 || cfg.classes < 1 ||
            cfg.cases.Length < 1 || cfg.repeats < 1 || cfg.sliceBudgetMs <= 0f ||
            cfg.confidence < 0f || cfg.confidence > 1f)
            throw new InvalidDataException("Invalid end-to-end model or benchmark configuration");
        if (!Enum.TryParse(cfg.backend, out BackendType backend) ||
            (backend != BackendType.CPU && backend != BackendType.GPUCompute))
            throw new InvalidDataException("Expected CPU or GPUCompute");
        if (backend == BackendType.GPUCompute && !SystemInfo.supportsComputeShaders)
            throw new InvalidOperationException("GPUCompute is unavailable");

        string output = Path.Combine(folder, "run-output");
        Directory.CreateDirectory(output);
        var report = new Report
        {
            backend = cfg.backend, gpu = SystemInfo.graphicsDeviceName, graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
            device = SystemInfo.deviceModel, os = SystemInfo.operatingSystem, unity = Application.unityVersion
        };
        status = "Measuring idle frames";
        BeginFrames();
        yield return new WaitForSecondsRealtime(1);
        report.idleFrames = EndFrames();
        status = "Loading model";
        Log("LOAD_BEGIN");
        var stage = Stopwatch.StartNew();
        var model = ModelLoader.Load(Path.Combine(folder, "model.sentis"));
        report.modelLoadMs = stage.Elapsed.TotalMilliseconds;
        stage.Restart();
        using var worker = new Worker(model, backend);
        report.workerCreateMs = stage.Elapsed.TotalMilliseconds;
        Log($"LOAD_END model_ms={report.modelLoadMs:F2} worker_ms={report.workerCreateMs:F2}");
        yield return null;
        for (int cycle = 0; cycle < cfg.repeats; cycle++)
        foreach (var item in cfg.cases)
        {
            if (item.left < 0 || item.top < 0 || item.contentWidth < 1 || item.contentHeight < 1 ||
                item.left + item.contentWidth > cfg.width || item.top + item.contentHeight > cfg.height)
                throw new InvalidDataException("Invalid letterbox rectangle: " + item.id);
            status = $"{cfg.backend} | {item.id} | pass {cycle + 1}";
            var result = new Inference { id = item.id, cycle = cycle, cold = report.inference.Count == 0 };
            Log($"INFERENCE_BEGIN id={item.id} cycle={cycle} cold={result.cold}");
            stage.Restart();
            using var input = new Tensor<float>(new TensorShape(1, 3, cfg.height, cfg.width),
                ReadFloats(Path.Combine(folder, item.id + ".f32"), 3 * cfg.height * cfg.width));
            result.inputMs = stage.Elapsed.TotalMilliseconds;
            // Allow the frame containing disk input to finish before measuring inference pacing.
            yield return null;
            BeginFrames();
            stage.Restart();
            var work = worker.ScheduleIterable(input);
            var slice = new Stopwatch();
            bool done = false;
            while (!done)
            {
                slice.Restart();
                do
                {
                    if (!work.MoveNext()) { done = true; break; }
                } while (slice.Elapsed.TotalMilliseconds < cfg.sliceBudgetMs);
                result.scheduleMainMs += slice.Elapsed.TotalMilliseconds;
                CheckTimeout(stage);
                if (!done) yield return null;
            }
            result.scheduleWallMs = stage.Elapsed.TotalMilliseconds;
            var det = (Tensor<float>)worker.PeekOutput(cfg.detectionName);
            var proto = (Tensor<float>)worker.PeekOutput(cfg.prototypeName);
            if (!det.shape.Equals(new TensorShape(1, cfg.rows, cfg.columns)) ||
                !proto.shape.Equals(new TensorShape(1, cfg.protoChannels, cfg.protoHeight, cfg.protoWidth)))
                throw new InvalidDataException("Sentis output shapes differ from ORT");
            var download = Stopwatch.StartNew();
            det.ReadbackRequest();
            proto.ReadbackRequest();
            float[] boxes = null, prototypes = null;
            while (true)
            {
                // AsyncGPUReadback data is only retained briefly. Copy each tensor
                // as soon as it completes, even if the other request is still pending.
                if (boxes == null && det.IsReadbackRequestDone()) boxes = det.DownloadToArray();
                if (prototypes == null && proto.IsReadbackRequestDone()) prototypes = proto.DownloadToArray();
                if (boxes != null && prototypes != null) break;
                CheckTimeout(stage);
                yield return null;
            }
            result.readbackMs = download.Elapsed.TotalMilliseconds;
            var decode = Stopwatch.StartNew();
            var masks = Decode(cfg, item, boxes, prototypes, result, cycle == 0);
            result.decodeMs = decode.Elapsed.TotalMilliseconds;
            result.inferenceMs = stage.Elapsed.TotalMilliseconds;
            // Capture the frame that includes final readback/decode.
            yield return null;
            result.frames = EndFrames();
            report.inference.Add(result);
            Log($"INFERENCE_END id={item.id} cycle={cycle} inference_ms={result.inferenceMs:F2} max_frame_ms={result.frames.maxMs:F2}");
            if (cycle == 0)
            {
                WriteFloats(Path.Combine(output, item.id + ".det.f32"), boxes);
                WriteFloats(Path.Combine(output, item.id + ".proto.f32"), prototypes);
                File.WriteAllBytes(Path.Combine(output, item.id + ".masks"), masks);
            }
            yield return null;
        }
        report.interrupted = interrupted;
        File.WriteAllText(Path.Combine(output, "benchmark.json"), JsonUtility.ToJson(report, true));
        status = interrupted ? "Completed with pause" : "Complete";
        Log(interrupted ? "COMPLETED_INTERRUPTED" : "ALL_PASS");
    }

    // Each accepted row uses an input-resolution bitset. Threshold interpolated logits at zero
    // (sigmoid > 0.5), then crop to the row's box and the exact unpadded letterbox rectangle.
    private static byte[] Decode(Config cfg, Case item, float[] det, float[] proto, Inference result, bool save)
    {
        foreach (float value in det)
            if (!float.IsFinite(value)) throw new InvalidDataException("Nonfinite detection");
        foreach (float value in proto)
            if (!float.IsFinite(value)) throw new InvalidDataException("Nonfinite prototype");
        int plane = cfg.protoWidth * cfg.protoHeight;
        int stride = (cfg.width * cfg.height + 7) / 8;
        var logs = new float[plane];
        using var packed = new MemoryStream();
        using var writer = new BinaryWriter(packed);
        for (int row = 0; row < cfg.rows; row++)
        {
            int start = row * cfg.columns;
            if (det[start + 4] <= cfg.confidence) continue;
            float cls = det[start + 5];
            if (cls != (int)cls || cls < 0 || cls >= cfg.classes)
                throw new InvalidDataException("Invalid detection class");
            result.acceptedRows++;
            int x1 = Mathf.CeilToInt(Mathf.Clamp(det[start], item.left, item.left + item.contentWidth));
            int y1 = Mathf.CeilToInt(Mathf.Clamp(det[start + 1], item.top, item.top + item.contentHeight));
            int x2 = Mathf.CeilToInt(Mathf.Clamp(det[start + 2], item.left, item.left + item.contentWidth));
            int y2 = Mathf.CeilToInt(Mathf.Clamp(det[start + 3], item.top, item.top + item.contentHeight));
            var bits = save ? new byte[stride] : null;
            int active = 0;
            if (x1 < x2 && y1 < y2)
            {
                float X(int x) => Mathf.Clamp((x + 0.5f) * cfg.protoWidth / cfg.width - 0.5f, 0, cfg.protoWidth - 1);
                float Y(int y) => Mathf.Clamp((y + 0.5f) * cfg.protoHeight / cfg.height - 0.5f, 0, cfg.protoHeight - 1);
                for (int y = (int)Y(y1); y <= Mathf.CeilToInt(Y(y2 - 1)); y++)
                for (int x = (int)X(x1); x <= Mathf.CeilToInt(X(x2 - 1)); x++)
                {
                    int p = y * cfg.protoWidth + x;
                    float sum = 0;
                    for (int c = 0; c < cfg.protoChannels; c++)
                        sum += det[start + 6 + c] * proto[c * plane + p];
                    logs[p] = sum;
                }
                for (int y = y1; y < y2; y++)
                {
                    float fy = Y(y);
                    int a = (int)fy, b = Mathf.CeilToInt(fy);
                    for (int x = x1; x < x2; x++)
                    {
                        float fx = X(x);
                        int l = (int)fx, r = Mathf.CeilToInt(fx);
                        float first = Mathf.LerpUnclamped(logs[a * cfg.protoWidth + l], logs[a * cfg.protoWidth + r], fx - l);
                        float last = Mathf.LerpUnclamped(logs[b * cfg.protoWidth + l], logs[b * cfg.protoWidth + r], fx - l);
                        if (Mathf.LerpUnclamped(first, last, fy - a) <= 0) continue;
                        active++;
                        int index = y * cfg.width + x;
                        if (bits != null) bits[index / 8] |= (byte)(1 << (index % 8));
                    }
                }
            }
            if (active > 0) result.nonemptyMasks++;
            if (bits != null)
            {
                writer.Write(row);
                writer.Write(bits);
            }
        }
        return save ? packed.ToArray() : null;
    }

    private void BeginFrames()
    {
        frameTimes.Clear();
        measuring = true;
    }

    private Frames EndFrames()
    {
        measuring = false;
        frameTimes.Sort();
        int count = frameTimes.Count;
        return new Frames
        {
            count = count,
            p50Ms = Percentile(0.50f), p95Ms = Percentile(0.95f), maxMs = Percentile(1f),
            over25Ms = frameTimes.FindAll(ms => ms > 25).Count, over50Ms = frameTimes.FindAll(ms => ms > 50).Count
        };
    }

    private float Percentile(float p) => frameTimes.Count == 0 ? 0 :
        frameTimes[Mathf.CeilToInt(p * frameTimes.Count) - 1];

    private static void CheckTimeout(Stopwatch clock)
    {
        if (clock.Elapsed.TotalSeconds > TimeoutSeconds)
            throw new TimeoutException("Inference exceeded the per-image deadline");
    }

    private static float[] ReadFloats(string path, int count)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length != count * sizeof(float)) throw new InvalidDataException(path);
        var result = new float[count];
        Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
        return result;
    }

    private static void WriteFloats(string path, float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        File.WriteAllBytes(path, bytes);
    }

    private static void Log(string message) => Debug.Log("YOLO_PROBE " + message);
}
