import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, createSSRApp, h, nextTick, reactive, ssrContextKey, type App, type Component } from "vue";
import { renderToString } from "vue/server-renderer";
import type { AxiosResponse, InternalAxiosRequestConfig } from "axios";
import { apiClient, invalidateSessionRequests } from "../api/client";
import { getServerIpAddresses } from "../api/system";
import { useAuthStore } from "../stores/auth";
import DashboardOverviewView from "./DashboardOverviewView.vue";
import MonitoringView from "./MonitoringView.vue";
import { copyToClipboard } from "../utils/clipboard";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../utils/clipboard", () => ({ copyToClipboard: vi.fn(async () => true) }));
type State = { isRestricted: boolean; showServerResources: boolean; canReadServerIps: boolean; ipLoading: boolean; ipError: string; ipLoaded: boolean;
  serverIps: string[]; accessLinks: { label: string; url: string }[]; copiedUrl: string; copyFailed: string;
  loadServerIps(): Promise<void>; copyAccess(url: string): Promise<void> };
const overview = (isRestricted = false) => ({ isRestricted, machineName: "fixture", operatingSystem: "Windows", osArchitecture: "x64", frameworkDescription: ".NET", panelVersion: "0.8.77", currentServerTimeUtc: "2026-10-04T10:00:00Z", bootTimeUtc: "2026-10-04T00:00:00Z", uptime: "10:00:00", cpuUsagePercent: 10, logicalProcessorCount: 4, totalMemoryBytes: 1000, availableMemoryBytes: 800, usedMemoryBytes: 200, memoryUsagePercent: 20, disks: [], networkInterfaces: [], cpuMeasured: true, memoryMeasured: true, disksMeasured: true, networkMeasured: true });
const ips = () => [{ address: "192.0.2.10", family: "InterNetwork", interfaceName: "Ethernet" }];
function response(config: InternalAxiosRequestConfig, data: unknown): AxiosResponse { return { config, data: { success: true, data }, headers: {}, status: 200, statusText: "OK" }; }
function deferred<T>() { let resolve!: (value: T) => void, reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
const originalAdapter = apiClient.defaults.adapter;
let app: App | undefined, calls: InternalAxiosRequestConfig[], read: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
async function flush() { for (let i = 0; i < 40; i++) await nextTick(); }
function unmount() { app?.unmount(); app = undefined; }
async function mount(role = "Administrator", restricted = false, confirmed = true, view: "dashboard" | "monitor" = "dashboard") {
  const pinia = createPinia(); setActivePinia(pinia); const auth = useAuthStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "actor-one", userName: "fixture", email: "a@example.test", role, preferredLanguage: "en", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } };
  const props = reactive({ overview: overview(restricted) as ReturnType<typeof overview> | null, overviewConfirmed: confirmed, overviewError: "", loading: false,
    sitesCount: 2, totalBindingCount: 2, ftpUserCount: 0, databaseCount: 0, securityStatusLabel: "", securePath: "private-entry", auditItems: [], allowedSections: ["home", "sites"], panelUpdate: null });
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  let instance: any;
  const viewComponent = { ...(view === "dashboard" ? DashboardOverviewView : MonitoringView), render: () => h("section") } as Component;
  app = renderer.createApp({ setup: () => () => h(viewComponent, { ...props, ref: (value: unknown) => { if (value) instance = value; } }) }).use(pinia); app.provide(ssrContextKey, {}); app.mount({}); await flush();
  return { state: instance.$.setupState as State, props, auth };
}
const ipCalls = () => calls.filter(config => config.url === "/system/ip-addresses");
beforeEach(() => {
  vi.useFakeTimers(); calls = []; invalidateSessionRequests();
  vi.stubGlobal("window", { setTimeout, clearTimeout, setInterval, clearInterval, location: { pathname: "/private-entry", origin: "https://panel.example.test:8443", protocol: "https:", host: "panel.example.test:8443", hostname: "panel.example.test", port: "8443" } });
  vi.stubGlobal("document", { hidden: false });
  read = async config => response(config, config.url === "/system/ip-addresses" ? ips() : config.url === "/system/history" ? [] : {
    capturedAtUtc: "2026-10-04T10:00:00Z", sampleWindowSeconds: 2, cpuBaselineReady: true, totalCpuPercent: 10, logicalProcessorCount: 4,
    totalMemoryBytes: 1000, usedMemoryBytes: 200, availableMemoryBytes: 800, memoryUsagePercent: 20, totalProcessCount: 0, sortBy: "cpu", processes: [] });
  apiClient.defaults.adapter = async config => { calls.push(config); return read(config); };
});
afterEach(() => { unmount(); apiClient.defaults.adapter = originalAdapter; vi.restoreAllMocks(); vi.unstubAllGlobals(); vi.useRealTimers(); });

