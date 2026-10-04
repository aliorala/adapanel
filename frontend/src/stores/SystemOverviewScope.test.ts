import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { apiClient, invalidateSessionRequests } from "../api/client";
import { useSystemStore } from "./system";
import { useAuthStore } from "./auth";
import type { AxiosResponse, InternalAxiosRequestConfig } from "axios";
const originalAdapter = apiClient.defaults.adapter;
const overview = () => ({ isRestricted: false, machineName: "private-server", operatingSystem: "Windows", osArchitecture: "x64", frameworkDescription: ".NET", panelVersion: "0.8.77", currentServerTimeUtc: "2026-10-04T10:00:00Z", bootTimeUtc: "2026-10-04T00:00:00Z", uptime: "10:00:00", cpuUsagePercent: 10, logicalProcessorCount: 4, totalMemoryBytes: 1000, availableMemoryBytes: 800, usedMemoryBytes: 200, memoryUsagePercent: 20, disks: [], networkInterfaces: [], cpuMeasured: true, memoryMeasured: true, disksMeasured: true, networkMeasured: true });
function response(config: InternalAxiosRequestConfig, data: unknown): AxiosResponse { return { config, data: { success: true, data }, headers: {}, status: 200, statusText: "OK" }; }
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
beforeEach(() => { setActivePinia(createPinia()); invalidateSessionRequests(); vi.stubGlobal("window", { location: { pathname: "/panel" } });
  useAuthStore().session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "admin-one", userName: "admin", email: "admin@example.test", role: "Administrator", preferredLanguage: "en", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } }; });
afterEach(() => { apiClient.defaults.adapter = originalAdapter; vi.unstubAllGlobals(); });
it.each([undefined, null, "false", 0])("requires an explicit boolean scope in the overview response (%s)", async isRestricted => {
  apiClient.defaults.adapter = async config => response(config, { ...overview(), isRestricted });
  await expect(useSystemStore().fetchOverview()).rejects.toThrow(); expect(useSystemStore().overviewConfirmed).toBe(false);
});
it.each([true, false])("preserves the measured server scope flag (%s)", async isRestricted => {
  apiClient.defaults.adapter = async config => response(config, { ...overview(), isRestricted }); await useSystemStore().fetchOverview();
  expect((useSystemStore().overview as unknown as { isRestricted: boolean }).isRestricted).toBe(isRestricted);
});
it.each(["actor", "role"])("does not publish an old full overview after %s changed without a new request", async change => {
  const pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig; apiClient.defaults.adapter = config => (request = config, pending.promise);
  const reading = useSystemStore().fetchOverview(); await Promise.resolve(); await Promise.resolve();
  if (change === "actor") useAuthStore().session!.user!.id = "admin-two"; else useAuthStore().session!.user!.role = "Customer";
  pending.resolve(response(request, overview())); await reading; expect(useSystemStore().overview).toBeNull(); expect(useSystemStore().overviewConfirmed).toBe(false);
});
