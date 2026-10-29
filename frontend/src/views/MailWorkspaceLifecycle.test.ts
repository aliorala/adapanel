import { afterEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { createRenderer, h, nextTick, ref, ssrContextKey, type App, type Component } from "vue";
import MailView from "./MailView.vue";
import { createMailAccount, deleteMailDomain, getMailClientSettings, getMailWorkspace, type MailWorkspace } from "../api/mail";
import { useSitesStore } from "../stores/sites";
import { useAuthStore } from "../stores/auth";
import ConfirmDialog, { closeConfirmDialog, dialogState } from "../components/ConfirmDialog.vue";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key, locale: ref("en") }) }));
vi.mock("../api/mail", async original => ({
  ...await original<typeof import("../api/mail")>(), createMailAccount: vi.fn(), deleteMailDomain: vi.fn(),
  getMailDns: vi.fn(async () => null),
  getMailWorkspace: vi.fn(), getMailPublicBaseUrl: vi.fn(async () => ""),
  getMailClientSettings: vi.fn(async () => null),
  getOutboundLimit: vi.fn(async () => ({ domain: "a.test", messagesPerHour: 0, configured: false })),
}));
const workspace = (domain = "a.test"): MailWorkspace => ({ installed: true, configured: false,
  selectedDomain: domain, domains: [], accounts: [], aliases: [], lists: [] });
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done; }); return { promise, resolve }; }
type State = { selectedDomain: string; workspace: MailWorkspace; notice: string; busy: boolean; loading: boolean;
  clientSettings: unknown; loadClientSettings(): Promise<void>;
  removeDomain(): Promise<void>; openDomain(domain: string): Promise<void>;
  accountForm: { localPart: string; password: string }; addAccount(): Promise<void>; refresh(): Promise<void>; backToDomains(): void };
let app: App | undefined;
let dialogApp: App | undefined;
afterEach(() => { closeConfirmDialog(); app?.unmount(); dialogApp?.unmount(); app = undefined; dialogApp = undefined; vi.clearAllMocks(); vi.unstubAllGlobals(); });
async function mount() {
  vi.mocked(getMailWorkspace).mockResolvedValue(workspace());
  const pinia = createPinia(); useSitesStore(pinia).sites = [{ name: "a.test", bindings: [] }] as never;
  useAuthStore(pinia).session = { isAuthenticated: true, requiresSetupWizard: false,
    user: { id: "actor", userName: "actor", role: "Customer", isActive: true } } as never;
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}),
    insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...MailView, render: () => h("section") } as Component).use(pinia);
  app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState;
  for (let i = 0; i < 30; i++) await nextTick();
  state.selectedDomain = "a.test"; state.accountForm.localPart = "one"; state.accountForm.password = "password-1";
  return state;
}
it.each(["back", "domain", "unmount", "draft"])("ignores a late mailbox workspace after %s changes", async change => {
  const state = await mount(), result = deferred<MailWorkspace>();
  vi.mocked(createMailAccount).mockReturnValueOnce(result.promise);
  const pending = state.addAccount();
  if (change === "back") state.backToDomains();
  if (change === "domain") state.selectedDomain = "b.test";
  if (change === "unmount") { app!.unmount(); app = undefined; }
  if (change === "draft") state.accountForm.localPart = "new-draft";
  const previous = state.workspace;
  result.resolve({ ...workspace(), accounts: [{ address: "old-response@a.test", maxSizeMB: 1, currentSizeMB: 0, active: true }] }); await pending;
  expect(state.workspace).toBe(previous);
  expect(state.accountForm.localPart).toBe(change === "draft" ? "new-draft" : "one");
  if (change === "back") expect(state.selectedDomain).toBe("");
  if (change === "domain") expect(state.selectedDomain).toBe("b.test");
});
it("keeps the latest read when refreshes finish in reverse order", async () => {
  const state = await mount(), old = deferred<MailWorkspace>(), latest = deferred<MailWorkspace>();
  vi.mocked(getMailWorkspace).mockReturnValueOnce(old.promise).mockReturnValueOnce(latest.promise);
  const first = state.refresh(), second = state.refresh();
  const fresh = { ...workspace(), accounts: [{ address: "fresh@a.test", maxSizeMB: 1, currentSizeMB: 0, active: true }] };
  latest.resolve(fresh); await second; old.resolve(workspace()); await first;
  expect(state.workspace.accounts[0]?.address).toBe("fresh@a.test");
  expect(state.loading).toBe(false);
});
it("does not publish previous domain client settings after navigation", async () => {
  const state = await mount(), pending = deferred<Awaited<ReturnType<typeof getMailClientSettings>>>();
  vi.mocked(getMailClientSettings).mockReturnValueOnce(pending.promise);
  const reading = state.loadClientSettings();
  state.backToDomains();
  pending.resolve({ domain: "a.test" } as Awaited<ReturnType<typeof getMailClientSettings>>);
  await reading;
  expect(state.clientSettings).toBeNull();
});

async function mountRealDialog() {
  vi.stubGlobal("document", { activeElement: null, body: { style: { overflow: "" } } });
  vi.stubGlobal("HTMLElement", class {});
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}),
    insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  dialogApp = renderer.createApp({ ...ConfirmDialog, render: () => h("section") } as Component);
  dialogApp.provide(ssrContextKey, {});
  return (dialogApp.mount({}).$ as unknown as { setupState: { typed: string; accept(): Promise<void> } }).setupState;
}

it.each(["domain", "session", "unmount"])("rejects an old real domain-delete confirmation after %s changes", async change => {
  const state = await mount(), dialog = await mountRealDialog();
  state.workspace = { ...workspace(), domains: ["a.test", "b.test"].map(name =>
    ({ name, accountCount: 0, maxSizeMB: 0, allocatedMB: 0, active: true })) };
  vi.mocked(deleteMailDomain).mockResolvedValue(workspace(""));
  const pending = state.removeDomain();
  await nextTick();
  expect(dialogState.open).toBe(true);
  expect(dialogState.options?.requireText).toBe("a.test");
  if (change === "domain") {
    vi.mocked(getMailWorkspace).mockResolvedValueOnce(workspace("b.test"));
    await state.openDomain("b.test");
    expect(state.selectedDomain).toBe("b.test");
  }
  if (change === "session") useAuthStore().$reset();
  if (change === "unmount") { app!.unmount(); app = undefined; }
  const before = state.workspace;
  dialog.typed = "a.test";
  await dialog.accept(); await pending;
  expect(deleteMailDomain).not.toHaveBeenCalled();
  expect(state.workspace).toBe(before);
  if (change === "domain") expect(state.selectedDomain).toBe("b.test");
});

it("deletes the captured domain only after the real dialog accepts its required text in the same context", async () => {
  const state = await mount(), dialog = await mountRealDialog();
  vi.mocked(deleteMailDomain).mockResolvedValue(workspace(""));
  const pending = state.removeDomain(); await nextTick();
  dialog.typed = "wrong.test"; await dialog.accept();
  expect(deleteMailDomain).not.toHaveBeenCalled();
  expect(dialogState.open).toBe(true);
  dialog.typed = "a.test"; await dialog.accept(); await pending;
  expect(deleteMailDomain).toHaveBeenCalledExactlyOnceWith("a.test");
  expect(state.selectedDomain).toBe("");
  expect(dialogState.open).toBe(false);
});
