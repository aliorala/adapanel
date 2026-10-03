import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { AxiosError, type AxiosResponse, type InternalAxiosRequestConfig } from "axios";
import { createRenderer, createSSRApp, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import { renderToString } from "@vue/server-renderer";
import SslView from "./SslView.vue";
import * as ssl from "../api/ssl";
import { apiClient, invalidateSessionRequests } from "../api/client";
import { useSitesStore } from "../stores/sites";
import { useAuthStore } from "../stores/auth";
import { useSubscriptionStore } from "../stores/subscription";
import type { SubscriptionUsage } from "../api/subscriptions";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
type Workspace = ssl.SslWorkspace & { httpsRedirectMeasured?: boolean };
type State = {
  workspace: Workspace | null; notice: string; noticeError: boolean; activeTab: string; busyAction: string;
  importForm: { hostName: string; password: string; base64Content: string; fileName: string };
  issueForm: { hostName: string; contactEmail: string; wildcard: boolean };
  toggleHttpsRedirect(enabled: boolean): Promise<void>; renew(): Promise<void>; issue(): Promise<void>; submitImport(): Promise<void>;
  refresh(force?: boolean): Promise<boolean>; handleFileChange(event: Event): Promise<void>;
  handleRedirectChange(event: Event): Promise<void>;
};
const workspace = (enabled = false, measured: boolean | undefined = true): Workspace => ({ siteName: "a.test", hostNames: ["a.test"], certificates: [], httpsRedirectEnabled: enabled, httpsRedirectMeasured: measured });
function response(config: InternalAxiosRequestConfig, data: unknown): AxiosResponse { return { config, data: { success: true, data }, headers: {}, status: 200, statusText: "OK" }; }
function deferred<T>() { let resolve!: (value: T) => void, reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
let app: App | undefined, calls: InternalAxiosRequestConfig[], role: string, initial: Workspace;
let installed: ssl.SslCertificate | null;
let redirectReply: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
const originalAdapter = apiClient.defaults.adapter;
async function flush() { for (let i = 0; i < 36; i++) await nextTick(); }
function unmount() { app?.unmount(); app = undefined; }
async function mount() {
  const pinia = createPinia(), auth = useAuthStore(pinia), sites = useSitesStore(pinia), subscription = useSubscriptionStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "actor", userName: "actor", email: "actor@example.test", role, preferredLanguage: "en", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } };
  sites.selectedSiteName = "a.test"; sites.sites = [{ id: 1, name: "a.test", state: "Started", physicalPath: "", applicationRootPath: "", applicationPoolName: "a.test", bindings: [], autoStart: true }];
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...SslView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush();
  return { state, sites, subscription, pinia };
}
async function renderState(state: State) {
  // Render the actual compiled template with the real mounted setup state.
  return renderToString(createSSRApp({ ...SslView, setup: () => state } as Component));
}
beforeEach(() => {
  invalidateSessionRequests(); calls = []; role = "Administrator"; initial = workspace(); installed = null;
  vi.stubGlobal("window", { location: { pathname: "/panel", hostname: "localhost" } });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture-token" } }), { status: 200 })));
  redirectReply = async config => response(config, workspace((JSON.parse(String(config.data)) as { enabled: boolean }).enabled));
  apiClient.defaults.adapter = async config => {
    calls.push(config);
    if (config.method === "get") return response(config, { ...initial, siteName: decodeURIComponent(config.url!.split("/").pop()!), certificates: installed ? [installed] : initial.certificates });
    if (config.url === "/ssl/https-redirect") return redirectReply(config);
    if (config.url === "/ssl/renew") return response(config, { measured: true, renewedCount: 0, verifiedCount: 1, missingFromStoreCount: 0, unverifiedCount: 0, bindingDriftCount: 0, bindingsMeasured: true, failureReason: "", output: "" });
    if (config.url === "/ssl/letsencrypt" || config.url === "/ssl/import") {
      installed = { siteName: "a.test", hostName: "a.test", port: 443, thumbprint: "FIXTURE", friendlyName: "fixture", subject: "CN=a.test", notAfterUtc: "2027-01-01T00:00:00Z", source: config.url === "/ssl/import" ? "manual" : "letsencrypt", isCertificateBound: true, isHttpsBindingPresent: true, isPresentInStore: true, bindingDetail: "", fallbackReason: "" };
      return response(config, installed);
    }
    throw new Error(`Unexpected request ${config.url}`);
  };
});
afterEach(() => { unmount(); apiClient.defaults.adapter = originalAdapter; invalidateSessionRequests(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it.each([true, false])("does not claim redirect %s when the measured result is the opposite", async enabled => {
  redirectReply = async config => response(config, workspace(!enabled)); const { state } = await mount(); await state.toggleHttpsRedirect(enabled);
  expect(calls.filter(call => call.method === "post")).toHaveLength(1); expect(state.workspace?.httpsRedirectEnabled).toBe(!enabled);
  expect(state.noticeError).toBe(true); expect(state.notice).not.toBe(enabled ? "panel.ssl.forceHttpsOn" : "panel.ssl.forceHttpsOff");
});
it.each([true, false])("only confirms the measured requested redirect state %s", async enabled => {
  const { state } = await mount(); await state.toggleHttpsRedirect(enabled);
  expect(state.noticeError).toBe(false); expect(state.workspace?.httpsRedirectEnabled).toBe(enabled); expect(state.notice).toBe(enabled ? "panel.ssl.forceHttpsOn" : "panel.ssl.forceHttpsOff");
});
it.each([{ measured: false }, { measured: undefined }])("does not interpret an unmeasured false response as a confirmed disable ($measured)", async ({ measured }) => {
  redirectReply = async config => response(config, { ...workspace(false), httpsRedirectMeasured: measured }); const { state } = await mount();
  await state.toggleHttpsRedirect(false); expect(state.noticeError).toBe(true); expect(state.notice).not.toBe("panel.ssl.forceHttpsOff");
});
it("does not replace the selected site with a redirect response for another site", async () => {
  redirectReply = async config => response(config, { ...workspace(true), siteName: "other.test" }); const { state } = await mount(); await state.toggleHttpsRedirect(true);
  expect(state.workspace?.siteName).not.toBe("other.test"); expect(state.noticeError).toBe(true);
});
it("reports command rejection without a success notice", async () => {
  redirectReply = async config => { throw new AxiosError("Failed", "ERR_BAD_RESPONSE", config, undefined, { ...response(config, null), status: 503, data: { success: false, message: "ssl.https_redirect_failed" } }); };
  const { state } = await mount(); await state.toggleHttpsRedirect(true); expect(state.noticeError).toBe(true); expect(state.notice).toBe("ssl.https_redirect_failed");
});
it("keeps the native checkbox at the last measured value while a redirect change is pending", async () => {
  const pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  redirectReply = config => (request = config, pending.promise); const { state } = await mount();
  const checkbox = { checked: true }; const saving = state.handleRedirectChange({ target: checkbox } as unknown as Event);
  expect(checkbox.checked).toBe(false); await vi.waitFor(() => expect(request).toBeDefined());
  pending.resolve(response(request, workspace(false))); await saving;
  expect(checkbox.checked).toBe(false); expect(state.workspace?.httpsRedirectEnabled).toBe(false); expect(state.noticeError).toBe(true);
});
it.each([false, undefined])("renders unreadable/legacy redirect state as unknown with a retry instead of an unchecked enabled control (%s)", async measured => {
  initial = { ...workspace(), httpsRedirectMeasured: measured }; const { state } = await mount(); const html = await renderState(state);
  expect(html).toContain("panel.ssl.redirectUnknown"); expect(html).toContain("panel.common.retry");
  expect(html).toMatch(/<input(?=[^>]*type="checkbox")(?=[^>]*disabled)[^>]*>/);
  const spy = vi.spyOn(ssl, "setHttpsRedirect"); await state.toggleHttpsRedirect(true); expect(spy).not.toHaveBeenCalled();
});
it.each(["redirect", "issue", "import"])("blocks the %s handler when subscription writes become locked", async action => {
  const { state, subscription } = await mount(); state.issueForm.contactEmail = "owner@example.test";
  await state.handleFileChange({ target: { files: [{ name: "cert.pfx", arrayBuffer: async () => new TextEncoder().encode("fixture").buffer }] } } as unknown as Event);
  subscription.usage = { isSuspended: true } as SubscriptionUsage; subscription.applyReadOnly();
  const spy = vi.spyOn(ssl, action === "redirect" ? "setHttpsRedirect" : action === "issue" ? "issueLetsEncrypt" : "importCertificate");
  if (action === "redirect") await state.toggleHttpsRedirect(true); else if (action === "issue") await state.issue(); else await state.submitImport();
  expect(spy).not.toHaveBeenCalled(); expect(calls.filter(call => call.method === "post")).toHaveLength(0);
});
it("renders a disabled redirect checkbox for a suspended account", async () => {
  const { state, subscription } = await mount(); subscription.usage = { isSuspended: true } as SubscriptionUsage;
  const html = await renderState(state); expect(html).toMatch(/<input(?=[^>]*type="checkbox")(?=[^>]*disabled)(?=[^>]*aria-disabled="true")[^>]*>/);
});
it.each(["Administrator", "Reseller", "Customer"])("shows the server-wide renewal action only when the %s can use it", async userRole => {
  role = userRole; const { state } = await mount(); state.activeTab = "letsencrypt"; const html = await renderState(state);
  expect(/<button[^>]*>\s*panel\.ssl\.renew\s*<\/button>/.test(html)).toBe(userRole === "Administrator");
  expect(html).toContain("panel.ssl.issue");
});
it("allows a customer to issue and import certificates for their measured site", async () => {
  role = "Customer"; const { state } = await mount(); state.issueForm.contactEmail = "owner@example.test"; await state.issue(); expect(state.noticeError).toBe(false);
  await state.handleFileChange({ target: { files: [{ name: "cert.pfx", arrayBuffer: async () => new TextEncoder().encode("fixture").buffer }] } } as unknown as Event);
  await state.submitImport(); expect(state.noticeError).toBe(false); expect(calls.filter(call => call.method === "post").map(call => call.url)).toEqual(["/ssl/letsencrypt", "/ssl/import"]);
});
it.each(["site", "session", "unmount"])("does not publish a delayed redirect into a replaced %s context", async change => {
  const pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  redirectReply = config => (request = config, pending.promise); const { state, sites } = await mount(); const saving = state.toggleHttpsRedirect(true);
  await vi.waitFor(() => expect(request).toBeDefined());
  if (change === "site") { sites.selectedSiteName = "b.test"; await flush(); } else if (change === "session") invalidateSessionRequests(); else unmount();
  pending.resolve(response(request, workspace(true))); await saving; expect(state.notice).toBe("");
  if (change === "site") expect(state.workspace?.siteName).toBe("b.test");
});
it("recovers a previously unknown redirect measurement on refresh", async () => {
  initial = workspace(false, false); const { state } = await mount(); initial = workspace(true, true); await state.refresh();
  const html = await renderState(state); expect(html).not.toContain("panel.ssl.redirectUnknown"); await state.toggleHttpsRedirect(false); expect(state.noticeError).toBe(false);
});
