"""Resource regressions: excluded cores, cooldown and cooperative pause boundaries."""

import unittest
from types import SimpleNamespace
from unittest.mock import Mock, patch

from resource_budget import ResourceBudget, allowed_cpus, duty_pause


class BudgetTests(unittest.TestCase):
    def test_cpu_zero_excluded_and_all_workers_share_six_cores(self):
        self.assertEqual(allowed_cpus(16), [2, 3, 4, 5, 6, 7])
        self.assertEqual(allowed_cpus(16, range(2, 10)), [2, 3, 4, 5, 6, 7])
        self.assertEqual(allowed_cpus(16, [4, 5]), [4, 5])
        self.assertEqual(allowed_cpus(2), [1])
        with self.assertRaises(ValueError):
            allowed_cpus(1)
        with self.assertRaises(ValueError):
            allowed_cpus(16, [0])
        with self.assertRaises(ValueError):
            allowed_cpus(16, range(8, 16))
        self.assertEqual(allowed_cpus(16, profile="normal"), list(range(1, 16)))

    def test_active_and_rest_time_stay_within_duty_budget(self):
        for active in (0.02, 0.5, 20):
            rest = duty_pause(active, 0.7)
            self.assertAlmostEqual(active / (active + rest), 0.7)
        for active, fraction in ((-1, .7), (1, 0), (1, 1)):
            with self.assertRaises(ValueError):
                duty_pause(active, fraction)

    def test_cuda_alias_resolves_current_device_before_memory_limit(self):
        def device(name, index=None):
            kind, _, explicit = name.partition(":")
            return SimpleNamespace(type=kind, index=int(explicit) if explicit else index)

        for requested, expected_index in (("cuda", 2), ("cuda:1", 1), ("cpu", None)):
            with self.subTest(device=requested):
                budget = self.budget_without_host_side_effects()
                torch = Mock()
                torch.device.side_effect = device
                torch.cuda.current_device.return_value = 2
                with patch.dict("sys.modules", {"torch": torch}), \
                     patch.object(budget, "telemetry") as telemetry:
                    budget.configure_torch(requested)
                self.assertEqual(budget.device.index, expected_index)
                if requested == "cpu":
                    torch.cuda.set_per_process_memory_fraction.assert_not_called()
                    telemetry.assert_not_called()
                else:
                    torch.cuda.set_per_process_memory_fraction.assert_called_once_with(.7, budget.device)
                    telemetry.assert_called_once()
                self.assertEqual(torch.cuda.current_device.call_count, int(requested == "cuda"))

    def budget_without_host_side_effects(self):
        budget = ResourceBudget.__new__(ResourceBudget)
        budget.last_poll, budget.gpu_target = 0.0, 0.8
        budget.log_path, budget.torch, budget.device = None, None, None
        budget.cpu = {"cpu_profile": "gaming", "affinity": list(range(2, 8))}
        policy = patch("resource_budget.cpu_profile", return_value="gaming")
        policy.start()
        self.addCleanup(policy.stop)
        return budget

    def test_cpu_or_gpu_or_memory_pressure_prevents_next_operation(self):
        budget = self.budget_without_host_side_effects()
        samples = [dict(cpu=.9, gpu=.1, vram=.1), dict(cpu=.1, gpu=.9, vram=.1),
                   dict(cpu=.1, gpu=.1, vram=.9), dict(cpu=.3, gpu=.4, vram=.3)]
        with patch.object(budget, "telemetry", side_effect=samples) as sample, \
             patch("resource_budget.time.monotonic", return_value=10), \
             patch("resource_budget.time.sleep") as sleep:
            budget.wait_for_headroom()
        self.assertEqual(sample.call_count, 4)
        self.assertEqual(sleep.call_count, 3)

    def test_telemetry_failure_does_not_start_work(self):
        budget = self.budget_without_host_side_effects()
        with patch.object(budget, "telemetry", side_effect=RuntimeError("no metrics")), \
             patch("resource_budget.time.monotonic", return_value=10):
            with self.assertRaises(RuntimeError):
                with budget.operation():
                    self.fail("operation must not start without telemetry")

    def test_failed_operation_still_rests_after_gpu_completion(self):
        budget = self.budget_without_host_side_effects()
        budget.duty = .7
        events = []
        with patch.object(budget, "wait_for_headroom"), \
             patch.object(budget, "synchronize", side_effect=lambda: events.append("sync")), \
             patch("resource_budget.time.monotonic", side_effect=[10, 11]), \
             patch("resource_budget.time.sleep", side_effect=lambda _: events.append("rest")):
            with self.assertRaisesRegex(ValueError, "failure"):
                with budget.operation():
                    raise ValueError("failure")
        self.assertEqual(events, ["sync", "rest"])

    def test_cpu_normal_request_releases_an_existing_cpu_pressure_wait(self):
        budget = self.budget_without_host_side_effects()
        mode = ["gaming"]
        def user_finishes_game(_):
            mode[0] = "normal"
        restored = {"cpu_profile": "normal", "affinity": list(range(1, 16)), "cpu_0_excluded": True}
        with patch("resource_budget.cpu_profile", side_effect=lambda: mode[0]), \
             patch("resource_budget.constrain_cpu", return_value=restored) as configure, \
             patch.object(budget, "telemetry", return_value=dict(cpu=.9, gpu=.1, vram=.1)) as sample, \
             patch("resource_budget.time.monotonic", return_value=10), \
             patch("resource_budget.time.sleep", side_effect=user_finishes_game) as sleep:
            budget.wait_for_headroom()
        self.assertEqual(sample.call_count, 2)
        self.assertEqual(sleep.call_count, 1)
        configure.assert_called_once_with(profile="normal")
        self.assertTrue(budget.cpu["cpu_0_excluded"])


if __name__ == "__main__":
    unittest.main()
