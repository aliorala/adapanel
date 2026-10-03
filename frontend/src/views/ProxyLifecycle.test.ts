import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { AxiosError, type AxiosResponse, type InternalAxiosRequestConfig } from "axios";
import { createRenderer, createSSRApp, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import { renderToString } from "@vue/server-renderer";
import ProxyView from "./ProxyView.vue";
import { apiClient, invalidateSessionRequests } from "../api/client";
import type { ReverseProxyConfiguration, SaveReverseProxyPayload, SaveRewriteRulePayload } from "../api/proxy";
import { useProxyStore } from "../stores/proxy";
import { useSitesStore } from "../stores/sites";
import { useAuthStore } from "../stores/auth";
import { useSubscriptionStore } from "../stores/subscription";
import type { SubscriptionUsage } from "../api/subscriptions";
import type { ConfirmOptions } from "../components/ConfirmDialog.vue";
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
const { confirm } = vi.hoisted(() => ({ confirm: vi.fn() }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm }) }));
type State = { proxyForm: SaveReverseProxyPayload; rewriteForm: SaveRewriteRulePayload; notice: string; noticeError: boolean; configFailed: boolean; activeSection: string; refreshConfiguration(): Promise<unknown>; saveProxy(): Promise<void>; removeProxy(): Promise<void>; saveRewriteRule(): Promise<void> };
const configuration = (siteName = "a.test", destinationUrl = "http://127.0.0.1:3000"): ReverseProxyConfiguration => ({ siteName, destinationUrl, mountPath: "/", isProxyEnabled: !!destinationUrl, isArrEnabled: true, hasHtmlLinkRewrite: false, preserveHostHeader: true, reverseRewriteHostInResponseHeaders: false, rewriteRules: [], webConfigPath: "" });
const rule = (name = "rule-a"): SaveRewriteRulePayload => ({ name, matchPattern: "^old/(.*)$", rewriteUrl: "/new/{R:1}", stopProcessing: true, appendQueryString: true, ignoreCase: true });
function deferred<T>() { let resolve!: (value: T) => void, reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
function response(config: InternalAxiosRequestConfig, data: unknown): AxiosResponse { return { config, data: { success: true, data }, headers: {}, status: 200, statusText: "OK" }; }
function failure(config: InternalAxiosRequestConfig) { return new AxiosError("Fixture failure", "ERR_BAD_RESPONSE", config, undefined, { ...response(config, null), status: 503, data: { success: false, message: "proxy.configuration_failed" } }); }
function siteOf(config: InternalAxiosRequestConfig) { return decodeURIComponent(config.url!.split("/")[2]!); }
let app: App | undefined, calls: InternalAxiosRequestConfig[], readReply: (c: InternalAxiosRequestConfig) => Promise<AxiosResponse>, writeReply: (c: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
const originalAdapter = apiClient.defaults.adapter;
async function flush() { for (let i = 0; i < 28; i++) await nextTick(); }
function unmount() { app?.unmount(); app = undefined; }
async function mount() {
  const pinia = createPinia(), auth = useAuthStore(pinia), sites = useSitesStore(pinia), store = useProxyStore(pinia), subscription = useSubscriptionStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "actor", userName: "actor", email: "actor@example.test", role: "Reseller", preferredLanguage: "en", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } };
  sites.selectedSiteName = "a.test"; sites.sites = ["a.test", "b.test"].map((name, i) => ({ id: i + 1, name, state: "Started", physicalPath: "", applicationRootPath: "", applicationPoolName: name, bindings: [], autoStart: true }));
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...ProxyView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return { state, store, sites, subscription, auth };
}
async function renderState(state: State) { return renderToString(createSSRApp({ ...ProxyView, setup: () => state } as Component)); }
function writes() { return calls.filter(c => c.method !== "get"); }
beforeEach(() => {
  invalidateSessionRequests(); calls = []; confirm.mockReset(); confirm.mockImplementation(async (options: ConfirmOptions) => { await options.action?.(); return true; });
  vi.stubGlobal("window", { location: { pathname: "/panel", hostname: "localhost" } });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture-token" } }), { status: 200 })));
  readReply = async config => response(config, configuration(siteOf(config)));
  writeReply = async config => {
    const site = siteOf(config), payload = config.data ? JSON.parse(String(config.data)) : {};
    return response(config, config.method === "delete" ? configuration(site, "") : config.url?.endsWith("/rewrite") ? { ...configuration(site), rewriteRules: [payload] } : { ...configuration(site, payload.destinationUrl), mountPath: payload.mountPath, hasHtmlLinkRewrite: payload.mountPath !== "/" && payload.enableHtmlLinkRewrite });
  };
  apiClient.defaults.adapter = async config => { calls.push(config); return config.method === "get" ? readReply(config) : writeReply(config); };
});
afterEach(() => { unmount(); apiClient.defaults.adapter = originalAdapter; invalidateSessionRequests(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it("keeps a saved configuration and its success notice when an older GET completes later", async () => {
  const { state, store } = await mount(); const old = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  readReply = config => (request = config, old.promise); const reading = state.refreshConfiguration(); await vi.waitFor(() => expect(request).toBeDefined());
  state.proxyForm.destinationUrl = "http://127.0.0.1:4000"; await state.saveProxy();
  old.resolve(response(request, configuration("a.test", "http://127.0.0.1:3000"))); await reading;
  expect(store.getConfiguration("a.test")?.destinationUrl).toBe("http://127.0.0.1:4000"); expect(state.proxyForm.destinationUrl).toBe("http://127.0.0.1:4000"); expect(state.notice).toBe("proxy.proxy_enabled");
});
it("does not overwrite edits made during an explicit configuration refresh", async () => {
  const { state } = await mount(); const old = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  readReply = config => (request = config, old.promise); const reading = state.refreshConfiguration(); await vi.waitFor(() => expect(request).toBeDefined());
  state.proxyForm.destinationUrl = "http://127.0.0.1:5000"; old.resolve(response(request, configuration())); await reading;
  expect(state.proxyForm.destinationUrl).toBe("http://127.0.0.1:5000");
});
it("does not discard a dirty draft that already existed when refresh was clicked", async () => {
  const { state } = await mount(); state.proxyForm.destinationUrl = "http://127.0.0.1:5000"; await state.refreshConfiguration(); expect(state.proxyForm.destinationUrl).toBe("http://127.0.0.1:5000");
});
it("keeps edits made while proxy save is pending, while displaying the submitted server result", async () => {
  const { state, store } = await mount(); const pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  writeReply = config => (request = config, pending.promise); state.proxyForm.destinationUrl = "http://127.0.0.1:4000"; const saving = state.saveProxy(); await vi.waitFor(() => expect(request).toBeDefined());
  state.proxyForm.destinationUrl = "http://127.0.0.1:5000"; pending.resolve(response(request, configuration("a.test", "http://127.0.0.1:4000"))); await saving;
  expect(state.proxyForm.destinationUrl).toBe("http://127.0.0.1:5000"); expect(store.getConfiguration("a.test")?.destinationUrl).toBe("http://127.0.0.1:4000");
});
it("resets the rewrite draft when the visible site selector changes", async () => {
  const { state, sites } = await mount(); state.activeSection = "rewrite"; Object.assign(state.rewriteForm, rule()); sites.selectedSiteName = "b.test"; await flush();
  expect(state.rewriteForm).toEqual({ ...rule(""), matchPattern: "", rewriteUrl: "" }); await state.saveRewriteRule(); expect(writes()).toHaveLength(0);
});
it("does not clear a newer rewrite draft on the same site after saving an earlier one", async () => {
  const { state } = await mount(); state.activeSection = "rewrite"; Object.assign(state.rewriteForm, rule()); const pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  writeReply = config => (request = config, pending.promise); const saving = state.saveRewriteRule(); await vi.waitFor(() => expect(request).toBeDefined());
  Object.assign(state.rewriteForm, rule("rule-new")); pending.resolve(response(request, { ...configuration(), rewriteRules: [rule()] })); await saving;
  expect(state.rewriteForm.name).toBe("rule-new");
});
it.each(["proxy", "rewrite", "remove"])("keeps B's editable draft and notice when A's %s finishes", async action => {
  const { state, sites } = await mount(); Object.assign(state.rewriteForm, rule()); const pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  writeReply = config => (request = config, pending.promise); const saving = action === "proxy" ? state.saveProxy() : action === "rewrite" ? state.saveRewriteRule() : state.removeProxy();
  await vi.waitFor(() => expect(request).toBeDefined()); sites.selectedSiteName = "b.test"; await flush();
  state.proxyForm.destinationUrl = "http://127.0.0.1:6000"; Object.assign(state.rewriteForm, rule("rule-b"));
  pending.resolve(response(request, action === "remove" ? configuration("a.test", "") : { ...configuration(), rewriteRules: [rule()] })); await saving;
  expect(state.proxyForm.destinationUrl).toBe("http://127.0.0.1:6000"); expect(state.rewriteForm.name).toBe("rule-b"); expect(state.notice).toBe("");
});
it.each(["session", "unmount"])("does not publish errors or a success notice after %s changes during a save", async change => {
  const { state } = await mount(); const pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  writeReply = config => (request = config, pending.promise); const saving = state.saveProxy(); await vi.waitFor(() => expect(request).toBeDefined());
  if (change === "session") invalidateSessionRequests(); else unmount(); pending.resolve(response(request, configuration())); await saving; expect(state.notice).toBe("");
});
it.each(["site", "site-return", "session", "locked", "unmount"])("rechecks %s context before confirmed removal sends a request", async change => {
  const answer = deferred<boolean>(); let options!: ConfirmOptions; confirm.mockImplementation(value => (options = value, answer.promise));
  const { state, sites, subscription } = await mount(); const removing = state.removeProxy(); expect(options).toBeDefined();
  if (change === "site" || change === "site-return") { sites.selectedSiteName = "b.test"; await flush(); if (change === "site-return") { sites.selectedSiteName = "a.test"; await flush(); } }
  else if (change === "session") invalidateSessionRequests(); else if (change === "locked") { subscription.usage = { isSuspended: true } as SubscriptionUsage; subscription.applyReadOnly(); } else unmount();
  await options.action?.(); answer.resolve(true); await removing; expect(writes()).toHaveLength(0); expect(state.notice).toBe("");
});
it("reserves removal before opening confirmation and prevents another write", async () => {
  const answer = deferred<boolean>(); confirm.mockReturnValue(answer.promise); const { state } = await mount(); const removing = state.removeProxy();
  const html = await renderState(state); expect(html).toMatch(/<button(?=[^>]*disabled)[^>]*>\s*panel\.proxy\.save\s*<\/button>/);
  const second = state.removeProxy(); answer.resolve(false); await Promise.all([removing, second]); expect(confirm).toHaveBeenCalledOnce(); expect(writes()).toHaveLength(0);
});
it("blocks a visible remove action while proxy save is pending", async () => {
  const { state } = await mount(); const pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  writeReply = config => (request = config, pending.promise); const saving = state.saveProxy(); await vi.waitFor(() => expect(request).toBeDefined());
  const html = await renderState(state); expect(html).toMatch(/<button(?=[^>]*disabled)[^>]*>\s*panel\.proxy\.remove\s*<\/button>/);
  const removing = state.removeProxy(); pending.resolve(response(request, configuration())); await Promise.all([saving, removing]); expect(confirm).not.toHaveBeenCalled();
});
it("closes all mutation gates after a failed refresh of cached configuration and recovers on retry", async () => {
  const { state } = await mount(); readReply = async config => { throw failure(config); }; await state.refreshConfiguration();
  Object.assign(state.rewriteForm, rule()); await state.saveProxy(); await state.saveRewriteRule(); await state.removeProxy(); expect(writes()).toHaveLength(0); expect(confirm).not.toHaveBeenCalled();
  readReply = async config => response(config, configuration()); await state.refreshConfiguration(); await state.saveProxy(); expect(writes()).toHaveLength(1); expect(state.noticeError).toBe(false);
});
it.each(["proxy", "rewrite", "remove"])("does not claim %s succeeded when the response does not contain its measured effect", async action => {
  const { state } = await mount(); Object.assign(state.rewriteForm, rule());
  writeReply = async config => response(config, action === "proxy" ? configuration("a.test", "") : configuration());
  await (action === "proxy" ? state.saveProxy() : action === "rewrite" ? state.saveRewriteRule() : state.removeProxy()); expect(state.noticeError).toBe(true); expect(state.notice).toBe("panel.proxy.resultUnconfirmed");
});
it.each(["missing-status", "wrong-site", "failed-envelope"])("rejects an unmeasured/invalid %s configuration response", async kind => {
  const { state, store } = await mount(); writeReply = async config => { const data = kind === "wrong-site" ? configuration("foreign.test") : kind === "missing-status" ? { ...configuration(), isProxyEnabled: undefined } : configuration(); return { ...response(config, data), data: { success: kind !== "failed-envelope", data } }; };
  await state.saveProxy(); expect(state.noticeError).toBe(true); expect(store.getConfiguration("a.test")?.siteName).toBe("a.test");
});
it.each(["proxy", "rewrite", "remove"])("keeps the normal measured %s path working", async action => {
  const { state } = await mount(); Object.assign(state.rewriteForm, rule()); await (action === "proxy" ? state.saveProxy() : action === "rewrite" ? state.saveRewriteRule() : state.removeProxy());
  expect(writes()).toHaveLength(1); expect(state.noticeError).toBe(false); if (action === "rewrite") expect(state.rewriteForm.name).toBe("");
});
it("accepts server URL/mount normalization and root mount without HTML rewriting", async () => {
  const { state } = await mount(); Object.assign(state.proxyForm, { destinationUrl: " http://LOCALHOST:80/api/ ", mountPath: "/", enableHtmlLinkRewrite: true });
  writeReply = async config => response(config, configuration("a.test", "http://localhost/api")); await state.saveProxy(); expect(state.noticeError).toBe(false);
});
it("accepts the backend's trimmed rewrite name, pattern and destination", async () => {
  const { state } = await mount(); Object.assign(state.rewriteForm, { ...rule(), name: "  kept name  ", matchPattern: "  ^old/(.*)$  ", rewriteUrl: " /new/{R:1} " });
  writeReply = async config => response(config, { ...configuration(), rewriteRules: [rule("kept name")] });
  await state.saveRewriteRule(); expect(state.noticeError).toBe(false); expect(state.notice).toBe("proxy.rewrite_rule_saved"); expect(state.rewriteForm.name).toBe("");
});
it("does not overwrite B's successful load error state with A's late read failure", async () => {
  const { state, sites } = await mount(); const old = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  readReply = config => siteOf(config) === "a.test" ? (request = config, old.promise) : Promise.resolve(response(config, configuration("b.test")));
  const reading = state.refreshConfiguration(); await vi.waitFor(() => expect(request).toBeDefined()); sites.selectedSiteName = "b.test"; await flush(); old.reject(failure(request)); await reading;
  expect(state.configFailed).toBe(false); expect(state.notice).toBe("");
});
