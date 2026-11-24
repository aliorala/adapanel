import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import SiteHtaccessView from "./SiteHtaccessView.vue";
import { getHtaccessReport, getServerHtaccessMode, setServerHtaccessMode, type HtaccessMode, type HtaccessServerSwitchState, type HtaccessSiteReport } from "../api/htaccess";
import { useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../api/htaccess", async original => ({ ...await original<object>(), getHtaccessReport: vi.fn(), getServerHtaccessMode: vi.fn(), setServerHtaccessMode: vi.fn() }));
type State = { serverSwitch: HtaccessServerSwitchState | null; serverBusy: boolean; serverNotice: string;
  serverNoticeError: boolean; saveServerMode(mode: HtaccessMode): Promise<void> };
const value = (mode: HtaccessMode): HtaccessServerSwitchState => ({ mode, switchFileExists: true, switchFilePath: "fixture-switch" });
function deferred<T>() { let resolve!: (value: T) => void, reject!: (error: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
let app: App | undefined;
async function flush() { for (let i = 0; i < 12; i++) await nextTick(); }
function session(id = "admin", role = "Administrator") { return { isAuthenticated: true, requiresSetupWizard: false,
  user: { id, role, userName: id, email: "fixture@example.test", preferredLanguage: "en", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } }; }
async function mount(role = "Administrator") {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia); const auth = useAuthStore(pinia); auth.session = session("actor", role);
  useSitesStore(pinia).selectedSiteName = "a.test";
  app = renderer.createApp({ ...SiteHtaccessView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return { state, auth };
}
beforeEach(() => {
  vi.mocked(getHtaccessReport).mockResolvedValue({ siteName: "a.test", mode: "off", documentRoot: "C:/sites/a.test", inspection: null } as unknown as HtaccessSiteReport);
  vi.mocked(getServerHtaccessMode).mockResolvedValue(value("off"));
  vi.mocked(setServerHtaccessMode).mockImplementation(async mode => value(mode));
});
afterEach(() => { app?.unmount(); app = undefined; vi.resetAllMocks(); });

it.each(["success", "failure"])("does not let an older server read's %s overwrite a confirmed save", async outcome => {
  const pending = deferred<HtaccessServerSwitchState>(); vi.mocked(getServerHtaccessMode).mockReturnValueOnce(pending.promise);
  const { state } = await mount(); await state.saveServerMode("enforce");
  expect(state.serverSwitch?.mode).toBe("enforce");
  if (outcome === "success") pending.resolve(value("off")); else pending.reject(new Error("old read failed"));
  await flush(); expect(state.serverSwitch?.mode).toBe("enforce"); expect(state.serverNoticeError).toBe(false);
});
it("does not carry another administrator's pending read into the new session", async () => {
  const pending = deferred<HtaccessServerSwitchState>(); vi.mocked(getServerHtaccessMode).mockReturnValueOnce(pending.promise).mockResolvedValueOnce(value("observe"));
  const { state, auth } = await mount(); auth.session = session("new-admin"); await flush();
  expect(state.serverSwitch?.mode).toBe("observe"); pending.resolve(value("off")); await flush(); expect(state.serverSwitch?.mode).toBe("observe");
});
it("does not publish an earlier administrator's save after logout", async () => {
  const pending = deferred<HtaccessServerSwitchState>(); vi.mocked(setServerHtaccessMode).mockReturnValueOnce(pending.promise);
  const { state, auth } = await mount(); const saving = state.saveServerMode("enforce"); auth.session = null; await flush();
  pending.resolve(value("enforce")); await saving;
  expect(state.serverSwitch).toBeNull(); expect(state.serverNotice).toBe(""); expect(state.serverBusy).toBe(false);
});
it("ignores a pending read after the view closes", async () => {
  const pending = deferred<HtaccessServerSwitchState>(); vi.mocked(getServerHtaccessMode).mockReturnValueOnce(pending.promise);
  const { state } = await mount(); app!.unmount(); app = undefined; pending.resolve(value("enforce")); await flush(); expect(state.serverSwitch).toBeNull();
});
it("does not send a server-wide write for a non-administrator", async () => {
  const { state } = await mount("Customer"); await state.saveServerMode("enforce"); expect(setServerHtaccessMode).not.toHaveBeenCalled(); expect(getServerHtaccessMode).not.toHaveBeenCalled();
});
