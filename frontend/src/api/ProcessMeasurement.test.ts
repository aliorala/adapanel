import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { apiClient, invalidateSessionRequests } from "./client";
import { getProcessSnapshot, type ProcessSnapshot } from "./processes";
const originalAdapter = apiClient.defaults.adapter;
const snapshot = (): ProcessSnapshot => ({ capturedAtUtc: "2026-10-04T10:00:00Z", sampleWindowSeconds: 2, cpuBaselineReady: true, totalCpuPercent: 0, logicalProcessorCount: 4,
  totalMemoryBytes: 1000, usedMemoryBytes: 200, availableMemoryBytes: 800, memoryUsagePercent: 20, totalProcessCount: 0, sortBy: "cpu", processes: [] });
let data: unknown, success: boolean;
beforeEach(() => { invalidateSessionRequests(); vi.stubGlobal("window", { location: { pathname: "/panel" } }); data = snapshot(); success = true;
  apiClient.defaults.adapter = async config => ({ config, status: 200, statusText: "OK", headers: {}, data: { success, data } }); });
afterEach(() => { apiClient.defaults.adapter = originalAdapter; vi.unstubAllGlobals(); });
it.each(["false-envelope", "null", "missing-array", "invalid-baseline", "wrong-sort", "invalid-number"])("rejects an unmeasured process response: %s", async kind => {
  if (kind === "false-envelope") success = false;
  else if (kind === "null") data = null;
  else data = { ...snapshot(), ...(kind === "missing-array" ? { processes: undefined } : kind === "invalid-baseline" ? { cpuBaselineReady: undefined } : kind === "wrong-sort" ? { sortBy: "memory" } : { totalCpuPercent: Number.NaN }) };
  await expect(getProcessSnapshot()).rejects.toThrow();
});
it.each([true, false])("accepts a valid CPU sample with baselineReady=%s", async cpuBaselineReady => {
  data = { ...snapshot(), cpuBaselineReady }; expect((await getProcessSnapshot()).cpuBaselineReady).toBe(cpuBaselineReady);
});
