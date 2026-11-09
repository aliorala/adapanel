import { afterEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { createRenderer, h, nextTick, ref, ssrContextKey, type App, type Component } from "vue";
import MailView from "./MailView.vue";
import { createMailAccount, deleteMailDomain, deleteMailAccount, deleteMailAlias, deleteMailList,
  getAutoResponder, setAutoResponder, setMailAccountPassword, setMailAccountQuota,
  getMailClientSettings, getMailWorkspace, type MailWorkspace } from "../api/mail";
import { useSitesStore } from "../stores/sites";
import { useAuthStore } from "../stores/auth";
import ConfirmDialog, { closeConfirmDialog, dialogState } from "../components/ConfirmDialog.vue";
import PromptDialog, { closePromptDialog, promptState } from "../components/PromptDialog.vue";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key, locale: ref("en") }) }));
vi.mock("../api/mail", async original => ({
  ...await original<typeof import("../api/mail")>(), createMailAccount: vi.fn(), deleteMailDomain: vi.fn(),
  deleteMailAccount: vi.fn(), deleteMailAlias: vi.fn(), deleteMailList: vi.fn(),
  getAutoResponder: vi.fn(), setAutoResponder: vi.fn(), setMailAccountPassword: vi.fn(), setMailAccountQuota: vi.fn(),
  getMailDns: vi.fn(async () => null),
  getMailWorkspace: vi.fn(), getMailPublicBaseUrl: vi.fn(async () => ""),
  getMailClientSettings: vi.fn(async () => null),
  getOutboundLimit: vi.fn(async () => ({ domain: "a.test", messagesPerHour: 0, configured: false })),
}));
const workspace = (domain = "a.test"): MailWorkspace => ({ installed: true, configured: false,
  selectedDomain: domain, domains: [], accounts: [], aliases: [], lists: [] });
function deferred<T>() {
  let resolve!: (value: T) => void, reject!: (error: unknown) => void;
  const promise = new Promise<T>((done, fail) => { resolve = done; reject = fail; });
  return { promise, resolve, reject };
}
type State = { selectedDomain: string; workspace: MailWorkspace; notice: string; busy: boolean; loading: boolean;
  domainLoading: boolean; domainWorkspaceReady: boolean;
  clientSettings: unknown; loadClientSettings(): Promise<void>;
  autoResponderFor: string; autoResponderForm: { enabled: boolean; subject: string; message: string };
  openAutoResponder(address: string): Promise<void>; saveAutoResponder(): Promise<void>;
  changePassword(address: string): Promise<void>; changeAccountQuota(address: string, quota: number): Promise<void>;
  removeAccount(address: string): Promise<void>; removeAlias(address: string): Promise<void>; removeList(address: string): Promise<void>;
  removeDomain(): Promise<void>; openDomain(domain: string): Promise<void>;
  accountForm: { localPart: string; password: string }; addAccount(): Promise<void>; refresh(): Promise<void>; backToDomains(): void };
let app: App | undefined;
let dialogApp: App | undefined;
afterEach(() => { closeConfirmDialog(); closePromptDialog(); app?.unmount(); dialogApp?.unmount(); app = undefined; dialogApp = undefined; vi.clearAllMocks(); vi.unstubAllGlobals(); });
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
  expect(state.accountForm.localPart).toBe(change === "draft" ? "new-draft" : change === "back" || change === "domain" ? "" : "one");
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

function populatedWorkspace(domain = "a.test"): MailWorkspace {
  return { ...workspace(domain), configured: true,
    domains: ["a.test", "b.test"].map(name => ({ name, accountCount: 1, maxSizeMB: 0, allocatedMB: 0, active: true })),
    accounts: [{ address: `one@${domain}`, maxSizeMB: 1024, currentSizeMB: 0, active: true }],
    aliases: [{ address: `alias@${domain}`, target: `one@${domain}`, active: true }],
    lists: [{ address: `list@${domain}`, recipients: [], description: "", postingPolicy: "anyone", allowedSenders: [], aliases: [] }]
  } as MailWorkspace;
}

