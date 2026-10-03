import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import DbToolSettingsView from "./DbToolSettingsView.vue";
import { getGlobalPhpSettings, updateGlobalPhpSettings, type SitePhpSettings } from "../api/php";
import { getRemoteDbAccess, setRemoteDbAccess } from "../api/databases";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../api/php", async original => ({ ...await original<object>(), getGlobalPhpSettings: vi.fn(), updateGlobalPhpSettings: vi.fn() }));
vi.mock("../api/databases", async original => ({ ...await original<object>(), getRemoteDbAccess: vi.fn(), setRemoteDbAccess: vi.fn() }));
const php: SitePhpSettings = { siteName: "global", memoryLimit: "256M", maxExecutionTime: 120, uploadMaxFilesize: "64M", postMaxSize: "64M", displayErrors: false, configured: true };
const remote = { enabled: true, allowedIp: "192.0.2.10", serverHost: "db.example.test", port: 3306 };
let app: App | undefined;
beforeEach(() => { vi.mocked(getGlobalPhpSettings).mockResolvedValue(php); vi.mocked(getRemoteDbAccess).mockResolvedValue(remote); vi.mocked(setRemoteDbAccess).mockResolvedValue(remote); vi.mocked(updateGlobalPhpSettings).mockResolvedValue(php); });
afterEach(() => { app?.unmount(); app = undefined; vi.clearAllMocks(); });
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
type State = { form: SitePhpSettings; loaded: boolean; remoteLoaded: boolean; remoteError: string; save(): Promise<void>; setRemote(enabled: boolean): Promise<void>; refreshAll(): Promise<void> };
async function flush() { for (let i = 0; i < 8; i++) await nextTick(); }
async function mount() {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...DbToolSettingsView, render: () => h("section") } as Component);
  app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState;
  await flush(); return state;
}
it("cannot change remote access before its existing allowlist is known", async () => {
  const pending = deferred<typeof remote>(); vi.mocked(getRemoteDbAccess).mockReturnValueOnce(pending.promise);
  const state = await mount(); await state.setRemote(true);
  expect(setRemoteDbAccess).not.toHaveBeenCalled(); pending.resolve(remote); await flush();
});
it("cannot overwrite remote access after the initial read fails", async () => {
  vi.mocked(getRemoteDbAccess).mockRejectedValueOnce(new Error("unavailable"));
  const state = await mount(); await state.setRemote(true); expect(setRemoteDbAccess).not.toHaveBeenCalled();
});
it("does not duplicate a pending remote access change", async () => {
  const state = await mount(), pending = deferred<typeof remote>(); vi.mocked(setRemoteDbAccess).mockReturnValue(pending.promise);
  const first = state.setRemote(false), second = state.setRemote(false); pending.resolve({ ...remote, enabled: false }); await Promise.all([first, second]);
  expect(setRemoteDbAccess).toHaveBeenCalledTimes(1);
});
it("does not save initial placeholder PHP values before loading finishes", async () => {
  const pending = deferred<SitePhpSettings>(); vi.mocked(getGlobalPhpSettings).mockReturnValueOnce(pending.promise);
  const state = await mount(); await state.save(); expect(updateGlobalPhpSettings).not.toHaveBeenCalled(); pending.resolve(php); await flush();
});
it("does not refresh or show completed settings after unmount during a save", async () => {
  const state = await mount(), pending = deferred<SitePhpSettings>(); vi.mocked(updateGlobalPhpSettings).mockReturnValueOnce(pending.promise);
  const saving = state.save(); app!.unmount(); app = undefined; pending.resolve(php); await saving;
  expect(getGlobalPhpSettings).toHaveBeenCalledTimes(1);
});
it("does not dispatch a duplicate PHP save", async () => {
  const state = await mount(), pending = deferred<SitePhpSettings>(); vi.mocked(updateGlobalPhpSettings).mockReturnValue(pending.promise);
  const first = state.save(), second = state.save(); pending.resolve(php); await Promise.all([first, second]);
  expect(updateGlobalPhpSettings).toHaveBeenCalledTimes(1);
});
it("refresh retries a failed remote access read and enables actions only after success", async () => {
  vi.mocked(getRemoteDbAccess).mockRejectedValueOnce(new Error("unavailable"));
  const state = await mount(); expect(state.remoteLoaded).toBe(false);
  await state.refreshAll(); expect(state.remoteLoaded).toBe(true); expect(state.remoteError).toBe("");
  await state.setRemote(false); expect(setRemoteDbAccess).toHaveBeenCalledWith(false, remote.allowedIp);
});
it("applies the server's saved settings and preserves an unlimited execution time", async () => {
  const state = await mount(); vi.mocked(updateGlobalPhpSettings).mockResolvedValueOnce({ ...php, memoryLimit: "512M", maxExecutionTime: 0 });
  await state.save(); expect(state.form.memoryLimit).toBe("512M"); expect(state.form.maxExecutionTime).toBe(0);
});
