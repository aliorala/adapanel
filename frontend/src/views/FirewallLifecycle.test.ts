import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import FirewallView from "./FirewallView.vue";
import { allowPort, blockPort, getFirewallRules, getPortStatus, type FirewallRule, type PortStatus } from "../api/firewall";
import { useAuthStore } from "../stores/auth";
const { confirm } = vi.hoisted(() => ({ confirm: vi.fn() }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm }) }));
vi.mock("../api/firewall", () => ({ allowPort: vi.fn(), blockPort: vi.fn(), getFirewallRules: vi.fn(), getPortStatus: vi.fn() }));
type State = { notice: string; noticeError: boolean; busy: boolean; loading: boolean; loadingPorts: boolean;
  rules: FirewallRule[]; portsLoaded: boolean; form: { port: number; protocol: string; ruleName: string };
  openPort(port: PortStatus): Promise<void>; openAllMissing(): Promise<void>; saveRule(action: "allow" | "block"): Promise<void> };
const port = (number = 1000): PortStatus => ({ port: number, rangeEnd: 0, protocol: "TCP", serviceKey: "fixture", category: "web", required: true,
  listening: true, listenScope: "all", firewallAllowed: false });
const rule = (verified = true): FirewallRule => ({ displayName: "fixture", direction: "Inbound", action: "Allow", protocol: "TCP", localPort: "1000", enabled: true, verified });
function deferred<T>() { let resolve!: (value: T) => void, reject!: (error: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
let app: App | undefined;
async function flush() { for (let i = 0; i < 8; i++) await nextTick(); }
async function mount() {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia); const auth = useAuthStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "actor", role: "Administrator", userName: "actor", email: "a@example.test", preferredLanguage: "en", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } };
  app = renderer.createApp({ ...FirewallView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return { state, auth };
}
beforeEach(() => {
  vi.mocked(getFirewallRules).mockResolvedValue([]); vi.mocked(getPortStatus).mockResolvedValue([port(), port(1001)]);
  vi.mocked(allowPort).mockResolvedValue(rule()); vi.mocked(blockPort).mockResolvedValue(rule()); confirm.mockResolvedValue(true);
});
afterEach(() => { app?.unmount(); app = undefined; vi.resetAllMocks(); });

it.each(["single", "bulk"])("does not call an unverified %s port write successful", async mode => {
  const { state } = await mount(); vi.mocked(allowPort).mockResolvedValue(rule(false));
  if (mode === "single") await state.openPort(port()); else await state.openAllMissing();
  expect(state.notice).toBe("panel.firewall.savedUnverified"); expect(state.noticeError).toBe(true); expect(allowPort).toHaveBeenCalledTimes(1);
});
it.each(["single", "bulk", "form"])("preserves refresh failure after a verified %s write", async mode => {
  const { state } = await mount(); vi.mocked(getFirewallRules).mockRejectedValue(new Error("Readback unavailable"));
  if (mode === "single") await state.openPort(port()); else if (mode === "bulk") await state.openAllMissing(); else await state.saveRule("allow");
  expect(state.noticeError).toBe(true); expect(state.notice).not.toBe("panel.firewall.saved"); expect(state.notice).not.toBe("panel.firewall.ports.opened"); expect(state.busy).toBe(false);
});
it("uses the exact target shown in confirmation even when the form changes", async () => {
  const { state } = await mount(); Object.assign(state.form, { port: 1000, protocol: "TCP", ruleName: "confirmed" });
  const pending = deferred<boolean>(); confirm.mockReturnValueOnce(pending.promise); const saving = state.saveRule("block");
  state.form.port = 1001; state.form.ruleName = "new draft"; pending.resolve(true); await saving;
  expect(blockPort).toHaveBeenCalledExactlyOnceWith(1000, "TCP", "confirmed"); expect(state.form.ruleName).toBe("new draft");
});
it("does not dispatch an old confirmation in another session", async () => {
  const { state, auth } = await mount(); const pending = deferred<boolean>(); confirm.mockReturnValueOnce(pending.promise);
  const saving = state.saveRule("block"); auth.session = null; pending.resolve(true); await saving; expect(blockPort).not.toHaveBeenCalled();
});
it("stops the bulk sequence when its session ends", async () => {
  const { state, auth } = await mount(); const pending = deferred<FirewallRule>(); vi.mocked(allowPort).mockReturnValueOnce(pending.promise);
  const opening = state.openAllMissing(); auth.session = null; pending.resolve(rule()); await opening;
  expect(allowPort).toHaveBeenCalledTimes(1); expect(state.notice).toBe(""); expect(state.busy).toBe(false);
});
it("does not start follow-up reads after unmount", async () => {
  const { state } = await mount(); const pending = deferred<FirewallRule>(); vi.mocked(allowPort).mockReturnValueOnce(pending.promise);
  const opening = state.openPort(port()), count = vi.mocked(getFirewallRules).mock.calls.length;
  app!.unmount(); app = undefined; pending.resolve(rule()); await opening; expect(getFirewallRules).toHaveBeenCalledTimes(count);
});
it("keeps an earlier read from replacing a post-write refresh", async () => {
  const pending = deferred<FirewallRule[]>(); vi.mocked(getFirewallRules).mockReturnValue(pending.promise);
  const { state } = await mount(); vi.mocked(getFirewallRules).mockResolvedValue([rule()]); await state.openPort(port());
  pending.resolve([]); await flush(); expect(state.rules).toHaveLength(1); expect(state.loading).toBe(false);
});
