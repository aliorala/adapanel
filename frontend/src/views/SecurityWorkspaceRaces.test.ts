import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import SecurityView from "./SecurityView.vue";
import { allowPort, getFirewallRules, removeFirewallRule } from "../api/firewall";
import { getBruteForce, getSiteSecurity, isolateSite, updateBruteForce, type SiteSecurityConfiguration } from "../api/security";
import { getAntivirusStatus, scanAntivirus, type AntivirusScanResult, type AntivirusStatus } from "../api/antivirus";
import { useSitesStore } from "../stores/sites";
import { invalidateSessionRequests } from "../api/client";

const confirm = vi.hoisted(() => vi.fn());
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm }) }));
vi.mock("../api/firewall", async original => ({ ...await original<object>(), getFirewallRules: vi.fn(), allowPort: vi.fn(), removeFirewallRule: vi.fn() }));
vi.mock("../api/security", async original => ({ ...await original<object>(), getSiteSecurity: vi.fn(), getBruteForce: vi.fn(), isolateSite: vi.fn(), updateBruteForce: vi.fn() }));
vi.mock("../api/antivirus", async original => ({ ...await original<object>(), getAntivirusStatus: vi.fn(), scanAntivirus: vi.fn() }));
let app: App | undefined;
beforeEach(() => {
  vi.useFakeTimers();
  vi.mocked(getFirewallRules).mockResolvedValue([]);
  vi.mocked(getBruteForce).mockResolvedValue({ settings: { enabled: true, maxFailedAttempts: 10, windowMinutes: 15, banMinutes: 60, whitelistedIps: [] }, bannedIps: [] });
  vi.mocked(getSiteSecurity).mockImplementation(async site => config(site));
  vi.mocked(getAntivirusStatus).mockResolvedValue(status());
  confirm.mockImplementation(async options => { await options.action?.(); return true; });
});
afterEach(() => { app?.unmount(); app = undefined; vi.restoreAllMocks(); vi.resetAllMocks(); vi.useRealTimers(); });

function config(siteName: string): SiteSecurityConfiguration {
  return { siteName, applicationPoolName: siteName, physicalPath: siteName, principal: siteName, isIsolated: true, folderAclApplied: true, basicRulesEnabled: true, requestsPerMinute: 120 };
}
function scan(scanStatus = "clean", startedAtUtc = "2026-10-02T10:00:00Z"): AntivirusScanResult {
  return { target: "a.test", startedAtUtc, status: scanStatus, durationSeconds: 15, scannedFiles: 20, infectedCount: 0, infected: [], detail: scanStatus === "failed" ? "scan engine failed" : "" };
}
function status(running = false, lastScan: AntivirusScanResult | null = null): AntivirusStatus {
  return { isInstalled: true, version: "1.0", databaseReady: true, databaseUpdatedAtUtc: null, runningScan: running ? { target: "a.test", startedAtUtc: "2026-10-02T10:00:00Z" } : null, lastScan };
}
function deferred<T>() { let resolve!: (value: T) => void, reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
type State = {
  securityConfig: SiteSecurityConfiguration | null; aclForm: { principal: string }; loading: boolean;
  avSiteName: string; avStatus: AntivirusStatus | null; busyAction: string; notice: string; noticeError: boolean;
  bruteForceLoaded: boolean; bruteForceReadError: boolean;
  refreshAll(): Promise<void>; refreshAvStatus(): Promise<unknown>; runAvScan(): Promise<void>; runSiteAction(action: "isolate"): Promise<void>;
  removeRule(name: string): Promise<void>; saveFirewall(allow: boolean): Promise<void>; saveBruteForce(): Promise<void>;
};
async function flush() { for (let i = 0; i < 12; i++) await nextTick(); }
async function mount(beforeMount?: () => void) {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia);
  useSitesStore().selectedSiteName = "a.test";
  vi.spyOn(useSitesStore(), "fetchSites").mockResolvedValue([]);
  beforeMount?.();
  app = renderer.createApp({ ...SecurityView, render: () => h("section") } as Component).use(pinia);
  app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState;
  await flush(); return state;
}
async function selectSite(site: string) { useSitesStore().selectedSiteName = site; await flush(); }
function unmount() { app!.unmount(); app = undefined; }

it("discards an old site's security response while the new site is still loading", async () => {
  const first = deferred<SiteSecurityConfiguration>(), second = deferred<SiteSecurityConfiguration>();
  vi.mocked(getSiteSecurity).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
  const state = await mount(); await selectSite("b.test");
  first.resolve(config("a.test")); await flush();
  expect(state.securityConfig).toBeNull(); expect(state.loading).toBe(true);
  second.resolve(config("b.test")); await flush();
  expect(state.securityConfig?.siteName).toBe("b.test"); expect(state.aclForm.principal).toBe("b.test");
});

