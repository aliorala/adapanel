import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { AxiosError, type AxiosResponse, type InternalAxiosRequestConfig } from "axios";
import { createRenderer, createSSRApp, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import { renderToString } from "@vue/server-renderer";
import SslView from "./SslView.vue";
import type { SslCertificate, SslWorkspace } from "../api/ssl";
import { apiClient, invalidateSessionRequests } from "../api/client";
import { useSitesStore } from "../stores/sites";
import { useAuthStore } from "../stores/auth";
import { useSubscriptionStore } from "../stores/subscription";
import type { SubscriptionUsage } from "../api/subscriptions";
import type { ConfirmOptions } from "../components/ConfirmDialog.vue";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string, values?: Record<string, unknown>) => values ? `${key}:${JSON.stringify(values)}` : key }) }));
const { confirm } = vi.hoisted(() => ({ confirm: vi.fn() }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm }) }));
type State = { workspace: SslWorkspace | null; notice: string; noticeError: boolean; busyAction: string; removeBinding(item: SslCertificate): Promise<void> };
const certificate = (hostName = "a.test", port = 443): SslCertificate => ({ siteName: "a.test", hostName, port, thumbprint: "ABCDEF", friendlyName: "fixture", subject: `CN=${hostName}`, notAfterUtc: "2027-01-01T00:00:00Z", source: "manual", isHttpsBindingPresent: true, isCertificateBound: true, bindingDetail: "", fallbackReason: "" });
const workspace = (siteName = "a.test", certificates = [certificate()]): SslWorkspace => ({ siteName, hostNames: [siteName], certificates, httpsRedirectEnabled: false, httpsRedirectMeasured: true });
const removed = () => ({ siteName: "a.test", hostName: "a.test", port: 443, bindingRemoved: true, httpsRedirectDisabled: false, certificateThumbprint: "ABCDEF", certificateAction: "kept", certificateUsedBy: [], detail: "", workspace: workspace("a.test", []) });
function response(config: InternalAxiosRequestConfig, data: unknown): AxiosResponse { return { config, data: { success: true, data }, headers: {}, status: 200, statusText: "OK" }; }
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
const originalAdapter = apiClient.defaults.adapter;
let app: App | undefined, calls: InternalAxiosRequestConfig[], role: string, initial: SslWorkspace;
let removeReply: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
async function flush() { for (let i = 0; i < 30; i++) await nextTick(); }
function unmount() { app?.unmount(); app = undefined; }
async function mount() {
  const pinia = createPinia(), auth = useAuthStore(pinia), sites = useSitesStore(pinia), subscription = useSubscriptionStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "actor", userName: "actor", email: "actor@example.test", role, preferredLanguage: "en", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } };
  sites.selectedSiteName = "a.test"; sites.sites = [{ id: 1, name: "a.test", state: "Started", physicalPath: "", applicationRootPath: "", applicationPoolName: "a.test", bindings: [], autoStart: true }];
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...SslView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return { state, sites, subscription, auth };
}
async function renderState(state: State) { return renderToString(createSSRApp({ ...SslView, setup: () => state } as Component)); }
function startRemoval(state: State, item = state.workspace!.certificates[0]!) {
  expect(typeof state.removeBinding, "The measured binding needs a usable removal action").toBe("function");
  return state.removeBinding(item);
}
function posts() { return calls.filter(call => call.method === "post"); }
beforeEach(() => {
  invalidateSessionRequests(); calls = []; role = "Administrator"; initial = workspace(); confirm.mockReset(); confirm.mockResolvedValue(true);
  vi.stubGlobal("window", { location: { pathname: "/panel", hostname: "localhost" } });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture-token" } }), { status: 200 })));
  removeReply = async config => response(config, removed());
  apiClient.defaults.adapter = async config => {
    calls.push(config);
    if (config.method === "get") { const site = decodeURIComponent(config.url!.split("/").pop()!); return response(config, site === "a.test" ? initial : workspace(site, [])); }
    if (config.url === "/ssl/remove") return removeReply(config);
    throw new Error(`Unexpected request ${config.method} ${config.url}`);
  };
});
afterEach(() => { unmount(); apiClient.defaults.adapter = originalAdapter; invalidateSessionRequests(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it.each(["Administrator", "Reseller", "Customer"])("offers scoped binding removal for %s and keeps the certificate in the store", async userRole => {
  role = userRole; const { state } = await mount(); const html = await renderState(state);
  expect(html).toContain("panel.ssl.removeBinding"); await startRemoval(state);
  expect(confirm).toHaveBeenCalledOnce(); expect(posts()).toHaveLength(1);
  expect(JSON.parse(String(posts()[0]!.data))).toEqual({ siteName: "a.test", hostName: "a.test", port: 443, removeCertificateFromStore: false });
  expect(state.workspace?.certificates).toEqual([]); expect(state.noticeError).toBe(false); expect(state.notice).toContain("panel.ssl.bindingRemoved");
});
it.each([false, null])("does not offer a destructive action for an absent/unmeasured binding (%s)", async present => {
  initial.certificates[0]!.isHttpsBindingPresent = present; const { state } = await mount(); const html = await renderState(state);
  expect(html).not.toMatch(/<button[^>]*>\s*panel\.ssl\.removeBinding\s*<\/button>/);
  await startRemoval(state); expect(confirm).not.toHaveBeenCalled(); expect(posts()).toHaveLength(0);
});
it("renders the removal action disabled for a locked account", async () => {
  const { state, subscription } = await mount(); subscription.usage = { isSuspended: true } as SubscriptionUsage;
  const html = await renderState(state);
  expect(html).toMatch(/<button(?=[^>]*disabled)(?=[^>]*aria-disabled="true")[^>]*>\s*panel\.ssl\.removeBinding\s*<\/button>/);
  await startRemoval(state); expect(confirm).not.toHaveBeenCalled(); expect(posts()).toHaveLength(0);
});
it("does nothing when the user declines confirmation", async () => {
  confirm.mockResolvedValue(false); const { state } = await mount(); await startRemoval(state);
  expect(posts()).toHaveLength(0); expect(state.notice).toBe(""); expect(state.busyAction).toBe("");
});
it.each(["site", "site-return", "session", "locked", "unmount"])("does not send a removal after %s changes while confirmation is open", async change => {
  const answer = deferred<boolean>(); confirm.mockReturnValue(answer.promise); const { state, sites, subscription } = await mount(); const removing = startRemoval(state);
  expect(confirm).toHaveBeenCalledOnce();
  if (change === "site" || change === "site-return") { sites.selectedSiteName = "b.test"; await flush(); if (change === "site-return") { sites.selectedSiteName = "a.test"; await flush(); } }
  else if (change === "session") invalidateSessionRequests();
  else if (change === "locked") { subscription.usage = { isSuspended: true } as SubscriptionUsage; subscription.applyReadOnly(); }
  else unmount();
  answer.resolve(true); await removing; expect(posts()).toHaveLength(0); expect(state.notice).toBe("");
});
it("reserves the action while confirmation and the removal request are pending", async () => {
  const answer = deferred<boolean>(), result = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  confirm.mockReturnValue(answer.promise); removeReply = config => (request = config, result.promise);
  const { state } = await mount(); const first = startRemoval(state); await startRemoval(state);
  expect(confirm).toHaveBeenCalledOnce(); expect(state.busyAction).toBe("remove");
  answer.resolve(true); await vi.waitFor(() => expect(request).toBeDefined()); await startRemoval(state); expect(posts()).toHaveLength(1);
  result.resolve(response(request, removed())); await first; expect(state.busyAction).toBe("");
});
it("confirms and sends the clicked host and custom port rather than the first certificate", async () => {
  initial.certificates.push(certificate("api.a.test", 8443));
  removeReply = async config => response(config, { ...removed(), hostName: "api.a.test", port: 8443, workspace: workspace("a.test", [certificate()]) });
  const { state } = await mount(); await startRemoval(state, state.workspace!.certificates[1]!);
  const options = confirm.mock.calls[0]![0] as ConfirmOptions; expect(JSON.stringify(options)).toContain("api.a.test"); expect(JSON.stringify(options)).toContain("8443");
  expect(JSON.parse(String(posts()[0]!.data))).toMatchObject({ siteName: "a.test", hostName: "api.a.test", port: 8443 }); expect(state.noticeError).toBe(false);
});
it.each(["site", "session", "unmount"])("does not publish a late removal response into a replaced %s context", async change => {
  const result = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig; removeReply = config => (request = config, result.promise);
  const { state, sites } = await mount(); const removing = startRemoval(state); await vi.waitFor(() => expect(request).toBeDefined());
  if (change === "site") { sites.selectedSiteName = "b.test"; await flush(); } else if (change === "session") invalidateSessionRequests(); else unmount();
  result.resolve(response(request, removed())); await removing; expect(state.notice).toBe("");
  if (change === "site") expect(state.workspace?.siteName).toBe("b.test");
});
it.each([false, undefined])("does not claim removal from an unconfirmed bindingRemoved value (%s)", async bindingRemoved => {
  removeReply = async config => response(config, { ...removed(), bindingRemoved }); const { state } = await mount(); await startRemoval(state);
  expect(state.noticeError).toBe(true); expect(state.notice).not.toContain("panel.ssl.bindingRemoved:");
});
it.each([{ siteName: "foreign.test" }, { hostName: "foreign.test" }, { port: 8443 }])("does not trust removal measured for a different target %j", async mismatch => {
  removeReply = async config => response(config, { ...removed(), ...mismatch }); const { state } = await mount(); await startRemoval(state);
  expect(state.noticeError).toBe(true); expect(state.workspace?.certificates).toHaveLength(1); expect(state.notice).not.toContain("panel.ssl.bindingRemoved:");
});
it.each([false, undefined])("reports confirmed removal separately from unknown redirect measurement (%s)", async httpsRedirectMeasured => {
  removeReply = async config => response(config, { ...removed(), workspace: { ...workspace("a.test", []), httpsRedirectMeasured } });
  const { state } = await mount(); await startRemoval(state);
  expect(state.workspace?.certificates).toEqual([]); expect(state.notice).toContain("panel.ssl.bindingRemoved:");
  expect(state.notice).toContain("panel.ssl.redirectUnverified"); expect(state.noticeError).toBe(true);
});
it("does not publish another site's workspace even when removal itself was measured", async () => {
  removeReply = async config => response(config, { ...removed(), workspace: workspace("foreign.test", []) }); const { state } = await mount(); await startRemoval(state);
  expect(state.workspace?.siteName).toBe("a.test"); expect(state.noticeError).toBe(true); expect(state.notice).toContain("panel.ssl.bindingRemoved:");
});
it("flags an inconsistent result that still lists the binding as present", async () => {
  removeReply = async config => response(config, { ...removed(), workspace: workspace() }); const { state } = await mount(); await startRemoval(state);
  expect(state.noticeError).toBe(true); expect(state.notice).toContain("panel.ssl.removalUnverified");
});
it("shows a failed request without implying that the binding was removed", async () => {
  removeReply = async config => { throw new AxiosError("Fixture rejected", "ERR_BAD_RESPONSE", config, undefined, { ...response(config, null), status: 409, data: { success: false, message: "ssl.remove_would_break_site" } }); };
  const { state } = await mount(); await startRemoval(state); expect(state.notice).toBe("ssl.remove_would_break_site"); expect(state.noticeError).toBe(true);
  expect(state.workspace?.certificates).toHaveLength(1); expect(state.busyAction).toBe("");
});
it("does not treat an unsuccessful API envelope as a confirmed removal", async () => {
  removeReply = async config => ({ ...response(config, removed()), data: { success: false, data: removed(), message: "ssl.remove_binding_failed" } });
  const { state } = await mount(); await startRemoval(state);
  expect(state.noticeError).toBe(true); expect(state.notice).not.toContain("panel.ssl.bindingRemoved:"); expect(state.workspace?.certificates).toHaveLength(1);
});
it("rejects a new actor context even when the selected site name stays the same", async () => {
  const answer = deferred<boolean>(); confirm.mockReturnValue(answer.promise); const { state, auth } = await mount(); const removing = startRemoval(state);
  auth.session!.user = { ...auth.session!.user!, id: "another-actor" }; answer.resolve(true); await removing;
  expect(posts()).toHaveLength(0); expect(state.notice).toBe("");
});
