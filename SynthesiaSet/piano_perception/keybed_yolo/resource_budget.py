"""Cooperative GPU scheduling and a shared Windows CPU cap for the local pilot.

Only explicitly attached pilot processes are changed. No system power/clock settings
or other applications are modified. GPU utilization is sampled, not a hard CUDA cap.
"""

import argparse
from contextlib import contextmanager
import ctypes
from ctypes import wintypes
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import time

import psutil

JOB_NAME = r"Local\KeytrailPilotCpuBudget"
CPU_CAP = 0.40
GPU_DUTY = 0.70
GLOBAL_TARGET = 0.80
VRAM_FRACTION = 0.70
_HANDLES = []
POLICY_PATH = Path(os.environ.get("KEYTRAIL_RESOURCE_POLICY", str(
    Path(os.environ.get("LOCALAPPDATA", str(Path.home()))) / "Keytrail" / "resource-policy.json"
)))


def cpu_profile():
    if not POLICY_PATH.exists():
        return "gaming"
    value = json.loads(POLICY_PATH.read_text(encoding="utf-8-sig")).get("cpu_profile", "gaming")
    if value not in ("gaming", "normal"):
        raise ValueError("CPU profile must be gaming or normal")
    return value


def allowed_cpus(count, inherited=None, profile="gaming"):
    """Leave logical processor 0 unused, and spare its sibling on this 16-thread PC."""
    if count < 2:
        raise ValueError("cannot exclude CPU 0 on a single-processor system")
    if profile not in ("gaming", "normal"):
        raise ValueError("unknown CPU profile")
    # Fix the shared pool before intersecting inherited affinity: separate worker
    # trees must not independently choose disjoint six-core pools.
    candidates = (list(range(2 if count >= 4 else 1, count))[:max(1, math.floor(count * CPU_CAP))]
                  if profile == "gaming" else list(range(1, count)))
    selected = [index for index in candidates if inherited is None or index in inherited]
    if not selected:
        raise ValueError("inherited affinity has no allowed processor other than CPU 0")
    return selected


class CpuRate(ctypes.Structure):
    _fields_ = [("ControlFlags", wintypes.DWORD), ("CpuRate", wintypes.DWORD)]


def windows_job(pid, cap=CPU_CAP):
    """Join the same named job so multiple workers share one aggregate CPU ceiling."""
    if os.name != "nt":
        return {"windows_hard_cap": False}
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
    kernel.CreateJobObjectW.restype = wintypes.HANDLE
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.SetInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
    kernel.QueryInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD, ctypes.c_void_p]
    kernel.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
    kernel.IsProcessInJob.argtypes = [wintypes.HANDLE, wintypes.HANDLE, ctypes.POINTER(wintypes.BOOL)]
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    job = kernel.CreateJobObjectW(None, JOB_NAME)
    if not job:
        raise ctypes.WinError(ctypes.get_last_error())
    process = None
    try:
        rate = CpuRate(0x1 | 0x4, round(10000 * cap)) if cap is not None else CpuRate(0, 0)
        if not kernel.SetInformationJobObject(job, 15, ctypes.byref(rate), ctypes.sizeof(rate)):
            raise ctypes.WinError(ctypes.get_last_error())
        if cap is not None:
            process = kernel.OpenProcess(0x0100 | 0x0001 | 0x0400, False, pid)
            if not process:
                raise ctypes.WinError(ctypes.get_last_error())
            present = wintypes.BOOL()
            if not kernel.IsProcessInJob(process, job, ctypes.byref(present)):
                raise ctypes.WinError(ctypes.get_last_error())
            if not present.value and not kernel.AssignProcessToJobObject(job, process):
                raise ctypes.WinError(ctypes.get_last_error())
        actual = CpuRate()
        if not kernel.QueryInformationJobObject(job, 15, ctypes.byref(actual), ctypes.sizeof(actual), None):
            raise ctypes.WinError(ctypes.get_last_error())
        _HANDLES.append(job)  # Keep alive; never set KILL_ON_JOB_CLOSE.
        return {"windows_hard_cap": actual.ControlFlags == 5,
                "cpu_cap": actual.CpuRate / 10000 if actual.ControlFlags == 5 else None}
    except BaseException:
        kernel.CloseHandle(job)
        raise
    finally:
        if process:
            kernel.CloseHandle(process)


def constrain_cpu(pid=None, profile=None):
    process = psutil.Process(pid)
    profile = profile or cpu_profile()
    # Normal is an explicit user choice to remove our temporary affinity cap.
    inherited = process.cpu_affinity() if profile == "gaming" else None
    cpus = allowed_cpus(psutil.cpu_count() or 1, inherited, profile)
    process.cpu_affinity(cpus)
    if os.name == "nt":
        process.nice(psutil.BELOW_NORMAL_PRIORITY_CLASS if profile == "gaming" else psutil.NORMAL_PRIORITY_CLASS)
    elif profile == "gaming":
        process.nice(10)
    record = {"pid": process.pid, "cpu_profile": profile, "affinity": process.cpu_affinity(),
              "cpu_0_excluded": 0 not in process.cpu_affinity()}
    try:
        record.update(windows_job(process.pid, CPU_CAP if profile == "gaming" else None))
    except OSError as error:
        # Affinity still limits this worker to <=40% of this machine's logical CPUs.
        record.update({"windows_hard_cap": False, "job_error": str(error)})
    return record