it("clears security data and form values when no site is selected", async () => {
  const state = await mount(); await selectSite("");
  expect(state.securityConfig).toBeNull(); expect(state.aclForm.principal).toBe("");
});

it("does not replace a new site's security settings with an old mutation result", async () => {
  const state = await mount(), pending = deferred<SiteSecurityConfiguration>();
  vi.mocked(isolateSite).mockReturnValueOnce(pending.promise);
  const saving = state.runSiteAction("isolate"); await selectSite("b.test");
  pending.resolve(config("a.test")); await saving;
  expect(state.securityConfig?.siteName).toBe("b.test"); expect(state.notice).toBe("");
});

it("loads site security even if the independent firewall list fails", async () => {
  vi.mocked(getFirewallRules).mockRejectedValueOnce(new Error("firewall unavailable"));
  const state = await mount();
  expect(state.securityConfig?.siteName).toBe("a.test");
});

it("does not start security requests after a late site-list response on an unmounted screen", async () => {
  const pending = deferred<never[]>();
  await mount(() => vi.mocked(useSitesStore().fetchSites).mockReturnValueOnce(pending.promise));
  unmount(); pending.resolve([]); await flush();
  expect(getFirewallRules).not.toHaveBeenCalled(); expect(getSiteSecurity).not.toHaveBeenCalled();
});

it("does not revive antivirus polling when its in-flight status request finishes after unmount", async () => {
  const state = await mount(), pending = deferred<AntivirusStatus>();
  vi.mocked(getAntivirusStatus).mockResolvedValueOnce(status(true)).mockReturnValueOnce(pending.promise);
  await state.refreshAvStatus(); await flush();
  await vi.advanceTimersByTimeAsync(15_000);
  expect(getAntivirusStatus).toHaveBeenCalledTimes(2);
  unmount(); pending.resolve(status(true)); await flush();
  await vi.advanceTimersByTimeAsync(60_000);
  expect(getAntivirusStatus).toHaveBeenCalledTimes(2); expect(vi.getTimerCount()).toBe(0);
});

it("shares overlapping antivirus status reads", async () => {
  const state = await mount(), pending = deferred<AntivirusStatus>();
  vi.mocked(getAntivirusStatus).mockReturnValue(pending.promise);
  const first = state.refreshAvStatus(), second = state.refreshAvStatus();
  pending.resolve(status()); await Promise.all([first, second]);
  expect(getAntivirusStatus).toHaveBeenCalledTimes(1);
});

it("does not start duplicate scans while a scan request is pending", async () => {
  const state = await mount(), pending = deferred<AntivirusScanResult>();
  state.avSiteName = "a.test";
  vi.mocked(scanAntivirus).mockReturnValue(pending.promise);
  const first = state.runAvScan(), second = state.runAvScan();
  pending.resolve(scan()); await Promise.all([first, second]);
  expect(scanAntivirus).toHaveBeenCalledTimes(1);
});

it.each(["failed", "interrupted"])("never reports a %s scan as clean", async scanStatus => {
  const state = await mount(); state.avSiteName = "a.test";
  vi.mocked(scanAntivirus).mockResolvedValue(scan(scanStatus));
  await state.runAvScan();
  expect(state.noticeError).toBe(true); expect(state.notice).not.toBe("panel.antivirus.scanClean");
});

it("does not claim an older clean scan is the result of a newly ended server scan", async () => {
  const state = await mount();
  vi.mocked(getAntivirusStatus).mockResolvedValueOnce(status(true)).mockResolvedValueOnce(status(false, scan("clean", "2026-10-01T10:00:00Z")));
  await state.refreshAvStatus(); await flush(); await vi.advanceTimersByTimeAsync(15_000);
  expect(state.noticeError).toBe(true); expect(state.notice).not.toBe("panel.antivirus.scanClean");
});

it("does not follow a completed scan with another status request after unmount", async () => {
  const state = await mount(), pending = deferred<AntivirusScanResult>(); state.avSiteName = "a.test";
  vi.mocked(scanAntivirus).mockReturnValueOnce(pending.promise);
  const scanning = state.runAvScan(); unmount(); pending.resolve(scan()); await scanning;
  expect(getAntivirusStatus).not.toHaveBeenCalled(); expect(state.notice).toBe("");
});

it("does not remove a firewall rule from a confirmation left open after unmount", async () => {
  const state = await mount();
  confirm.mockImplementationOnce(async options => { unmount(); await options.action?.(); return true; });
  await state.removeRule("AdaPanel Example");
  expect(removeFirewallRule).not.toHaveBeenCalled();
});

it("does not let another security operation release the busy lock during a scan", async () => {
  const state = await mount(), pending = deferred<AntivirusScanResult>(); state.avSiteName = "a.test";
  vi.mocked(scanAntivirus).mockReturnValueOnce(pending.promise);
  const scanning = state.runAvScan(); await state.saveFirewall(true); await state.saveBruteForce();
  expect(allowPort).not.toHaveBeenCalled(); expect(updateBruteForce).not.toHaveBeenCalled(); expect(state.busyAction).toBe("av");
  pending.resolve(scan()); await scanning;
});

