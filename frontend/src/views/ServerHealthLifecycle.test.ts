import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import type { AxiosResponse, InternalAxiosRequestConfig } from "axios";
import ServerHealthView from "./ServerHealthView.vue";
import { apiClient, invalidateSessionRequests } from "../api/client";
import { useAuthStore } from "../stores/auth";
import { useSubscriptionStore } from "../stores/subscription";
import type { DomainRefreshSummary, ServiceHealth } from "../api/health";
import type { DnsServerHealth } from "../api/dns";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
type State = { services: ServiceHealth[]; dns: DnsServerHealth | null; domains: DomainRefreshSummary | null; loading: boolean;
  loaded: boolean; servicesConfirmed: boolean; dnsConfirmed: boolean; domainsConfirmed: boolean; domainsLoading: boolean;
  servicesLoad: { message: { value: string } }; notice: string; noticeError: boolean; actionBusy: boolean; onarilacakVar: boolean; domainReport: DomainRefreshSummary["results"];
  canRestart(name: string): boolean; load(): Promise<void>; restart(name: string): Promise<void>; repairDns(): Promise<void>; refreshAllDomains(): Promise<void> };
const services = (): ServiceHealth[] => [{ name: "W3SVC", label: "IIS", status: "Running" }, { name: "DNS", label: "DNS", status: "Running" }];
const dns = (): DnsServerHealth => ({ installed: true, serviceStatus: "Running", startMode: "Auto", delayedAutoStart: false,
  listeningAddresses: ["127.0.0.1"], expectedAddresses: ["192.0.2.1"], missingAddresses: ["192.0.2.1"], publiclyBound: false,
  zoneCount: 1, needsRepair: true, lastAutoRepairUtc: null, lastAutoRepairStatus: "", lastAutoRepairSucceeded: null, details: "" });
const domains = (): DomainRefreshSummary => ({ totalSites: 1, updatedSites: 1, failedSites: 0, serverFindings: [],
  results: [{ siteName: "fixture.test", zoneName: "fixture.test", alreadyCurrent: false, error: "", addedDnsRecords: [], appliedMailRecords: 0, findings: [] }] });
