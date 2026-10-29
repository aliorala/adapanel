import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import BillingView from "./BillingView.vue";
import { getBillingSettings, getBillingUsage, updateBillingSettings, type BillingSettings } from "../api/billing";
import { useAuthStore } from "../stores/auth";
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../api/billing", () => ({ getBillingSettings: vi.fn(), getBillingUsage: vi.fn(), updateBillingSettings: vi.fn() }));
type State = { settings: BillingSettings | null; enabled: boolean; notice: string; noticeError: boolean; refresh(): Promise<void>; save(): Promise<void> };
const settings = (enabled: boolean): BillingSettings => ({ enabled, lastCallAtUtc: null, lastAccountCount: 0, recentCalls: [] });
function deferred<T>() { let resolve!: (value: T) => void, reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
let app: App | undefined;
async function flush() { for (let i = 0; i < 12; i++) await nextTick(); }
function session(id = "admin", role = "Administrator") { return { isAuthenticated: true, requiresSetupWizard: false, user: { id, role, userName: id, email: "fixture@example.test", preferredLanguage: "en", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } }; }
async function mount(role = "Administrator") {
 const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
 const pinia = createPinia(), auth = useAuthStore(pinia); auth.session = session("admin", role);
 app = renderer.createApp({ ...BillingView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
 const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return { state, auth };
}
beforeEach(() => { vi.mocked(getBillingSettings).mockResolvedValue(settings(false)); vi.mocked(getBillingUsage).mockResolvedValue({ generatedAtUtc: "fixture", accounts: [], unmeasuredTrafficCount: 0 }); vi.mocked(updateBillingSettings).mockImplementation(async enabled => settings(enabled)); });
afterEach(() => { app?.unmount(); app = undefined; vi.resetAllMocks(); });
it.each(["success", "failure"])("ignores an older refresh's %s after saving", async outcome => {
 const { state } = await mount(); const pending = deferred<BillingSettings>(); vi.mocked(getBillingSettings).mockReturnValueOnce(pending.promise);
 const reading = state.refresh(); state.enabled = true; await state.save();
 if (outcome === "success") pending.resolve(settings(false)); else pending.reject(new Error("old read")); await reading;
 expect(state.settings?.enabled).toBe(true); expect(state.enabled).toBe(true); expect(state.noticeError).toBe(false);
});
it("ignores another administrator's pending read", async () => {
 const pending = deferred<BillingSettings>(); vi.mocked(getBillingSettings).mockReturnValueOnce(pending.promise);
 const { state, auth } = await mount(); auth.session = session("new-admin"); await flush(); pending.resolve(settings(true)); await flush(); expect(state.settings?.enabled).toBe(false);
});
it.each(["logout", "unmount"])("ignores a pending save after %s", async action => {
 const { state, auth } = await mount(); const pending = deferred<BillingSettings>(); vi.mocked(updateBillingSettings).mockReturnValueOnce(pending.promise); state.enabled = true; const saving = state.save();
 if (action === "logout") auth.session = null; else { app!.unmount(); app = undefined; }
 pending.resolve(settings(true)); await saving; expect(state.settings?.enabled).not.toBe(true); expect(state.notice).toBe("");
});
it("reports a committed setting followed by usage failure honestly", async () => {
 const { state } = await mount(); vi.mocked(getBillingUsage).mockRejectedValueOnce(new Error("usage failed")); state.enabled = true; await state.save(); expect(state.settings?.enabled).toBe(true); expect(state.notice).toContain("billing.settings_saved"); expect(state.noticeError).toBe(true);
});
it("prevents duplicate writes and refreshes while saving", async () => {
 const { state } = await mount(); const pending = deferred<BillingSettings>(); vi.mocked(updateBillingSettings).mockReturnValueOnce(pending.promise); state.enabled = true;
 const saving = state.save(); await state.save(); await state.refresh(); expect(updateBillingSettings).toHaveBeenCalledTimes(1); expect(getBillingSettings).toHaveBeenCalledTimes(1); pending.resolve(settings(true)); await saving;
});
it("does not send billing requests for a customer", async () => {
 const { state } = await mount("Customer"); await state.save(); await state.refresh(); expect(getBillingSettings).not.toHaveBeenCalled(); expect(updateBillingSettings).not.toHaveBeenCalled();
});
