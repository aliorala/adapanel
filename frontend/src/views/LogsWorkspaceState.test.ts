import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import LogsView from "./LogsView.vue";
import { getPanelLog, getSiteLog, getLogRotation, updateLogRotation, runLogRotation, type LogTail, type LogRotationStatus } from "../api/logs";
import { useSitesStore } from "../stores/sites";
import { useAuthStore } from "../stores/auth";
const confirm = vi.hoisted(() => vi.fn());
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm }) }));
vi.mock("../api/logs", async original => ({ ...await original<object>(), getPanelLog: vi.fn(), getSiteLog: vi.fn(), getLogRotation: vi.fn(), updateLogRotation: vi.fn(), runLogRotation: vi.fn() }));
const rotation: LogRotationStatus = { settings: { enabled: true, compress: true, siteLogMaxAgeDays: 90, panelLogMaxAgeDays: 60, maxFileSizeMB: 0, siteKeepCount: 0, panelKeepCount: 30, maxTotalSizeMB: 0, intervalHours: 24 }, lastRunUtc: null, nextRunUtc: null, targets: [], totalFreedBytes: 0 };
function log(name: string): LogTail { return { name, path: name, lines: [name], lastModifiedUtc: null }; }
let app: App | undefined;
beforeEach(() => { vi.mocked(getPanelLog).mockResolvedValue(log("panel")); vi.mocked(getSiteLog).mockImplementation(async name => log(name)); vi.mocked(getLogRotation).mockResolvedValue(rotation); vi.mocked(updateLogRotation).mockResolvedValue(rotation); vi.mocked(runLogRotation).mockResolvedValue(rotation); confirm.mockResolvedValue(true); });
afterEach(() => { app?.unmount(); app = undefined; vi.restoreAllMocks(); vi.clearAllMocks(); });
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
type State = { selectedSiteName: string; siteLog: LogTail; notice: string; saveRotation(): Promise<void>; runRotation(): Promise<void> };
async function flush() { for (let i = 0; i < 16; i++) await nextTick(); }
async function mount() {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia); useSitesStore().selectedSiteName = "a.test";
  vi.spyOn(useSitesStore(), "fetchSites").mockResolvedValue([]); vi.spyOn(useAuthStore(), "fetchAuditLogs").mockResolvedValue([]);
  app = renderer.createApp({ ...LogsView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return state;
}
it("cannot overwrite retention settings with defaults while their read is pending", async () => {
  const pending = deferred<LogRotationStatus>(); vi.mocked(getLogRotation).mockReturnValueOnce(pending.promise);
  const state = await mount(); await state.saveRotation(); expect(updateLogRotation).not.toHaveBeenCalled(); pending.resolve(rotation); await flush();
});
it("cannot save or run rotation when retention settings failed to load", async () => {
  vi.mocked(getLogRotation).mockRejectedValueOnce(new Error("fixture unavailable")); const state = await mount();
  await state.saveRotation(); await state.runRotation(); expect(updateLogRotation).not.toHaveBeenCalled(); expect(runLogRotation).not.toHaveBeenCalled();
});
it("does not start cleanup after its confirmation's screen unmounts", async () => {
  const state = await mount(); confirm.mockImplementationOnce(async () => { app!.unmount(); app = undefined; return true; });
  await state.runRotation(); expect(runLogRotation).not.toHaveBeenCalled();
});
it("ignores old site log results while the latest site is pending", async () => {
  const state = await mount(), first = deferred<LogTail>(), second = deferred<LogTail>();
  vi.mocked(getSiteLog).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
  state.selectedSiteName = "b.test"; await flush(); state.selectedSiteName = "c.test"; await flush();
  first.resolve(log("b.test")); await flush(); expect(state.siteLog.lines).toEqual([]); second.resolve(log("c.test")); await flush(); expect(state.siteLog.lines).toEqual(["c.test"]);
});
it("does not dispatch duplicate retention writes", async () => {
  const state = await mount(), pending = deferred<LogRotationStatus>(); vi.mocked(updateLogRotation).mockReturnValue(pending.promise);
  const first = state.saveRotation(), second = state.saveRotation(); pending.resolve(rotation); await Promise.all([first, second]); expect(updateLogRotation).toHaveBeenCalledTimes(1);
});
it("runs a confirmed cleanup after settings are measured", async () => {
  const state = await mount(); await state.runRotation(); expect(runLogRotation).toHaveBeenCalledOnce();
});
it("keeps a site-log read failure visible after the full refresh finishes", async () => {
  vi.mocked(getSiteLog).mockRejectedValue(new Error("fixture unreadable"));
  const state = await mount(); expect(state.notice).toBe("common.unexpected_error");
});
