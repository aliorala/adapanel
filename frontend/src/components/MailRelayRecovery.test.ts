import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import MailRelayCard from "./MailRelayCard.vue";
import { getMailRelay, setMailRelay, type MailRelay } from "../api/mail";

const dialog = vi.hoisted(() => ({ confirm: vi.fn() }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("./ConfirmDialog.vue", () => ({ useConfirm: () => dialog }));
vi.mock("../api/mail", () => ({ getMailRelay: vi.fn(), setMailRelay: vi.fn() }));

const pending: MailRelay = { installed: true, configured: false, enabled: false, host: "", port: 587,
  protocol: "smtp", implicitTls: false, allowInvalidCerts: false, username: "", hasPassword: false,
  securityStatus: "recovery-pending" };
const restored: MailRelay = { ...pending, configured: true, enabled: true, host: "relay.example.test",
  username: "existing-user", hasPassword: true, securityStatus: "verified-settings" };
type State = { recover(): Promise<void>; relay: MailRelay | null; busy: boolean; notice: string;
  noticeError: boolean; form: { enabled: boolean; host: string; password: string } };
let app: App | undefined;
async function mount() {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}),
    createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {},
    parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...MailRelayCard, render: () => h("section") } as Component, { provider: "mailenable" });
  app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState;
  for (let i = 0; i < 8; i++) await nextTick();
  return state;
}
beforeEach(() => { vi.clearAllMocks(); vi.mocked(getMailRelay).mockResolvedValue(pending);
  vi.mocked(setMailRelay).mockResolvedValue(restored); });
afterEach(() => { app?.unmount(); app = undefined; });

it("waits for explicit confirmation before requesting recovery", async () => {
  const state = await mount(); await state.recover();
  expect(dialog.confirm).toHaveBeenCalledWith(expect.objectContaining({
    title: "panel.mail.relayRecover", message: "panel.mail.relayRecoverConfirm", action: expect.any(Function)
  }));
  expect(setMailRelay).not.toHaveBeenCalled();
});

it("uses restore-only and retains an enabled relay instead of disabling it", async () => {
  const state = await mount(); await state.recover();
  await dialog.confirm.mock.calls[0][0].action();
  expect(setMailRelay).toHaveBeenCalledTimes(1);
  expect(setMailRelay).toHaveBeenCalledWith(expect.objectContaining({ recoverOnly: true, password: "" }));
  expect(state.relay).toEqual(restored);
  expect(state.form.enabled).toBe(true); expect(state.form.host).toBe(restored.host);
  expect(state.form.password).toBe(""); expect(state.notice).toBe("mail.relay_recovered");
  expect(state.busy).toBe(false);
});

it("does not claim recovery succeeded when the server rejects it", async () => {
  vi.mocked(setMailRelay).mockRejectedValueOnce(new Error("recovery failed"));
  const state = await mount(); await state.recover();
  await dialog.confirm.mock.calls[0][0].action();
  expect(state.relay?.securityStatus).toBe("recovery-pending");
  expect(state.notice).not.toBe("mail.relay_recovered");
  expect(state.noticeError).toBe(true); expect(state.busy).toBe(false);
});