function response(config: InternalAxiosRequestConfig, data: unknown): AxiosResponse { return { config, data: { success: true, data }, headers: {}, status: 200, statusText: "OK" }; }
function deferred<T>() { let resolve!: (value: T) => void, reject!: (reason: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
const originalAdapter = apiClient.defaults.adapter;
let app: App | undefined, calls: InternalAxiosRequestConfig[], read: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>, write: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
async function flush() { for (let i = 0; i < 50; i++) await nextTick(); }
function unmount() { app?.unmount(); app = undefined; }
async function mount() {
  const pinia = createPinia(), auth = useAuthStore(pinia), subscription = useSubscriptionStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "admin", userName: "admin", email: "a@example.test", role: "Administrator", preferredLanguage: "en", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } };
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...ServerHealthView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return { state, auth, subscription };
}
const mutations = () => calls.filter(config => config.method !== "get");
beforeEach(() => {
  invalidateSessionRequests(); calls = [];
  vi.stubGlobal("window", { location: { pathname: "/panel", hostname: "localhost" } });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "mock-token" } }), { status: 200 })));
  read = async config => response(config, config.url === "/server-health" ? services() : config.url === "/dns/server/health" ? dns() : domains());
  write = async config => response(config, config.url === "/server-health/restart" ? services().find(row => row.name === JSON.parse(String(config.data)).name) : config.url === "/dns/server/repair" ? { ...dns(), publiclyBound: true, needsRepair: false, missingAddresses: [] } : domains());
  apiClient.defaults.adapter = async config => { calls.push(config); return config.method === "get" ? read(config) : write(config); };
});
afterEach(() => { unmount(); apiClient.defaults.adapter = originalAdapter; invalidateSessionRequests(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it("keeps the reload reserved until all independent health reads settle", async () => {
  const pending = deferred<AxiosResponse>(), normal = read; let request!: InternalAxiosRequestConfig;
  read = config => config.url === "/dns/server/health" ? (request = config, pending.promise) : normal(config);
  const { state } = await mount(); expect(state.loading).toBe(true); await state.load(); await state.restart("W3SVC");
  expect(calls.filter(config => config.url === "/dns/server/health")).toHaveLength(1); expect(mutations()).toHaveLength(0);
  pending.resolve(response(request, dns())); await flush(); expect(state.loading).toBe(false);
});
it("does not let a pending reload race with a DNS repair", async () => {
  const { state } = await mount(), pending = deferred<AxiosResponse>(), normal = read; let request!: InternalAxiosRequestConfig;
  read = config => config.url === "/dns/server/health" ? (request = config, pending.promise) : normal(config);
  const loading = state.load(); await flush(); await state.repairDns(); expect(mutations()).toHaveLength(0);
  pending.resolve(response(request, dns())); await loading;
  await state.repairDns(); expect(state.dns?.publiclyBound).toBe(true); expect(state.noticeError).toBe(false);
});
for (const kind of ["restart", "dns", "domains"] as const) {
  const act = (state: State) => kind === "restart" ? state.restart("W3SVC") : kind === "dns" ? state.repairDns() : state.refreshAllDomains();
  it(`${kind}: reserves all health actions and refresh while a mutation is pending`, async () => {
    const { state } = await mount(), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
    write = config => (request = config, pending.promise); const writing = act(state); await flush(); const before = calls.length;
    const others = [state.restart("DNS"), state.repairDns(), state.refreshAllDomains(), state.load()]; await flush(); expect(calls).toHaveLength(before);
    pending.resolve(response(request, kind === "restart" ? services()[0] : kind === "dns" ? { ...dns(), publiclyBound: true, needsRepair: false, missingAddresses: [] } : domains()));
    await Promise.all([writing, ...others]); expect(state.actionBusy).toBe(false);
  });
  it.each(["unmount", "session", "actor"])(`${kind}: suppresses late result and follow-up requests after %s`, async change => {
    const { state, auth } = await mount(), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
    write = config => (request = config, pending.promise); const writing = act(state); await flush(); const count = calls.length;
    if (change === "unmount") unmount(); else if (change === "session") invalidateSessionRequests(); else auth.session!.user!.id = "replacement";
    pending.resolve(response(request, kind === "restart" ? services()[0] : kind === "dns" ? { ...dns(), publiclyBound: true, needsRepair: false, missingAddresses: [] } : domains())); await writing;
    expect(state.notice).toBe(""); expect(calls).toHaveLength(count);
  });
}
it.each(["unmount", "session", "actor"])("does not publish or start subsequent read stages after %s", async change => {
  const pending = deferred<AxiosResponse>(), normal = read; let request!: InternalAxiosRequestConfig;
  read = config => config.url === "/server-health" ? (request = config, pending.promise) : normal(config);
  const { state, auth } = await mount(); const count = calls.length;
  if (change === "unmount") unmount(); else if (change === "session") invalidateSessionRequests(); else auth.session!.user!.id = "replacement";
  pending.resolve(response(request, services())); await flush(); expect(state.services).toEqual([]); expect(state.notice).toBe(""); expect(calls).toHaveLength(count);
});
it.each(["Stopped", "Unknown", "NotInstalled"])("does not announce a successful restart when readback is %s", async status => {
  const { state } = await mount(); write = async config => response(config, { ...services()[0], status }); await state.restart("W3SVC");
  expect(state.noticeError).toBe(true); expect(state.notice).not.toBe("panel.health.restartedNotice");
});
it("rejects a different service response instead of updating the wrong row", async () => {
  const { state } = await mount(); write = async config => response(config, { ...services()[1], status: "Stopped" }); await state.restart("W3SVC");
  expect(state.services[1]!.status).toBe("Running"); expect(state.noticeError).toBe(true);
});
it("requires a new successful service measurement after a failed reload", async () => {
  const { state } = await mount(), normal = read; read = config => config.url === "/server-health" ? Promise.reject(new Error("health offline")) : normal(config);
  await state.load(); await state.restart("W3SVC"); expect(mutations()).toHaveLength(0); expect(state.services).toHaveLength(2); expect(state.servicesConfirmed).toBe(false);
  read = normal; await state.load(); await state.restart("W3SVC"); expect(mutations()).toHaveLength(1); expect(state.noticeError).toBe(false);
});
it.each(["false-envelope", "null", "invalid-status", "duplicate"])("does not accept an unreadable service measurement: %s", async kind => {
  const normal = read; read = async config => {
    if (config.url !== "/server-health") return normal(config);
    const result = response(config, kind === "null" ? null : kind === "invalid-status" ? [{ ...services()[0], status: undefined }] : kind === "duplicate" ? [services()[0], services()[0]] : services());
    if (kind === "false-envelope") result.data.success = false; return result;
  };
  const { state } = await mount(); expect(state.servicesConfirmed).toBe(false); expect(state.servicesLoad.message.value).not.toBe("");
});
it.each(["error-row", "unresolved-server", "confirmation-row"])("reports incomplete domain repair honestly: %s", async kind => {
  const { state } = await mount(); const result = domains(); result.updatedSites = 0;
  const finding = { key: "health.fixture", severity: "warn" as const, repaired: false, needsConfirmation: kind === "confirmation-row", detail: "" };
  if (kind === "error-row") result.results[0]!.error = "DNS unavailable";
  else if (kind === "unresolved-server") result.serverFindings = [finding]; else result.results[0]!.findings = [finding];
  write = async config => response(config, result); await state.refreshAllDomains(); expect(state.noticeError).toBe(true);
});
it("keeps a repaired domain finding as a positive measured result", async () => {
  const { state } = await mount(), result = domains(); result.results[0]!.findings = [{ key: "health.fixture", severity: "warn", repaired: true, needsConfirmation: false, detail: "" }];
  write = async config => response(config, result); await state.refreshAllDomains(); expect(state.noticeError).toBe(false);
});
it("does not call an incomplete DNS repair healthy merely because the public binding exists", async () => {
  const { state } = await mount(); write = async config => response(config, { ...dns(), publiclyBound: true, needsRepair: true }); await state.repairDns(); expect(state.noticeError).toBe(true);
});
it.each(["StartPending", "StopPending", "ContinuePending", "PausePending", "Paused"])("preserves the other measurements when a Windows service has the transitional status %s", async status => {
  const normal = read;
  read = async config => config.url === "/server-health" ? response(config, [{ ...services()[0], status }, services()[1]]) : normal(config);
  const { state } = await mount(); expect(state.servicesConfirmed).toBe(true); expect(state.services[0]!.status).toBe(status);
  expect(state.canRestart("W3SVC")).toBe(false); expect(state.canRestart("DNS")).toBe(true);
});
it.each(["dns", "domains"])("requires a measured %s payload and permits retry recovery", async family => {
  const normal = read; read = async config => config.url === (family === "dns" ? "/dns/server/health" : "/server-health/refresh-domains") ? response(config, null) : normal(config);
  const { state } = await mount(); expect(family === "dns" ? state.dnsConfirmed : state.domainsConfirmed).toBe(false);
  read = normal; await state.load(); expect(family === "dns" ? state.dnsConfirmed : state.domainsConfirmed).toBe(true);
});
it("does not count repaired sites and repaired findings as remaining repair work", async () => {
  const { state } = await mount(); expect(state.onarilacakVar).toBe(true);
  const result = domains(); result.serverFindings = [{ key: "health.fixture", severity: "warn", repaired: true, needsConfirmation: false, detail: "" }];
  write = async config => response(config, result); await state.refreshAllDomains();
  expect(state.domains?.updatedSites).toBe(1); expect(state.onarilacakVar).toBe(false); expect(state.noticeError).toBe(false);
});
it("keeps confirmation-only findings visible even when the domain defaults are already current", async () => {
  const result = domains(); result.updatedSites = 0; result.results[0]!.alreadyCurrent = true;
  result.results[0]!.findings = [{ key: "repair.site_stopped", severity: "warn", repaired: false, needsConfirmation: true, detail: "" }];
  const normal = read; read = config => config.url === "/server-health/refresh-domains" ? Promise.resolve(response(config, result)) : normal(config);
  const { state } = await mount(); expect(state.onarilacakVar).toBe(true); expect(state.domainReport).toHaveLength(1);
  expect(state.domainReport[0]!.findings[0]!.needsConfirmation).toBe(true);
});
