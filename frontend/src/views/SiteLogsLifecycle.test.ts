import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import SiteLogsView from "./SiteLogsView.vue";
import { getSiteLog, getSiteLogRotation, runSiteLogRotation, type LogTail, type LogRotationTarget } from "../api/logs";
import { useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
const { confirm } = vi.hoisted(() => ({ confirm: vi.fn() }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm }) }));
vi.mock("../api/logs", () => ({ getSiteLog: vi.fn(), getSiteLogRotation: vi.fn(), runSiteLogRotation: vi.fn() }));
type State = { log: LogTail | null; rotation: LogRotationTarget | null; notice: string; noticeError: boolean;
  loading: boolean; rotationBusy: boolean; load(): Promise<void>; runRotation(): Promise<void> };
const tail = (name = "a.test"): LogTail => ({ name, path: name, lines: [name], lastModifiedUtc: null });
const rotation = (siteName = "a.test", status = "success"): LogRotationTarget => ({ key: siteName, name: siteName, siteName,
  directory: "", directoryExists: true, lastRotationUtc: null, rotatedFiles: 1, deletedArchives: 0, freedBytes: 0,
  skippedLockedFiles: 0, skippedUnsafeFiles: 0, currentLogBytes: 0, currentArchiveBytes: 0, status, error: status === "error" ? "fixture failure" : "" });
function deferred<T>() { let resolve!: (value: T) => void, reject!: (error: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
let app: App | undefined;
async function flush() { for (let i = 0; i < 8; i++) await nextTick(); }
async function mount() {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia); const auth = useAuthStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "actor", role: "Administrator", userName: "actor", email: "a@example.test", preferredLanguage: "en", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } };
  const sites = useSitesStore(pinia); sites.selectedSiteName = "a.test";
  app = renderer.createApp({ ...SiteLogsView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return { state, auth, sites };
}
beforeEach(() => {
  vi.mocked(getSiteLog).mockImplementation(async name => tail(name)); vi.mocked(getSiteLogRotation).mockImplementation(async name => rotation(name));
  vi.mocked(runSiteLogRotation).mockImplementation(async name => rotation(name)); confirm.mockResolvedValue(true);
});
afterEach(() => { app?.unmount(); app = undefined; vi.resetAllMocks(); });

it.each(["site", "session", "unmount"])("does not dispatch a rotation confirmation after %s changes", async kind => {
  const { state, sites, auth } = await mount(); const pending = deferred<boolean>(); confirm.mockReturnValueOnce(pending.promise);
  const running = state.runRotation();
  if (kind === "site") sites.selectedSiteName = "b.test";
  else if (kind === "session") auth.session = null;
  else { app!.unmount(); app = undefined; }
  pending.resolve(true); await running; expect(runSiteLogRotation).not.toHaveBeenCalled();
});
it("loads the new site and ignores the previous site's delayed result and follow-up", async () => {
  const pending = deferred<LogTail>(); vi.mocked(getSiteLog).mockReturnValueOnce(pending.promise);
  const { state, sites } = await mount(); sites.selectedSiteName = "b.test"; await flush(); pending.resolve(tail()); await flush();
  expect(state.log?.name).toBe("b.test"); expect(state.rotation?.siteName).toBe("b.test");
  expect(getSiteLogRotation).not.toHaveBeenCalledWith("a.test");
});
it("keeps a late mutation result out of a different site's screen", async () => {
  const { state, sites } = await mount(); const pending = deferred<LogRotationTarget>(); vi.mocked(runSiteLogRotation).mockReturnValueOnce(pending.promise);
  const running = state.runRotation(); await flush(); sites.selectedSiteName = "b.test"; await flush(); pending.resolve(rotation()); await running;
  expect(state.rotation?.siteName).toBe("b.test"); expect(state.notice).toBe(""); expect(state.rotationBusy).toBe(false);
});
it("does not show success when rotation returns an error result", async () => {
  const { state } = await mount(); vi.mocked(runSiteLogRotation).mockResolvedValueOnce(rotation("a.test", "error")); await state.runRotation();
  expect(state.noticeError).toBe(true); expect(state.notice).not.toBe("panel.logs.rotationCompleted");
});
it("ignores a pre-mutation rotation read which completes after the mutation", async () => {
  const pending = deferred<LogRotationTarget>(); vi.mocked(getSiteLogRotation).mockReturnValueOnce(pending.promise);
  const { state } = await mount(); await state.runRotation(); pending.resolve({ ...rotation(), rotatedFiles: 0 }); await flush();
  expect(state.rotation?.rotatedFiles).toBe(1); expect(state.loading).toBe(false);
});
it("does not start a competing read while rotation is running", async () => {
  const { state } = await mount(); const pending = deferred<LogRotationTarget>(); vi.mocked(runSiteLogRotation).mockReturnValueOnce(pending.promise);
  const running = state.runRotation(); await flush(); const calls = vi.mocked(getSiteLog).mock.calls.length;
  await state.load(); expect(getSiteLog).toHaveBeenCalledTimes(calls);
  pending.resolve(rotation()); await running; expect(state.rotation?.rotatedFiles).toBe(1);
});
it("keeps readable log content when only rotation metadata fails", async () => {
  vi.mocked(getSiteLogRotation).mockRejectedValueOnce(new Error("Unavailable")); const { state } = await mount();
  expect(state.log?.name).toBe("a.test"); expect(state.rotation).toBeNull(); expect(state.loading).toBe(false);
});