async function mountRealPrompt() {
  vi.stubGlobal("document", { activeElement: null, body: { style: { overflow: "" } } });
  vi.stubGlobal("HTMLElement", class {});
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}),
    insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  dialogApp = renderer.createApp({ ...PromptDialog, render: () => h("section") } as Component);
  dialogApp.provide(ssrContextKey, {});
  return (dialogApp.mount({}).$ as unknown as { setupState: { value: string; accept(): void } }).setupState;
}

it("clears old domain rows and forms immediately and stays closed when the next workspace fails", async () => {
  const state = await mount(); state.workspace = populatedWorkspace();
  state.autoResponderFor = "one@a.test"; state.autoResponderForm.message = "old domain message";
  const response = deferred<MailWorkspace>(); vi.mocked(getMailWorkspace).mockReturnValueOnce(response.promise);
  const pending = state.openDomain("b.test");
  expect(state.workspace.accounts).toEqual([]); expect(state.workspace.aliases).toEqual([]); expect(state.workspace.lists).toEqual([]);
  expect(state.domainLoading).toBe(true);
  expect(state.autoResponderFor).toBe(""); expect(state.accountForm.password).toBe("");
  // A rejected request must not revive the previous domain's writable rows.
  response.reject(new Error("workspace unavailable"));
  await pending;
  expect(state.selectedDomain).toBe("b.test"); expect(state.domainWorkspaceReady).toBe(false);
  expect(state.domainLoading).toBe(false); expect(state.busy).toBe(false);
  await state.changePassword("one@a.test"); await state.changeAccountQuota("one@a.test", 10);
  await state.removeAccount("one@a.test"); await state.saveAutoResponder();
  expect(promptState.open).toBe(false); expect(dialogState.open).toBe(false);
  expect(setMailAccountPassword).not.toHaveBeenCalled(); expect(setMailAccountQuota).not.toHaveBeenCalled();
  expect(deleteMailAccount).not.toHaveBeenCalled(); expect(setAutoResponder).not.toHaveBeenCalled();
});

it.each(["password", "quota"])("rejects a captured mailbox %s prompt after domain navigation and return", async kind => {
  const state = await mount(), dialog = await mountRealPrompt(); state.workspace = populatedWorkspace();
  const pending = kind === "password" ? state.changePassword("one@a.test") : state.changeAccountQuota("one@a.test", 10);
  await nextTick(); expect(promptState.open).toBe(true);
  state.backToDomains(); vi.mocked(getMailWorkspace).mockResolvedValueOnce(populatedWorkspace()); await state.openDomain("a.test");
  dialog.value = kind === "password" ? "password-new" : "50"; dialog.accept(); await pending;
  expect(setMailAccountPassword).not.toHaveBeenCalled(); expect(setMailAccountQuota).not.toHaveBeenCalled();
});

it.each(["account", "alias", "list"])("rejects a captured %s deletion confirmation after navigation and return", async kind => {
  const state = await mount(), dialog = await mountRealDialog(); state.workspace = populatedWorkspace();
  const pending = kind === "account" ? state.removeAccount("one@a.test") : kind === "alias" ? state.removeAlias("alias@a.test") : state.removeList("list@a.test");
  await nextTick(); expect(dialogState.open).toBe(true);
  state.backToDomains(); vi.mocked(getMailWorkspace).mockResolvedValueOnce(populatedWorkspace()); await state.openDomain("a.test");
  await dialog.accept(); await pending;
  expect(deleteMailAccount).not.toHaveBeenCalled(); expect(deleteMailAlias).not.toHaveBeenCalled(); expect(deleteMailList).not.toHaveBeenCalled();
});

