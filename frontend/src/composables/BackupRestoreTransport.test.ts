import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { effectScope, nextTick, type EffectScope } from "vue";
import type { AxiosResponse, InternalAxiosRequestConfig } from "axios";
import { apiClient, invalidateSessionRequests } from "../api/client";
import { restoreBackup, type BackupRecord } from "../api/backup";
import { useAuthStore } from "../stores/auth";
import { useBackupRestore } from "./useBackupRestore";

const dialogs = vi.hoisted(() => ({ confirm: vi.fn(), prompt: vi.fn() }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: dialogs.confirm }) }));
vi.mock("../components/PromptDialog.vue", () => ({ usePrompt: () => ({ prompt: dialogs.prompt }) }));
vi.mock("../router", () => ({ default: { push: vi.fn() } }));
const record = (): BackupRecord => ({ id: "one", name: "alpha.zip", type: "site", source: "alpha.test", path: "/backups/alpha.zip", format: "zip", createdAtUtc: "2026-10-04T12:00:00Z", size: 100,
  status: "restored", targetType: "local", remoteUri: "", encrypted: false, warnings: [], preservedPath: "", chainId: "", chainIndex: 0, backupMode: "full", baseBackupId: "", subscriptionUserId: "", itemCount: 1, localMissing: false, remoteMissing: false, remoteVerifiedUtc: null });