it("does not read firewall rules after its write finishes on an unmounted screen", async () => {
  const state = await mount(), pending = deferred<Awaited<ReturnType<typeof allowPort>>>();
  vi.mocked(allowPort).mockReturnValueOnce(pending.promise); vi.mocked(getFirewallRules).mockClear();
  const saving = state.saveFirewall(true); unmount(); pending.resolve({ displayName: "AdaPanel Example", protocol: "TCP", localPort: "8080", action: "Allow", direction: "Inbound", enabled: true, verified: true }); await saving;
  expect(getFirewallRules).not.toHaveBeenCalled();
});

it("discards status reads started before the local scan began", async () => {
  const state = await mount(), oldRead = deferred<AntivirusStatus>(); state.avSiteName = "a.test";
  vi.mocked(getAntivirusStatus).mockReturnValueOnce(oldRead.promise).mockResolvedValueOnce(status(false, scan()));
  const reading = state.refreshAvStatus();
  vi.mocked(scanAntivirus).mockResolvedValueOnce(scan()); await state.runAvScan();
  oldRead.resolve(status(true)); await reading; await flush();
  expect(state.avStatus?.runningScan).toBeNull(); expect(state.avStatus?.lastScan?.status).toBe("clean");
});

it.each(["clean", "infected", "failed", "interrupted"])("reports the actual %s result when a server scan ends", async scanStatus => {
  const state = await mount();
  vi.mocked(getAntivirusStatus).mockResolvedValueOnce(status(true)).mockResolvedValueOnce(status(false, scan(scanStatus)));
  await state.refreshAvStatus(); await flush(); await vi.advanceTimersByTimeAsync(15_000);
  expect(state.noticeError).toBe(scanStatus !== "clean");
  expect(state.notice).toContain(scanStatus === "clean" ? "panel.antivirus.scanClean" : scanStatus === "infected" ? "panel.antivirus.scanInfected" : scanStatus === "interrupted" ? "panel.antivirus.interruptedHint" : "panel.antivirus.scanUnconfirmed");
  expect(vi.getTimerCount()).toBe(0);
});

it("keeps polling a running server scan after a transient status error", async () => {
  const state = await mount();
  vi.mocked(getAntivirusStatus).mockResolvedValueOnce(status(true)).mockRejectedValueOnce(new Error("offline")).mockResolvedValueOnce(status(false, scan()));
  await state.refreshAvStatus(); await flush(); await vi.advanceTimersByTimeAsync(30_000);
  expect(getAntivirusStatus).toHaveBeenCalledTimes(3); expect(state.notice).toBe("panel.antivirus.scanClean");
});

it("does not apply or poll an antivirus response from an invalidated session", async () => {
  const state = await mount(), pending = deferred<AntivirusStatus>();
  vi.mocked(getAntivirusStatus).mockReturnValueOnce(pending.promise);
  const reading = state.refreshAvStatus(); invalidateSessionRequests(); pending.resolve(status(true)); await reading; await flush();
  expect(state.avStatus).toBeNull(); expect(vi.getTimerCount()).toBe(0);
});

it("does not save default brute-force settings after their initial read fails", async () => {
  vi.mocked(getBruteForce).mockRejectedValueOnce(new Error("settings unavailable"));
  const state = await mount(); await state.saveBruteForce();
  expect(updateBruteForce).not.toHaveBeenCalled();
  expect(state.bruteForceLoaded).toBe(false); expect(state.bruteForceReadError).toBe(true);
});

it("does not treat old brute-force settings as verified after a refresh fails", async () => {
  const state = await mount();
  vi.mocked(getBruteForce).mockRejectedValueOnce(new Error("refresh unavailable"));
  await state.refreshAll(); await state.saveBruteForce();
  expect(updateBruteForce).not.toHaveBeenCalled(); expect(state.bruteForceLoaded).toBe(false); expect(state.bruteForceReadError).toBe(true);
});

it("allows saving the actual server settings after a failed read is successfully retried", async () => {
  vi.mocked(getBruteForce).mockRejectedValueOnce(new Error("settings unavailable"));
  const state = await mount();
  const restored = { settings: { enabled: false, maxFailedAttempts: 20, windowMinutes: 5, banMinutes: 30, whitelistedIps: ["198.51.100.5"] }, bannedIps: [] };
  vi.mocked(getBruteForce).mockResolvedValueOnce(restored); vi.mocked(updateBruteForce).mockResolvedValueOnce(restored);
  await state.refreshAll(); await state.saveBruteForce();
  expect(updateBruteForce).toHaveBeenCalledExactlyOnceWith(restored.settings);
  expect(state.bruteForceLoaded).toBe(true); expect(state.bruteForceReadError).toBe(false);
});