it.each(["password", "quota"])("preserves a valid same-domain mailbox %s prompt", async kind => {
  const state = await mount(), dialog = await mountRealPrompt(); state.workspace = populatedWorkspace();
  vi.mocked(setMailAccountPassword).mockResolvedValueOnce(populatedWorkspace()); vi.mocked(setMailAccountQuota).mockResolvedValueOnce(populatedWorkspace());
  const pending = kind === "password" ? state.changePassword("one@a.test") : state.changeAccountQuota("one@a.test", 10);
  await nextTick(); dialog.value = kind === "password" ? "password-new" : "50"; dialog.accept(); await pending;
  if (kind === "password") expect(setMailAccountPassword).toHaveBeenCalledExactlyOnceWith("one@a.test", "password-new");
  else expect(setMailAccountQuota).toHaveBeenCalledExactlyOnceWith("one@a.test", 50);
});

it("rejects mailbox actions for an address absent from the current workspace", async () => {
  const state = await mount(); state.workspace = populatedWorkspace();
  await state.changePassword("missing@a.test"); await state.removeAccount("missing@a.test");
  expect(promptState.open).toBe(false); expect(dialogState.open).toBe(false);
});

it("does not revive an old autoresponder read after opening another domain", async () => {
  const state = await mount(); state.workspace = populatedWorkspace();
  const result = deferred<Awaited<ReturnType<typeof getAutoResponder>>>(); vi.mocked(getAutoResponder).mockReturnValueOnce(result.promise);
  const pending = state.openAutoResponder("one@a.test");
  vi.mocked(getMailWorkspace).mockResolvedValueOnce(populatedWorkspace("b.test")); await state.openDomain("b.test");
  result.resolve({ address: "one@a.test", enabled: true, subject: "old", message: "old message" }); await pending;
  expect(state.autoResponderFor).toBe(""); expect(state.autoResponderForm.message).toBe("");
  await state.saveAutoResponder(); expect(setAutoResponder).not.toHaveBeenCalled();
});

it("keeps the current domain busy while a previous domain load finishes", async () => {
  const state = await mount(); state.workspace = populatedWorkspace();
  const old = deferred<MailWorkspace>(), current = deferred<MailWorkspace>();
  vi.mocked(getMailWorkspace).mockReturnValueOnce(old.promise).mockReturnValueOnce(current.promise);
  const first = state.openDomain("a.test"), second = state.openDomain("b.test");
  old.resolve(populatedWorkspace()); await first;
  expect(state.busy).toBe(true); expect(state.domainLoading).toBe(true);
  expect(state.domainWorkspaceReady).toBe(false);
  current.resolve(populatedWorkspace("b.test")); await second;
  expect(state.workspace.selectedDomain).toBe("b.test"); expect(state.domainWorkspaceReady).toBe(true);
  expect(state.busy).toBe(false); expect(state.domainLoading).toBe(false);
});

it.each(["account", "alias", "list"])("preserves a valid same-domain %s delete confirmation", async kind => {
  const state = await mount(), dialog = await mountRealDialog(); state.workspace = populatedWorkspace();
  const result = populatedWorkspace(); result.accounts = []; result.aliases = []; result.lists = [];
  vi.mocked(deleteMailAccount).mockResolvedValueOnce(result); vi.mocked(deleteMailAlias).mockResolvedValueOnce(result); vi.mocked(deleteMailList).mockResolvedValueOnce(result);
  const pending = kind === "account" ? state.removeAccount("one@a.test") : kind === "alias" ? state.removeAlias("alias@a.test") : state.removeList("list@a.test");
  await nextTick(); await dialog.accept(); await pending;
  if (kind === "account") expect(deleteMailAccount).toHaveBeenCalledExactlyOnceWith("one@a.test");
  if (kind === "alias") expect(deleteMailAlias).toHaveBeenCalledExactlyOnceWith("alias@a.test");
  if (kind === "list") expect(deleteMailList).toHaveBeenCalledExactlyOnceWith("list@a.test");
});
