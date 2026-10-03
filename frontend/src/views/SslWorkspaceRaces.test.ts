import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import SslView from "./SslView.vue";
import { getSslWorkspace, importCertificate, setHttpsRedirect, type SslWorkspace, type SslCertificate } from "../api/ssl";
import { useSitesStore } from "../stores/sites";
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../api/ssl", async original => ({ ...await original<object>(), getSslWorkspace: vi.fn(), importCertificate: vi.fn(), setHttpsRedirect: vi.fn() }));
function workspace(siteName: string): SslWorkspace { return { siteName, hostNames: [siteName], certificates: [], httpsRedirectEnabled: false, httpsRedirectMeasured: true }; }
function certificate(siteName = "a.test"): SslCertificate { return { siteName, hostName: siteName, port: 443, thumbprint: "FIXTURE", friendlyName: "fixture", subject: `CN=${siteName}`, notAfterUtc: "2027-01-01T00:00:00Z", source: "manual", isCertificateBound: true, isHttpsBindingPresent: true, isPresentInStore: true, bindingDetail: "", fallbackReason: "" }; }
let app: App | undefined;
beforeEach(() => { vi.mocked(getSslWorkspace).mockImplementation(async site => ({ ...workspace(site), certificates: [certificate(site)] })); vi.mocked(importCertificate).mockResolvedValue(certificate()); });
afterEach(() => { app?.unmount(); app = undefined; vi.restoreAllMocks(); vi.clearAllMocks(); });
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
type State = { workspace: SslWorkspace | null; importForm: { hostName: string; password: string; fileName: string; base64Content: string }; notice: string; handleFileChange(event: Event): Promise<void>; submitImport(): Promise<void>; toggleHttpsRedirect(enabled: boolean): Promise<void> };
async function flush() { for (let i = 0; i < 8; i++) await nextTick(); }
async function mount() {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia); useSitesStore().selectedSiteName = "a.test"; vi.spyOn(useSitesStore(), "fetchSites").mockResolvedValue([]);
  app = renderer.createApp({ ...SslView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return state;
}
async function selectSite(name: string) { useSitesStore().selectedSiteName = name; await flush(); }
function fileEvent(name: string, promise: Promise<ArrayBuffer>) { return { target: { files: [{ name, arrayBuffer: () => promise }] } } as unknown as Event; }
function buffer(value: string): ArrayBuffer { return new TextEncoder().encode(value).buffer as ArrayBuffer; }
it("does not combine the new filename with an old file while reading", async () => {
  const state = await mount(), pending = deferred<ArrayBuffer>();
  Object.assign(state.importForm, { fileName: "old.pfx", base64Content: "OLD", password: "fixture" });
  const reading = state.handleFileChange(fileEvent("new.pfx", pending.promise)); await state.submitImport();
  expect(importCertificate).not.toHaveBeenCalled(); pending.resolve(buffer("new")); await reading;
});
it("keeps the latest selected file when earlier reads complete last", async () => {
  const state = await mount(), first = deferred<ArrayBuffer>(), second = deferred<ArrayBuffer>();
  const readFirst = state.handleFileChange(fileEvent("first.pfx", first.promise));
  const readSecond = state.handleFileChange(fileEvent("second.pfx", second.promise));
  second.resolve(buffer("second")); await readSecond; first.resolve(buffer("first")); await readFirst;
  expect(state.importForm.fileName).toBe("second.pfx"); expect(state.importForm.base64Content).toBe(btoa("second"));
});
it("does not keep an old site's private key or password on site change", async () => {
  const state = await mount(), pending = deferred<ArrayBuffer>(); state.importForm.password = "fixture";
  const reading = state.handleFileChange(fileEvent("a.pfx", pending.promise)); await selectSite("b.test"); pending.resolve(buffer("private-key")); await reading;
  expect(state.importForm.password).toBe(""); expect(state.importForm.base64Content).toBe(""); expect(state.importForm.hostName).toBe("b.test");
});
it("does not expose an old certificate workspace under the new site", async () => {
  const first = deferred<SslWorkspace>(), second = deferred<SslWorkspace>(); vi.mocked(getSslWorkspace).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
  const state = await mount(); await selectSite("b.test"); first.resolve(workspace("a.test")); await flush();
  expect(state.workspace).toBeNull(); second.resolve(workspace("b.test")); await flush();
});
it("ignores an old HTTPS redirect result after site change", async () => {
  const state = await mount(), pending = deferred<SslWorkspace>(); vi.mocked(setHttpsRedirect).mockReturnValueOnce(pending.promise);
  const saving = state.toggleHttpsRedirect(true); await selectSite("b.test"); pending.resolve({ ...workspace("a.test"), httpsRedirectEnabled: true }); await saving;
  expect(state.workspace?.siteName).toBe("b.test"); expect(state.workspace?.httpsRedirectEnabled).toBe(false);
});
it("does not clear a new import form after an old site's import finishes", async () => {
  const state = await mount(), pending = deferred<SslCertificate>(); vi.mocked(importCertificate).mockReturnValueOnce(pending.promise);
  Object.assign(state.importForm, { fileName: "a.pfx", base64Content: "YQ==" }); const saving = state.submitImport(); await selectSite("b.test");
  Object.assign(state.importForm, { fileName: "b.pfx", base64Content: "Yg==" }); pending.resolve({ isCertificateBound: true } as SslCertificate); await saving;
  expect(state.importForm.fileName).toBe("b.pfx"); expect(state.importForm.base64Content).toBe("Yg=="); expect(state.notice).toBe("");
});
it("reports local file read errors without leaving stale content available for import", async () => {
  const state = await mount(); state.importForm.base64Content = "OLD";
  await state.handleFileChange(fileEvent("broken.pfx", Promise.reject(new Error("fixture read failure"))));
  expect(state.importForm.base64Content).toBe(""); expect(state.importForm.fileName).toBe("");
  expect(state.notice).toBe("panel.ssl.fileReadFailed"); await state.submitImport(); expect(importCertificate).not.toHaveBeenCalled();
});
it("imports the latest file normally and clears its password and content after success", async () => {
  const state = await mount(); state.importForm.password = "fixture";
  await state.handleFileChange(fileEvent("cert.pfx", Promise.resolve(buffer("cert")))); await state.submitImport();
  expect(importCertificate).toHaveBeenCalledWith({ siteName: "a.test", hostName: "a.test", password: "fixture", fileName: "cert.pfx", base64Content: btoa("cert"), port: 443 });
  expect(state.importForm.password).toBe(""); expect(state.importForm.base64Content).toBe(""); expect(state.notice).toBe("panel.ssl.imported");
});
it("does not overwrite a failed post-import refresh with a green success message", async () => {
  const state = await mount(); Object.assign(state.importForm, { fileName: "cert.pfx", base64Content: "YQ==" });
  vi.mocked(getSslWorkspace).mockRejectedValueOnce(new Error("fixture refresh failure")); await state.submitImport();
  expect(state.notice).toBe("common.unexpected_error"); expect(state.workspace).toBeNull();
});
