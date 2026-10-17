import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ref, ssrContextKey, type App, type Component } from "vue";
import SslView from "./SslView.vue";
import { getSslWorkspace, renewAllCertificates } from "../api/ssl";
import { useSitesStore } from "../stores/sites";
import { invalidateSessionRequests } from "../api/client";

const fixture = vi.hoisted(() => ({ role: "Administrator", locked: null as any }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string, values?: object) => key + (values ? ` ${JSON.stringify(values)}` : "") }) }));
vi.mock("../stores/auth", () => ({ useAuthStore: () => ({ session: { user: { role: fixture.role } } }), extractApiMessage: () => "fixture.read_failed", extractApiDetails: () => "" }));
vi.mock("../composables/useSubscriptionGate", () => ({ useSubscriptionGate: () => ({ locked: fixture.locked, writeGate: () => ({}) }) }));
vi.mock("../api/ssl", async original => ({ ...await original<object>(), getSslWorkspace: vi.fn(), renewAllCertificates: vi.fn() }));
type State = { notice: string; noticeError: boolean; busyAction: string; renew(): Promise<void> };
let app: App | undefined;
const success = () => ({ measured: true, renewedCount: 2, verifiedCount: 3, missingFromStoreCount: 0, unverifiedCount: 0, bindingDriftCount: 0, bindingsMeasured: true, failureReason: "", output: "fixture command output" });
beforeEach(() => {
  invalidateSessionRequests(); fixture.role = "Administrator"; fixture.locked = ref(false);
  vi.mocked(getSslWorkspace).mockImplementation(async siteName => ({ siteName, hostNames: [siteName], certificates: [], httpsRedirectEnabled: false, httpsRedirectMeasured: true }));
  vi.mocked(renewAllCertificates).mockResolvedValue(success());
});
afterEach(() => { app?.unmount(); app = undefined; invalidateSessionRequests(); vi.resetAllMocks(); });
async function flush() { for (let i = 0; i < 12; i++) await nextTick(); }
async function mount(site = "a.test") {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia); const sites = useSitesStore(); sites.selectedSiteName = site;
  vi.spyOn(sites, "fetchSites").mockResolvedValue([]);
  app = renderer.createApp({ ...SslView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return state;
}
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }

it("reports an unmeasured renewal as an error and preserves both provider diagnostics", async () => {
  vi.mocked(renewAllCertificates).mockResolvedValue({ ...success(), measured: false, failureReason: "fixture wacs unavailable", output: "fixture command refused" });
  const state = await mount(); await state.renew();
  expect(state.noticeError).toBe(true); expect(state.notice).toContain("panel.ssl.renewalFailed");
  expect(state.notice).toContain("fixture wacs unavailable"); expect(state.notice).toContain("fixture command refused");
});
it.each(["missingFromStoreCount", "bindingDriftCount"])("does not show green when %s is measured", async field => {
  vi.mocked(renewAllCertificates).mockResolvedValue({ ...success(), [field]: 2 });
  const state = await mount(); await state.renew();
  expect(state.noticeError).toBe(true); expect(state.notice).toContain("panel.ssl.renewalProblems");
  expect(state.notice).toContain("fixture command output");
});
it.each([{ unverifiedCount: 1 }, { bindingsMeasured: false }, { measured: undefined }, { verifiedCount: undefined }, { renewedCount: -1 }, { missingFromStoreCount: "0" }])("does not invent a complete result from incomplete metadata %j", async partial => {
  vi.mocked(renewAllCertificates).mockResolvedValue({ ...success(), ...partial } as any);
  const state = await mount(); await state.renew();
  expect(state.noticeError).toBe(true); expect(state.notice).toContain("panel.ssl.renewalUnverified");
});
it("shows measured counts for a verified successful renewal", async () => {
  const state = await mount(); await state.renew();
  expect(state.noticeError).toBe(false); expect(state.notice).toContain('panel.ssl.renewalSummary {"renewed":2,"verified":3}');
  expect(state.notice).toContain("fixture command output");
});
it("shows a reported renewal warning as an error while preserving measured counts", async () => {
  vi.mocked(renewAllCertificates).mockResolvedValue({ ...success(), failureReason: "win-acme reported errors", output: "Renewal for example.test succeeded with errors" });
  const state = await mount(); await state.renew();
  expect(state.noticeError).toBe(true);
  expect(state.notice).toContain('panel.ssl.renewalSummary {"renewed":2,"verified":3}');
  expect(state.notice).toContain("win-acme reported errors");
  expect(state.notice).toContain("succeeded with errors");
});
it("reports the server-wide result even when no site is selected", async () => {
  const state = await mount(""); await state.renew();
  expect(renewAllCertificates).toHaveBeenCalledTimes(1); expect(getSslWorkspace).not.toHaveBeenCalled();
  expect(state.notice).toContain("panel.ssl.renewalSummary"); expect(state.noticeError).toBe(false);
});
it("retains the renewal failure when the following workspace refresh also fails", async () => {
  const state = await mount(); vi.mocked(renewAllCertificates).mockResolvedValue({ ...success(), measured: false, failureReason: "fixture renewal failure" });
  vi.mocked(getSslWorkspace).mockRejectedValueOnce(new Error("read failed")); await state.renew();
  expect(state.noticeError).toBe(true); expect(state.notice).toContain("fixture renewal failure"); expect(state.notice).toContain("fixture.read_failed");
});
it.each(["Customer", "Reseller"])("rejects direct renewal dispatch for %s", async role => {
  fixture.role = role; const state = await mount(); await state.renew(); expect(renewAllCertificates).not.toHaveBeenCalled();
});
it("blocks renewal while subscription writes are locked", async () => {
  const state = await mount(); fixture.locked.value = true; await state.renew(); expect(renewAllCertificates).not.toHaveBeenCalled();
});
it("dispatches only one renewal during an in-flight operation", async () => {
  const state = await mount(), pending = deferred<ReturnType<typeof success>>(); vi.mocked(renewAllCertificates).mockReturnValueOnce(pending.promise);
  const first = state.renew(); await state.renew(); expect(renewAllCertificates).toHaveBeenCalledTimes(1); pending.resolve(success()); await first;
});
it.each(["site", "session", "unmount"])("does not publish a renewal into a replaced %s context", async change => {
  const state = await mount(), pending = deferred<ReturnType<typeof success>>(); vi.mocked(renewAllCertificates).mockReturnValueOnce(pending.promise);
  const first = state.renew();
  if (change === "site") { useSitesStore().selectedSiteName = "b.test"; await flush(); }
  else if (change === "session") invalidateSessionRequests();
  else { app?.unmount(); app = undefined; }
  const reads = vi.mocked(getSslWorkspace).mock.calls.length; pending.resolve(success()); await first;
  expect(state.notice).toBe(""); expect(getSslWorkspace).toHaveBeenCalledTimes(reads);
});