it.each(["Customer", "Reseller"])("%s restricted Home does not request admin IP inventory", async role => {
  const { state } = await mount(role, true); expect(ipCalls()).toHaveLength(0); expect(state.isRestricted).toBe(true); expect(state.showServerResources).toBe(false);
});
it.each(["Customer", "Reseller"])("%s cannot reuse a full admin overview as server authority", async role => {
  const { state } = await mount(role, false); expect(ipCalls()).toHaveLength(0); expect(state.showServerResources).toBe(false);
});
it("does not read IP inventory until the full overview is confirmed", async () => {
  const { state, props } = await mount("Administrator", false, false); expect(ipCalls()).toHaveLength(0); expect(state.showServerResources).toBe(false);
  props.overviewConfirmed = true; await flush(); expect(ipCalls()).toHaveLength(1); expect(state.serverIps).toEqual(["192.0.2.10"]);
});
it("does not read IP inventory for a restricted response even when the local role says admin", async () => {
  const { state } = await mount("Administrator", true); expect(ipCalls()).toHaveLength(0); expect(state.showServerResources).toBe(false);
});
it.each(["unmount", "session", "actor", "role", "restricted", "unconfirmed"])("discards a late IP list after %s changes", async change => {
  const pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig; read = config => (request = config, pending.promise);
  const { state, auth, props } = await mount();
  if (change === "unmount") unmount(); else if (change === "session") invalidateSessionRequests(); else if (change === "actor") auth.session!.user!.id = "actor-two";
  else if (change === "role") auth.session!.user!.role = "Customer"; else if (change === "restricted") props.overview!.isRestricted = true; else props.overviewConfirmed = false;
  await flush(); pending.resolve(response(request, ips())); await flush(); expect(state.serverIps).toEqual([]); expect(state.ipError).toBe("");
});
it("keeps the IP request reserved against a second reload", async () => {
  const pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig; read = config => (request = config, pending.promise);
  const { state } = await mount(); await state.loadServerIps(); expect(ipCalls()).toHaveLength(1); pending.resolve(response(request, ips())); await flush(); expect(state.ipLoading).toBe(false);
});
it("exposes an IP read error and recovers only after a successful retry", async () => {
  read = () => Promise.reject(new Error("fixture unavailable")); const { state } = await mount(); expect(state.ipError).not.toBe(""); expect(state.ipLoaded).toBe(false);
  read = async config => response(config, ips()); await state.loadServerIps(); expect(state.ipError).toBe(""); expect(state.ipLoaded).toBe(true); expect(state.serverIps).toEqual(["192.0.2.10"]);
});
it("distinguishes a measured empty IP inventory from failure", async () => {
  read = async config => response(config, []); const { state } = await mount(); expect(state.ipLoaded).toBe(true); expect(state.ipError).toBe("");
});
it("uses the active remote origin instead of inventing a localhost panel link", async () => {
  const { state } = await mount(); expect(state.accessLinks[0]!.url).toBe("https://panel.example.test:8443/private-entry");
  expect(state.accessLinks.some(link => link.url.includes("localhost"))).toBe(false);
});
it("keeps a real loopback origin when the panel is opened locally", async () => {
  Object.assign(window.location, { origin: "http://localhost:5000", protocol: "http:", host: "localhost:5000", hostname: "localhost", port: "5000" });
  const { state } = await mount(); expect(state.accessLinks[0]!.url).toBe("http://localhost:5000/private-entry");
});
it("renders global IPv6 addresses as bracketed panel URLs", async () => {
  read = async config => response(config, [{ address: "2001:db8::5", family: "InterNetworkV6", interfaceName: "Ethernet" }]);
  const { state } = await mount(); expect(state.accessLinks.map(link => link.url)).toContain("https://[2001:db8::5]:8443/private-entry");
});
it("does not publish a clipboard outcome or schedule a timer after leaving the screen", async () => {
  const pending = deferred<boolean>(); vi.mocked(copyToClipboard).mockReturnValueOnce(pending.promise);
  const { state } = await mount(), copying = state.copyAccess(state.accessLinks[0]!.url); unmount(); pending.resolve(true); await copying;
  expect(state.copiedUrl).toBe(""); expect(vi.getTimerCount()).toBe(0);
});
for (const role of ["Customer", "Reseller"]) it(`${role} scoped monitoring does not start server history/process polling`, async () => {
  const { state } = await mount(role, true, true, "monitor"); await vi.advanceTimersByTimeAsync(60_000); expect(calls).toHaveLength(0); expect(state.showServerResources).toBe(false);
});
it.each(["false-envelope", "null", "bad-row", "bad-address"])("rejects an unmeasured IP response: %s", async kind => {
  read = async config => { const result = response(config, kind === "null" ? null : kind === "bad-row" ? [{ address: "192.0.2.1" }] : kind === "bad-address" ? [{ ...ips()[0], address: "evil.test/path" }] : ips());
    if (kind === "false-envelope") result.data.success = false; return result; };
  await expect(getServerIpAddresses()).rejects.toThrow();
});
it.each(["2001:db8::1]/unexpected?[", "2001:db8::1]#fragment["])("rejects URL syntax embedded in an IPv6 address: %s", async address => {
  read = async config => response(config, [{ address, family: "InterNetworkV6", interfaceName: "Ethernet" }]);
  await expect(getServerIpAddresses()).rejects.toThrow();
});
it("preserves an IPv4-mapped IPv6 address in a measured inventory", async () => {
  const address = "::ffff:192.0.2.10";
  read = async config => response(config, [{ address, family: "InterNetworkV6", interfaceName: "Ethernet" }]);
  await expect(getServerIpAddresses()).resolves.toEqual([{ address, family: "InterNetworkV6", interfaceName: "Ethernet" }]);
});
for (const role of ["Customer", "Reseller"]) {
  it.each([true, false])(`${role} Home renders scoped content without revealing full or restricted server placeholders (%s)`, async restricted => {
    const { auth, props } = await mount(role, restricted); unmount();
    const pinia = createPinia(); useAuthStore(pinia).session = auth.session;
    const html = await renderToString(createSSRApp(DashboardOverviewView, { ...props }).use(pinia));
    expect(html).toContain("panel.dashboard.scoped.title"); expect(html).toContain("panel.navigation.sites");
    expect(html).not.toContain("panel.monitor.partialMeasurement"); expect(html).not.toContain('role="meter"');
    expect(html).not.toContain("panel.system.serverInfo"); expect(html).not.toContain("panel.dashboard.access.title");
    expect(html).not.toContain("fixture");
    if (restricted) expect(html).toContain("AdaPanel 0.8.77");
  });
  it(`${role} Monitoring renders the permission explanation without mounting server consumers`, async () => {
    const { auth, props } = await mount(role, true, true, "monitor"); unmount();
    const pinia = createPinia(); useAuthStore(pinia).session = auth.session;
    const html = await renderToString(createSSRApp(MonitoringView, { overview: props.overview, overviewConfirmed: true, loading: false }).use(pinia));
    expect(html).toContain("panel.monitor.restricted"); expect(html).not.toContain('role="meter"');
    expect(html).not.toContain("panel.monitor.partialMeasurement"); expect(html).not.toContain("panel.system.serverInfo");
  });
}