function response(config: InternalAxiosRequestConfig, data: unknown): AxiosResponse { return { config, data: { success: true, data }, headers: {}, status: 200, statusText: "OK" }; }
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
async function flush() { for (let i = 0; i < 40; i++) await nextTick(); }
const originalAdapter = apiClient.defaults.adapter;
let scope: EffectScope, calls: InternalAxiosRequestConfig[];
let transport: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
beforeEach(() => {
  vi.resetAllMocks(); invalidateSessionRequests(); setActivePinia(createPinia());
  useAuthStore().session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "admin-a", role: "Administrator", userName: "admin", email: "admin@example.test", preferredLanguage: "en", isActive: true, isTwoFactorEnabled: false, lastLoginAtUtc: null } };
  vi.stubGlobal("window", { location: { pathname: "/panel" }, setTimeout, clearTimeout });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture-token" } }), { status: 200 })));
  calls = []; transport = async config => response(config, config.method === "get" ? { chainId: "chain", links: [], broken: false, missingLinks: [] } : record());
  apiClient.defaults.adapter = async config => { calls.push(config); return transport(config); };
  dialogs.prompt.mockResolvedValue("fixture-password"); dialogs.confirm.mockImplementation(async options => { await options.action(); return true; }); scope = effectScope();
});
afterEach(() => { scope.stop(); apiClient.defaults.adapter = originalAdapter; invalidateSessionRequests(); vi.unstubAllGlobals(); });
it.each([false, true])("transmits the explicit DEFINER adaptation choice: %s", async adapt => {
  const flow = scope.run(() => useBackupRestore())!;
  dialogs.confirm.mockImplementation(async options => { options.checkbox.value = adapt; await options.action(); return true; });
  await flow.restore(record(), vi.fn(), vi.fn());
  const body = JSON.parse(calls.find(config => config.url === "/backup/restore")!.data);
  expect(body.adaptDefiners ?? false).toBe(adapt);
  expect(body.backupId).toBe("one");
});
for (const transition of ["actor", "session"] as const) for (const stage of ["chain", "password", "confirmation", "restore", "refresh"] as const) {
  it(`does not continue ${stage} into a changed ${transition} with the same administrator role`, async () => {
    const flow = scope.run(() => useBackupRestore())!, gate = deferred<void>(), result = vi.fn(), error = vi.fn();
    const refresh = vi.fn(async () => { if (stage === "refresh") await gate.promise; });
    const normalTransport = transport;
    transport = async config => { if ((stage === "chain" && config.method === "get") || (stage === "restore" && config.method === "post")) await gate.promise; return normalTransport(config); };
    if (stage === "password") dialogs.prompt.mockImplementation(async () => { await gate.promise; return "fixture-password"; });
    if (stage === "confirmation") dialogs.confirm.mockImplementation(async options => { await gate.promise; await options.action(); return true; });
    const restoring = flow.restore({ ...record(), chainId: stage === "chain" ? "chain" : "", encrypted: stage === "chain" || stage === "password" }, result, error, refresh);
    await flush();
    if (transition === "actor") useAuthStore().session!.user!.id = "admin-b";
    else useAuthStore().session = { ...useAuthStore().session!, user: { ...useAuthStore().session!.user! } };
    gate.resolve(); await restoring;
    expect(calls.filter(config => config.method === "post")).toHaveLength(stage === "restore" || stage === "refresh" ? 1 : 0);
    if (stage === "chain") expect(dialogs.prompt).not.toHaveBeenCalled();
    if (stage !== "refresh") expect(refresh).not.toHaveBeenCalled();
    expect(result).not.toHaveBeenCalled(); expect(error).not.toHaveBeenCalled();
  });
}
it("keeps one full restore pending through confirmation and refresh", async () => {
  const flow = scope.run(() => useBackupRestore())!, gate = deferred<void>(), result = vi.fn();
  dialogs.confirm.mockImplementation(async options => { await gate.promise; await options.action(); return true; });
  const first = flow.restore(record(), result, vi.fn()); await flow.restore(record(), result, vi.fn()); gate.resolve(); await first;
  expect(calls.filter(config => config.method === "post")).toHaveLength(1); expect(result).toHaveBeenCalledOnce(); expect(flow.restoringId.value).toBe("");
});
it.each(["restored", "restored_with_errors"])("keeps a measured %s outcome with its recovery metadata", async status => {
  const value = { ...record(), status, restoredPath: "/restored/alpha", preservedPath: "/preserved/alpha", warnings: status === "restored" ? [] : [{ messageKey: "backup.site_create_failed", target: "alpha.test", detail: "Partial restore" }] };
  transport = async config => response(config, value);
  await expect(restoreBackup("one", "", "secret")).resolves.toEqual(value);
});
it.each([
  "false-envelope", "missing-record", "wrong-id", "created-status", "unknown-status", "missing-warnings", "null-warning", "blank-warning-key", "invalid-warning-target", "invalid-warning-detail", "invalid-restored-path", "invalid-preserved-path"
])("rejects an unmeasured restore response: %s", async kind => {
  transport = async config => {
    const warning = { messageKey: "backup.site_create_failed", target: "alpha.test", detail: "Partial restore" };
    const value = { ...record(), ...(kind === "wrong-id" ? { id: "other" } : kind === "created-status" ? { status: "created" } : kind === "unknown-status" ? { status: "" } : kind === "missing-warnings" ? { warnings: undefined } : kind === "null-warning" ? { warnings: [null] } : kind === "blank-warning-key" ? { warnings: [{ ...warning, messageKey: " " }] } : kind === "invalid-warning-target" ? { warnings: [{ ...warning, target: 1 }] } : kind === "invalid-warning-detail" ? { warnings: [{ ...warning, detail: null }] } : kind === "invalid-restored-path" ? { restoredPath: {} } : kind === "invalid-preserved-path" ? { preservedPath: [] } : {}) };
    const result = response(config, kind === "missing-record" ? null : value); if (kind === "false-envelope") result.data.success = false; return result;
  };
  await expect(restoreBackup("one", "")).rejects.toThrow("panel.backup.resultUnconfirmed");
});
it("shows invalid-success as an error without refreshing or publishing restoration", async () => {
  const flow = scope.run(() => useBackupRestore())!, result = vi.fn(), error = vi.fn(), refresh = vi.fn();
  transport = async config => response(config, { ...record(), status: "created" });
  await flow.restore(record(), result, error, refresh);
  expect(error).toHaveBeenCalledOnce(); expect(result).not.toHaveBeenCalled(); expect(refresh).not.toHaveBeenCalled();
});
it.each(["name", "source", "createdAtUtc", "size"])("rejects malformed restored-record metadata before presenting %s", async field => {
  transport = async config => response(config, { ...record(), [field]: field === "size" ? -1 : field === "createdAtUtc" ? "not-a-date" : null });
  await expect(restoreBackup("one", "")).rejects.toThrow("panel.backup.resultUnconfirmed");
});
