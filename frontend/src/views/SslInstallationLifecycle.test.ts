import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { type AxiosResponse, type InternalAxiosRequestConfig } from "axios";
import { createRenderer, createSSRApp, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import { renderToString } from "@vue/server-renderer";
import SslView from "./SslView.vue";
import { apiClient, invalidateSessionRequests } from "../api/client";
import type { SslCertificate, SslWorkspace } from "../api/ssl";
import { useSitesStore } from "../stores/sites";
import { useAuthStore } from "../stores/auth";
import { useSubscriptionStore } from "../stores/subscription";
import type { SubscriptionUsage } from "../api/subscriptions";
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
type Certificate = SslCertificate & { isPresentInStore?: boolean | null };
type State = { workspace: SslWorkspace | null; notice: string; noticeError: boolean; activeTab: string; busyAction: string; loading: boolean; fileLoading: boolean;
  issueForm: { hostName: string; contactEmail: string; wildcard: boolean }; importForm: { hostName: string; password: string; fileName: string; base64Content: string };
  issue(): Promise<void>; submitImport(): Promise<void>; refresh(force?: boolean): Promise<boolean>; handleFileChange(event: Event): Promise<void> };
const certificate = (source = "manual"): Certificate => ({ siteName: "a.test", hostName: "a.test", port: 443, thumbprint: "A1B2C3D4", friendlyName: "fixture", subject: "CN=a.test", notAfterUtc: "2027-01-01T00:00:00Z", source, isHttpsBindingPresent: true, isCertificateBound: true, isPresentInStore: true, bindingDetail: "", fallbackReason: "" });
function workspace(siteName = "a.test", certificates: SslCertificate[] = []): SslWorkspace { return { siteName, hostNames: [siteName, `alt.${siteName}`], certificates, httpsRedirectEnabled: true, httpsRedirectMeasured: true }; }
function response(config: InternalAxiosRequestConfig, data: unknown): AxiosResponse { return { config, data: { success: true, data }, headers: {}, status: 200, statusText: "OK" }; }
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
function fileEvent(bytes = new Uint8Array([0, 128, 255, 1])) { return { target: { files: [{ name: "cert.pfx", arrayBuffer: async () => bytes.buffer }] } } as unknown as Event; }
let app: App | undefined, calls: InternalAxiosRequestConfig[], latest: Certificate | null;
let readReply: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>, writeReply: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
const originalAdapter = apiClient.defaults.adapter;
async function flush() { for (let i = 0; i < 32; i++) await nextTick(); }
function unmount() { app?.unmount(); app = undefined; }
async function mount() {
  const pinia = createPinia(), auth = useAuthStore(pinia), sites = useSitesStore(pinia), subscription = useSubscriptionStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "actor", userName: "actor", email: "actor@example.test", role: "Administrator", preferredLanguage: "en", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } };
  sites.selectedSiteName = "a.test"; sites.sites = [{ id: 1, name: "a.test", state: "Started", physicalPath: "", applicationRootPath: "", applicationPoolName: "a.test", bindings: [], autoStart: true }];
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...SslView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return { state, sites, auth, subscription };
}
async function prepare(action: string) { const fixture = await mount(); fixture.state.activeTab = action === "issue" ? "letsencrypt" : "import"; fixture.state.issueForm.contactEmail = "owner@example.test"; if (action === "import") await fixture.state.handleFileChange(fileEvent()); return fixture; }
function run(state: State, action: string) { return action === "issue" ? state.issue() : state.submitImport(); }
function posts() { return calls.filter(config => config.method === "post"); }
beforeEach(() => {
  invalidateSessionRequests(); calls = []; latest = null;
  vi.stubGlobal("window", { location: { pathname: "/panel", hostname: "localhost" } });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture-token" } }), { status: 200 })));
  readReply = async config => response(config, workspace(decodeURIComponent(config.url!.split("/").pop()!), latest ? [latest] : []));
  writeReply = async config => { latest = certificate(config.url === "/ssl/import" ? "manual" : "letsencrypt"); return response(config, latest); };
  apiClient.defaults.adapter = async config => { calls.push(config); return config.method === "get" ? readReply(config) : writeReply(config); };
});
afterEach(() => { unmount(); apiClient.defaults.adapter = originalAdapter; invalidateSessionRequests(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it.each(["issue", "import"])("only confirms a matching measured %s installation and its refreshed certificate", async action => {
  const { state } = await prepare(action); await run(state, action); expect(posts()).toHaveLength(1); expect(state.noticeError).toBe(false); expect(state.notice).toBe(action === "issue" ? "panel.ssl.issued" : "panel.ssl.imported");
});
it.each([undefined, null, false])("does not turn binding metadata %s into installation success", async isCertificateBound => {
  const { state } = await prepare("import"); writeReply = async config => { latest = { ...certificate(), isCertificateBound } as Certificate; return response(config, latest); };
  await state.submitImport(); expect(state.noticeError).toBe(true); expect(state.notice).not.toBe("panel.ssl.imported");
});
it.each([undefined, null, false])("does not claim installation when certificate-store presence is %s", async isPresentInStore => {
  const { state } = await prepare("import"); writeReply = async config => { latest = { ...certificate(), isPresentInStore }; return response(config, latest); };
  await state.submitImport(); expect(state.noticeError).toBe(true); expect(state.notice).not.toBe("panel.ssl.imported");
});
it.each([{ siteName: "other.test" }, { hostName: "other.test" }, { port: 8443 }, { thumbprint: "" }, { isHttpsBindingPresent: undefined }])("does not confirm a certificate with mismatched or missing installation identity %j", async mismatch => {
  const { state } = await prepare("issue"); writeReply = async config => { latest = { ...certificate("letsencrypt"), ...mismatch } as Certificate; return response(config, latest); };
  await state.issue(); expect(state.noticeError).toBe(true); expect(state.notice).not.toBe("panel.ssl.issued");
});
it.each(["missing", "different-thumbprint", "store-missing", "unbound"])("does not show green when the post-import GET is %s", async kind => {
  const { state } = await prepare("import"); readReply = async config => response(config, workspace("a.test", kind === "missing" ? [] : [{ ...certificate(), ...(kind === "different-thumbprint" ? { thumbprint: "DIFFERENT" } : kind === "store-missing" ? { isPresentInStore: false } : { isCertificateBound: false }) }]));
  await state.submitImport(); expect(state.noticeError).toBe(true); expect(state.notice).not.toBe("panel.ssl.imported");
});
it.each(["issue", "import"])("does not accept HTTP200 success:false for %s", async action => {
  const { state } = await prepare(action); writeReply = async config => { latest = certificate(action === "issue" ? "letsencrypt" : "manual"); return { ...response(config, latest), data: { success: false, data: latest, message: "ssl.binding_failed" } }; };
  await run(state, action); expect(state.noticeError).toBe(true); expect(state.notice).not.toBe(action === "issue" ? "panel.ssl.issued" : "panel.ssl.imported");
});
it.each(["provider.failed", ""])("shows self-signed issuance as a fallback warning even with reason '%s'", async fallbackReason => {
  const { state } = await prepare("issue"); writeReply = async config => { latest = { ...certificate("self-signed"), fallbackReason }; return response(config, latest); };
  await state.issue(); expect(state.noticeError).toBe(true); expect(state.notice).toContain("panel.ssl.fallbackTitle"); if (fallbackReason) expect(state.notice).toContain(fallbackReason);
});
it("allows an intentionally imported self-signed certificate with measured installation", async () => {
  const { state } = await prepare("import"); writeReply = async config => { latest = certificate("self-signed"); return response(config, latest); };
  await state.submitImport(); expect(state.noticeError).toBe(false); expect(state.notice).toBe("panel.ssl.imported");
});
it("keeps a successful import's preserved-redirect warning visible after refreshing its certificate", async () => {
  const { state } = await prepare("import");
  writeReply = async config => { latest = { ...certificate(), fallbackReason: "ssl.https_redirect_preserved" }; return response(config, latest); };
  await state.submitImport();
  expect(state.noticeError).toBe(true);
  expect(state.notice).toBe("ssl.https_redirect_preserved");
  expect(state.workspace?.certificates[0]?.isCertificateBound).toBe(true);
});
it("accepts a measured wildcard issuance for the requested base domain", async () => {
  const { state } = await prepare("issue"); state.issueForm.wildcard = true; writeReply = async config => { latest = { ...certificate("letsencrypt"), hostName: "*.a.test" }; return response(config, latest); };
  await state.issue(); expect(state.noticeError).toBe(false);
});
it("does not move the user away from a tab selected while issuance is pending", async () => {
  const { state } = await prepare("issue"), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  writeReply = config => (request = config, pending.promise); const issuing = state.issue(); await vi.waitFor(() => expect(request).toBeDefined());
  state.activeTab = "import"; latest = certificate("letsencrypt"); pending.resolve(response(request, latest)); await issuing; expect(state.activeTab).toBe("import");
});
it("preserves the host and email draft edited through enabled issue fields while issuing", async () => {
  const { state } = await prepare("issue"), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  writeReply = config => (request = config, pending.promise); const issuing = state.issue(); await vi.waitFor(() => expect(request).toBeDefined());
  state.issueForm.hostName = "alt.a.test"; state.issueForm.contactEmail = "new@example.test"; latest = certificate("letsencrypt"); pending.resolve(response(request, latest)); await issuing;
  expect(JSON.parse(String(request.data))).toMatchObject({ hostName: "a.test", contactEmail: "owner@example.test" }); expect(state.issueForm.hostName).toBe("alt.a.test"); expect(state.issueForm.contactEmail).toBe("new@example.test");
});
it("keeps import fields disabled while its immutable payload is pending", async () => {
  const { state } = await prepare("import"), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  writeReply = config => (request = config, pending.promise); const importing = state.submitImport(); await vi.waitFor(() => expect(request).toBeDefined());
  const html = await renderToString(createSSRApp({ ...SslView, setup: () => state } as Component)); expect(html).toMatch(/<input(?=[^>]*type="file")(?=[^>]*disabled)[^>]*>/);
  latest = certificate(); pending.resolve(response(request, latest)); await importing; expect(state.importForm.base64Content).toBe("");
});
it("reports an empty selected file instead of leaving a named but unusable upload", async () => {
  const { state } = await prepare("import"); await state.handleFileChange(fileEvent(new Uint8Array(0))); expect(state.noticeError).toBe(true); expect(state.importForm.fileName).toBe(""); expect(state.importForm.base64Content).toBe("");
});
it("encodes binary certificate bytes across chunk boundaries without corrupting the upload", async () => {
  const { state } = await prepare("import"), bytes = new Uint8Array(70000); for (let i = 0; i < bytes.length; i++) bytes[i] = i % 256;
  await state.handleFileChange(fileEvent(bytes)); await state.submitImport(); const submitted = JSON.parse(String(posts()[0]!.data));
  expect(Uint8Array.from(atob(submitted.base64Content), char => char.charCodeAt(0))).toEqual(bytes);
});
it.each(["site", "session", "unmount"])("does not store private upload content after %s changes during file reading", async change => {
  const { state, sites } = await prepare("import"), pending = deferred<ArrayBuffer>(); const reading = state.handleFileChange({ target: { files: [{ name: "new.pfx", arrayBuffer: () => pending.promise }] } } as unknown as Event);
  if (change === "site") { sites.selectedSiteName = "b.test"; await flush(); } else if (change === "session") invalidateSessionRequests(); else unmount();
  pending.resolve(new Uint8Array([1, 2, 3]).buffer); await reading; expect(state.importForm.base64Content).toBe("");
});
it("blocks upload submission when the subscription becomes locked during file reading", async () => {
  const { state, subscription } = await prepare("import"), pending = deferred<ArrayBuffer>(); const reading = state.handleFileChange({ target: { files: [{ name: "new.pfx", arrayBuffer: () => pending.promise }] } } as unknown as Event);
  subscription.usage = { isSuspended: true } as SubscriptionUsage; subscription.applyReadOnly(); pending.resolve(new Uint8Array([1, 2, 3]).buffer); await reading; await state.submitImport(); expect(posts()).toHaveLength(0);
});
it("keeps issuance/import closed during a real pending GET instead of allowing a GET→POST rollback", async () => {
  const { state } = await prepare("import"), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  readReply = config => (request = config, pending.promise); const reading = state.refresh(); await vi.waitFor(() => expect(request).toBeDefined());
  await state.issue(); await state.submitImport(); expect(posts()).toHaveLength(0); pending.resolve(response(request, workspace())); await reading;
});
it.each(["wrong-site", "failed-envelope", "invalid-shape"])("does not enable SSL operations using a %s GET workspace", async kind => {
  readReply = async config => kind === "failed-envelope" ? { ...response(config, workspace()), data: { success: false, data: workspace() } } : response(config, kind === "wrong-site" ? workspace("foreign.test") : { ...workspace(), hostNames: null });
  const { state } = await mount(); expect(state.workspace).toBeNull(); expect(state.noticeError).toBe(true);
});
