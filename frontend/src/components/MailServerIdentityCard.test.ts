import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import MailServerIdentityCard from "./MailServerIdentityCard.vue";
import { getMailServerIdentity, setMailServerIdentity, type MailServerIdentity } from "../api/mailIdentity";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../api/mailIdentity", () => ({ getMailServerIdentity: vi.fn(), setMailServerIdentity: vi.fn() }));
const snapshot: MailServerIdentity = { supported: true, configuredHostName: "mail.example.test", currentHostName: "mail.example.test",
  domain: "example.test", contactEmail: "certificates@example.test", pinned: true, applied: true, steps: [], ptrRequest: "" };
type State = { load(): Promise<void>; save(): Promise<void>; form: { domain: string; hostName: string; contactEmail: string };
  identity: MailServerIdentity | null; busy: boolean; loading: boolean; notice: string; noticeError: boolean; verified: boolean };
let app: App | undefined;
async function flush() { for (let i = 0; i < 12; i++) await nextTick(); }
async function mount(isAdmin = true) {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}),
    insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...MailServerIdentityCard, render: () => h("section") } as Component,
    { isAdmin, domains: [{ name: "example.test" }, { name: "other.test" }] });
  app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState;
  await flush();
  return state;
}
function draft(state: State) { state.form.domain = "other.test"; state.form.hostName = "mx.other.test"; state.form.contactEmail = "admin@other.test"; }
beforeEach(() => { vi.clearAllMocks(); vi.mocked(getMailServerIdentity).mockResolvedValue(snapshot); vi.mocked(setMailServerIdentity).mockResolvedValue(snapshot); });
afterEach(() => { app?.unmount(); app = undefined; });

it("never fetches or saves when mounted outside administrator context", async () => {
  const state = await mount(false); draft(state); await state.load(); await state.save();
  expect(getMailServerIdentity).not.toHaveBeenCalled(); expect(setMailServerIdentity).not.toHaveBeenCalled();
});
it("loads the saved name and refreshes measured state without overwriting an edited form", async () => {
  const state = await mount(); expect(state.form.hostName).toBe(snapshot.configuredHostName);
  expect(state.form.contactEmail).toBe(snapshot.contactEmail);
  draft(state); vi.mocked(getMailServerIdentity).mockResolvedValueOnce({ ...snapshot, currentHostName: "old.example.test", applied: false });
  await state.load();
  expect(state.form).toEqual({ domain: "other.test", hostName: "mx.other.test", contactEmail: "admin@other.test" });
  expect(state.identity?.currentHostName).toBe("old.example.test"); expect(state.verified).toBe(false);
});
it("does not derive a hostname when the selected domain changes", async () => {
  const state = await mount(); state.form.domain = "other.test"; await nextTick(); expect(state.form.hostName).toBe(snapshot.configuredHostName);
});
it("does not claim success for an unverified result, even when a previous name remains pinned", async () => {
  const state = await mount(); draft(state);
  vi.mocked(setMailServerIdentity).mockResolvedValueOnce({ ...snapshot, applied: false,
    steps: [{ key: "tls", status: "failed", message: "Certificate check failed" }] });
  await state.save();
  expect(state.verified).toBe(false); expect(state.notice).toBe("panel.mail.identityFailed"); expect(state.noticeError).toBe(true);
  expect(state.identity?.steps[0]?.status).toBe("failed"); expect(state.form.hostName).toBe("mx.other.test");
});
it("requires both verified application and a persistent pin before claiming success", async () => {
  const state = await mount(); draft(state);
  vi.mocked(setMailServerIdentity).mockResolvedValueOnce({ ...snapshot, pinned: false }); await state.save();
  expect(state.noticeError).toBe(true); expect(state.notice).toBe("panel.mail.identityFailed");
});
it("blocks duplicate submit and refresh while the operation is running", async () => {
  let finish!: (value: MailServerIdentity) => void;
  vi.mocked(setMailServerIdentity).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
  const state = await mount(); draft(state); const operation = state.save();
  expect(state.busy).toBe(true); await state.save(); await state.load();
  expect(setMailServerIdentity).toHaveBeenCalledTimes(1); expect(getMailServerIdentity).toHaveBeenCalledTimes(1);
  finish(snapshot); await operation; expect(state.busy).toBe(false); expect(state.noticeError).toBe(false);
});
it.each(["ECONNABORTED", "ETIMEDOUT", "ERR_NETWORK"])("checks status after %s without repeating POST or claiming success", async code => {
  const state = await mount(); draft(state); vi.mocked(setMailServerIdentity).mockRejectedValueOnce({ code });
  await state.save();
  expect(getMailServerIdentity).toHaveBeenCalledTimes(2); expect(setMailServerIdentity).toHaveBeenCalledTimes(1);
  expect(state.notice).toBe("panel.mail.identityUncertain"); expect(state.noticeError).toBe(true); expect(state.busy).toBe(false);
});
it("keeps failed loads retryable and does not enable a save before status is known", async () => {
  vi.mocked(getMailServerIdentity).mockRejectedValueOnce(new Error("offline")); const state = await mount(); draft(state); await state.save();
  expect(state.identity).toBeNull(); expect(setMailServerIdentity).not.toHaveBeenCalled(); await state.load(); expect(state.identity).toEqual(snapshot);
});
it("does not claim success on a rejected save and permits a retry", async () => {
  const state = await mount(); draft(state); vi.mocked(setMailServerIdentity).mockRejectedValueOnce(new Error("rejected"));
  await state.save(); expect(state.noticeError).toBe(true); expect(state.busy).toBe(false);
  await state.save(); expect(setMailServerIdentity).toHaveBeenCalledTimes(2); expect(state.notice).toBe("panel.mail.identitySaved");
});

it("labels known steps and safely falls back for new server statuses", async () => {
  const state = await mount() as State & { stepLabel(key: string): string; statusLabel(status: string): string };
  expect(state.stepLabel("dns-preparation")).toBe("panel.mail.identityStepDns");
  expect(state.statusLabel("action-required")).toBe("panel.mail.identityStatusAction");
  expect(state.stepLabel("future-step")).toBe("panel.mail.identityStepUnknown");
  expect(state.statusLabel("future-status")).toBe("panel.mail.identityStatusUnknown");
});