def duty_pause(active_seconds, fraction=GPU_DUTY):
    if active_seconds < 0 or not 0 < fraction < 1:
        raise ValueError("invalid active duration or duty fraction")
    return active_seconds * (1 / fraction - 1)


class ResourceBudget:
    def __init__(self, log_path=None, gpu_target=GLOBAL_TARGET, duty=GPU_DUTY):
        if not 0 < duty <= gpu_target <= GLOBAL_TARGET:
            raise ValueError("GPU duty must not exceed the requested 80% ceiling")
        self.gpu_target, self.duty = gpu_target, duty
        self.log_path = Path(log_path) if log_path else None
        self.last_poll = 0.0
        self.torch = None
        self.device = None
        self.cpu = constrain_cpu()
        psutil.cpu_percent(None)
        self.record("configured", **self.cpu, gpu_duty=duty, global_target=gpu_target)

    def record(self, event, **values):
        if self.log_path:
            self.log_path.parent.mkdir(parents=True, exist_ok=True)
            with self.log_path.open("a", encoding="utf-8") as output:
                output.write(json.dumps({"utc": time.time(), "event": event, **values}) + "\n")

    def configure_torch(self, device="cuda:0"):
        import torch
        torch.set_num_threads(min(4 if self.cpu["cpu_profile"] == "gaming" else 8, len(self.cpu["affinity"])))
        try:
            torch.set_num_interop_threads(1)
        except RuntimeError:
            pass  # Already initialized; intra-op and process affinity still apply.
        self.torch, self.device = torch, torch.device(device)
        if self.device.type == "cuda":
            if self.device.index is None:
                self.device = torch.device("cuda", torch.cuda.current_device())
            torch.cuda.set_per_process_memory_fraction(VRAM_FRACTION, self.device)
            self.telemetry()  # Fail before GPU work if measured headroom is unavailable.
        self.record("torch", device=str(self.device), threads=torch.get_num_threads(), vram_fraction=VRAM_FRACTION)

    def telemetry(self):
        cpu = psutil.cpu_percent(None) / 100
        if self.device is None or self.device.type != "cuda":
            return {"cpu": cpu, "gpu": 0.0, "vram": 0.0}
        executable = shutil.which("nvidia-smi")
        if not executable:
            raise RuntimeError("nvidia-smi is required to measure the shared GPU budget")
        index = self.device.index if self.device.index is not None else 0
        result = subprocess.run(
            [executable, f"--id={index}", "--query-gpu=utilization.gpu,memory.used,memory.total", "--format=csv,noheader,nounits"],
            capture_output=True, text=True, check=True, timeout=5,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
        )
        gpu, used, total = map(float, result.stdout.strip().split(","))
        return {"cpu": cpu, "gpu": gpu / 100, "vram": used / total}

    def refresh_cpu_profile(self):
        profile = cpu_profile()
        if profile != self.cpu["cpu_profile"]:
            self.cpu = constrain_cpu(profile=profile)
            if self.torch is not None:
                self.torch.set_num_threads(min(4 if profile == "gaming" else 8, len(self.cpu["affinity"])))
            self.record("cpu_profile_changed", **self.cpu)

    def wait_for_headroom(self):
        self.refresh_cpu_profile()
        if time.monotonic() - self.last_poll < 2:
            return
        paused = False
        while True:
            self.refresh_cpu_profile()
            values = self.telemetry()
            self.last_poll = time.monotonic()
            busy = ((self.cpu["cpu_profile"] == "gaming" and values["cpu"] > GLOBAL_TARGET)
                    or values["gpu"] > self.gpu_target or values["vram"] > GLOBAL_TARGET)
            self.record("headroom_wait" if busy else "headroom", **values)
            if not busy:
                if paused:
                    print("Resource budget: resuming at a safe operation boundary", flush=True)
                return
            if not paused:
                print("Resource budget: waiting for CPU/GPU headroom", flush=True)
                paused = True
            if self.torch is not None and self.device.type == "cuda":
                self.torch.cuda.empty_cache()
            time.sleep(2)

    def synchronize(self):
        if self.torch is not None and self.device.type == "cuda":
            self.torch.cuda.synchronize(self.device)

    @contextmanager
    def operation(self):
        self.wait_for_headroom()
        start = time.monotonic()
        try:
            yield
        finally:
            self.synchronize()
            active = time.monotonic() - start
            pause = duty_pause(active, self.duty)
            time.sleep(pause)
            self.record("operation", active_seconds=active, rest_seconds=pause)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--attach", type=int, help="explicit owned PID; existing descendants are included")
    args = parser.parse_args()
    root = psutil.Process(args.attach)
    processes = [root, *root.children(recursive=True)]
    records = []
    for process in processes:
        try:
            records.append(constrain_cpu(process.pid))
        except psutil.NoSuchProcess:
            pass
    print(json.dumps(records, indent=2))


if __name__ == "__main__":
    main()
