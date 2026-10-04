import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ref, ssrContextKey, type App, type Component, type ComponentPublicInstance } from "vue";
import FtpView from "./FtpView.vue";
import { createFtpUser, deleteFtpUser, ensureFtpServer, getFtpStatus, getFtpTls, getFtpUsers, setFtpPassword, setFtpTls, type FtpServerStatus, type FtpUser } from "../api/ftp";
import { invalidateSessionRequests } from "../api/client";
import { useSitesStore } from "../stores/sites";
import { useAuthStore, type CurrentUser } from "../stores/auth";
import type { ConfirmOptions } from "../components/ConfirmDialog.vue";

const { prompt, confirm } = vi.hoisted(() => ({ prompt: vi.fn(), confirm: vi.fn() }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../components/PromptDialog.vue", () => ({ usePrompt: () => ({ prompt }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm }) }));
vi.mock("../api/ftp", () => ({ createFtpUser: vi.fn(), deleteFtpUser: vi.fn(), ensureFtpServer: vi.fn(), getFtpStatus: vi.fn(), getFtpTls: vi.fn(), getFtpUsers: vi.fn(), setFtpPassword: vi.fn(), setFtpTls: vi.fn() }));
function user(site = "a.test"): FtpUser { return { userName: `${site}_ftp`, homePath: `C:/sites/${site}`, siteName: site, existsOnSystem: true, isEnabled: true, createdAtUtc: "2026-10-02T10:00:00Z" }; }
const status: FtpServerStatus = { serverAddress: "fixture.test", controlPort: 21, passivePortStart: 50000, passivePortEnd: 51000, isConfigured: true, statusMessage: "ftp.server_ready", serviceState: "Running", siteState: "Started", isSiteRunning: true, hasControlPortConflict: false, controlPortOwner: "" };
let app: App | undefined;
let site = ref<string | undefined>("a.test");
beforeEach(() => {
  site = ref("a.test"); getFtpUsersMock(); vi.mocked(ensureFtpServer).mockResolvedValue(status); vi.mocked(getFtpStatus).mockResolvedValue(status);
  vi.mocked(getFtpTls).mockResolvedValue({ requireSsl: false }); prompt.mockResolvedValue("FixturePassword123!");
  confirm.mockImplementation(async (options: ConfirmOptions) => { await options.action?.(); return true; });
});
function getFtpUsersMock() { vi.mocked(getFtpUsers).mockResolvedValue([user(), user("b.test")]); }
afterEach(() => { app?.unmount(); app = undefined; vi.restoreAllMocks(); vi.resetAllMocks(); });
function deferred<T>() { let resolve!: (value: T) => void, reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
type State = {
  users: FtpUser[]; usersLoaded: boolean; notice: string; noticeError: boolean; creating: boolean; changingPassword: string; deletingUser: string;
  requireSsl: boolean; tlsLoaded: boolean; tlsSaving: boolean; showCreateForm: boolean;
  serverStatus: FtpServerStatus | null;
  serverReady: boolean; serverStateLabel: string; usersLoad: { message: { value: string } }; statusLoad: { message: { value: string } };
  formatCreatedAt(value: string): string;
  form: { userName: string; password: string; siteName: string; homePath: string };
  refresh(): Promise<void>; refreshAll(): Promise<void>; createUser(): Promise<void>; changeFtpPassword(userName: string): Promise<void>;
  removeUser(userName: string): Promise<void>; toggleTls(value: boolean): Promise<void>; loadTls(): Promise<void>; toggleCreateForm(): void;
};
async function flush() { for (let i = 0; i < 14; i++) await nextTick(); }
async function mount(role = "Administrator", fetchSites?: Promise<never[]>) {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia); useAuthStore().session = { isAuthenticated: true, requiresSetupWizard: false, user: { role } as CurrentUser };
  vi.spyOn(useSitesStore(), "fetchSites").mockReturnValue(fetchSites ?? Promise.resolve([]));
  let vm: ComponentPublicInstance | undefined;
  const component = { ...FtpView, render: () => h("section") } as Component;
  app = renderer.createApp({ render: () => h(component, { siteName: site.value, ref: (value: unknown) => { if (value) vm = value as ComponentPublicInstance; } }) }).use(pinia);
  app.provide(ssrContextKey, {}); app.mount({}); await flush();
  return (vm!.$ as unknown as { setupState: State }).setupState;
}
async function selectSite(name?: string) { site.value = name; await flush(); }
function fill(state: State) { state.showCreateForm = true; Object.assign(state.form, { userName: "new_ftp", password: "FixturePassword123!", homePath: "C:/sites/a.test" }); }

it("drops a password answer when the site changes before the prompt resolves", async () => {
  const state = await mount(), pending = deferred<string | null>(); prompt.mockReturnValueOnce(pending.promise);
  const changing = state.changeFtpPassword("a.test_ftp"); await selectSite("b.test"); pending.resolve("FixturePassword123!"); await changing;
  expect(setFtpPassword).not.toHaveBeenCalled(); expect(state.notice).toBe("");
});
it("does not delete an old site's account after its confirmation is accepted", async () => {
  const state = await mount(); confirm.mockImplementationOnce(async (options: ConfirmOptions) => { await selectSite("b.test"); await options.action?.(); return true; });
  await state.removeUser("a.test_ftp"); expect(deleteFtpUser).not.toHaveBeenCalled();
});
it("does not change a password after the screen closes", async () => {
  const state = await mount(), pending = deferred<string | null>(); prompt.mockReturnValueOnce(pending.promise);
  const changing = state.changeFtpPassword("a.test_ftp"); app!.unmount(); app = undefined; pending.resolve("FixturePassword123!"); await changing;
  expect(setFtpPassword).not.toHaveBeenCalled();
});
it("does not continue a queued delete after authentication changes", async () => {
  const state = await mount(); confirm.mockImplementationOnce(async (options: ConfirmOptions) => { invalidateSessionRequests(); await options.action?.(); return true; });
  await state.removeUser("a.test_ftp"); expect(deleteFtpUser).not.toHaveBeenCalled();
});
it("clears credentials, home path and user-name drafts when the site scope changes", async () => {
  const state = await mount(); fill(state); await selectSite("b.test");
  expect(state.form).toEqual({ siteName: "b.test", userName: "", password: "", homePath: "" }); expect(state.showCreateForm).toBe(false);
});
it("clears the old site when switching to the global FTP screen", async () => {
  const state = await mount(); await selectSite(undefined); expect(state.form.siteName).toBe(""); expect(state.tlsLoaded).toBe(true);
});
it("keeps the latest list when an earlier refresh arrives last", async () => {
  const state = await mount(), first = deferred<FtpUser[]>(), second = deferred<FtpUser[]>(); vi.mocked(getFtpUsers).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
  const a = state.refresh(), b = state.refresh(); second.resolve([user("b.test")]); await b; first.resolve([user()]); await a;
  expect(state.users.map(item => item.siteName)).toEqual(["b.test"]);
});
it("does not start FTP setup after the screen closes while awaiting the sites list", async () => {
  const pending = deferred<never[]>(); await mount("Administrator", pending.promise); app!.unmount(); app = undefined; pending.resolve([]); await flush();
  expect(ensureFtpServer).not.toHaveBeenCalled(); expect(getFtpUsers).not.toHaveBeenCalled();
});
it("does not attempt admin-only FTP setup for a reseller", async () => {
  await mount("Reseller"); expect(ensureFtpServer).not.toHaveBeenCalled(); expect(getFtpUsers).toHaveBeenCalledOnce();
});
it("rejects duplicate account creation and preserves a later site's form", async () => {
  const state = await mount(), pending = deferred<FtpUser>(); fill(state); vi.mocked(createFtpUser).mockReturnValueOnce(pending.promise);
  const creating = state.createUser(); await state.createUser(); expect(createFtpUser).toHaveBeenCalledOnce();
  await selectSite("b.test"); fill(state); state.form.userName = "b_new"; pending.resolve(user()); await creating;
  expect(state.form.userName).toBe("b_new"); expect(state.form.password).toBe("FixturePassword123!"); expect(state.showCreateForm).toBe(true); expect(state.notice).toBe("");
});
it("does not clear a new global-site draft after an old account creation finishes", async () => {
  site.value = undefined; const state = await mount(), pending = deferred<FtpUser>(); state.form.siteName = "a.test"; fill(state);
  vi.mocked(createFtpUser).mockReturnValueOnce(pending.promise); const creating = state.createUser(); state.form.siteName = "b.test"; state.form.userName = "b_new";
  pending.resolve(user()); await creating; expect(state.form.userName).toBe("b_new"); expect(state.showCreateForm).toBe(true);
});
it("does not offer duplicate password prompts or overlap account writes", async () => {
  const state = await mount(), pending = deferred<string | null>(); prompt.mockReturnValueOnce(pending.promise);
  const changing = state.changeFtpPassword("a.test_ftp"); await state.changeFtpPassword("a.test_ftp"); await state.removeUser("a.test_ftp");
  expect(prompt).toHaveBeenCalledOnce(); expect(confirm).not.toHaveBeenCalled(); pending.resolve(null); await changing;
});
it("keeps the confirmed creation and the failed list refresh separate", async () => {
  const state = await mount(); fill(state); vi.mocked(createFtpUser).mockResolvedValueOnce(user()); vi.mocked(getFtpUsers).mockRejectedValueOnce(new Error("refresh failed"));
  await state.createUser(); expect(state.notice).toContain("panel.ftp.created"); expect(state.usersLoad.message.value).toBe("panel.ftp.listLoadFailed");
});
it("keeps a TLS write from being overwritten by an older TLS read", async () => {
  site.value = undefined; const state = await mount(), pending = deferred<{ requireSsl: boolean }>(); vi.mocked(getFtpTls).mockReturnValueOnce(pending.promise);
  const reading = state.loadTls(); vi.mocked(setFtpTls).mockResolvedValueOnce({ requireSsl: true, verified: true }); await state.toggleTls(true);
  pending.resolve({ requireSsl: false }); await reading; expect(state.requireSsl).toBe(true);
});
it("rejects duplicate TLS writes", async () => {
  site.value = undefined; const state = await mount(), pending = deferred<{ requireSsl: boolean }>(); vi.mocked(setFtpTls).mockReturnValueOnce(pending.promise);
  const first = state.toggleTls(true); await state.toggleTls(false); expect(setFtpTls).toHaveBeenCalledOnce(); pending.resolve({ requireSsl: true }); await first;
});
it("preserves successful account creation, password change and deletion", async () => {
  const state = await mount(); fill(state); vi.mocked(createFtpUser).mockResolvedValueOnce(user()); await state.createUser();
  expect(createFtpUser).toHaveBeenCalledWith({ userName: "new_ftp", password: "FixturePassword123!", siteName: "a.test", homePath: "C:/sites/a.test" });
  expect(state.form.password).toBe(""); expect(state.notice).toBe("panel.ftp.created");
  await state.changeFtpPassword("a.test_ftp"); expect(setFtpPassword).toHaveBeenCalledWith("a.test_ftp", "FixturePassword123!"); expect(state.notice).toBe("panel.ftp.passwordChanged");
  await state.removeUser("a.test_ftp"); expect(deleteFtpUser).toHaveBeenCalledWith("a.test_ftp"); expect(state.notice).toBe("panel.ftp.deleted");
});
it("does not refresh or publish an old account deletion after the site changes", async () => {
  const state = await mount(), pending = deferred<void>(); vi.mocked(deleteFtpUser).mockReturnValueOnce(pending.promise);
  const deleting = state.removeUser("a.test_ftp"); await selectSite("b.test"); const reads = vi.mocked(getFtpUsers).mock.calls.length;
  pending.resolve(); await deleting; expect(getFtpUsers).toHaveBeenCalledTimes(reads); expect(state.notice).toBe("");
});
it("preserves a reopened creation form when its previous request finishes", async () => {
  const state = await mount(), pending = deferred<FtpUser>(); fill(state); vi.mocked(createFtpUser).mockReturnValueOnce(pending.promise);
  const creating = state.createUser(); state.toggleCreateForm(); state.toggleCreateForm(); state.form.userName = "later_draft";
  pending.resolve(user()); await creating; expect(state.form.userName).toBe("later_draft"); expect(state.showCreateForm).toBe(true); expect(state.notice).toBe("");
});
it("does not apply an old user list after authentication changes", async () => {
  const state = await mount(), pending = deferred<FtpUser[]>(); vi.mocked(getFtpUsers).mockReturnValueOnce(pending.promise);
  const reading = state.refresh(); invalidateSessionRequests(); pending.resolve([user("private-old.test")]); await reading;
  expect(state.users.some(item => item.siteName === "private-old.test")).toBe(false);
});
it("does not let an old setup result overwrite a newer server measurement", async () => {
  const pending = deferred<FtpServerStatus>(); vi.mocked(ensureFtpServer).mockReturnValueOnce(pending.promise); const state = await mount();
  const latest = { ...status, isConfigured: false, statusMessage: "ftp.control_port_conflict" }; vi.mocked(getFtpStatus).mockResolvedValueOnce(latest);
  await state.refreshAll(); pending.resolve(status); await flush(); expect(state.serverStatus?.statusMessage).toBe("ftp.control_port_conflict");
});
it("reports a failed status measurement even when the user-list refresh succeeds", async () => {
  const state = await mount(); vi.mocked(getFtpStatus).mockRejectedValueOnce(new Error("status failed")); await state.refreshAll();
  expect(state.statusLoad.message.value).toBe("panel.ftp.statusLoadFailed"); expect(state.serverReady).toBe(false);
  expect(state.serverStateLabel).toBe("panel.ftp.serverUnmeasured");
});

it("does not call an unmeasured server ready because a saved configuration flag is true", async () => {
  vi.mocked(ensureFtpServer).mockResolvedValueOnce({ ...status, statusMessage: "ftp.server_status_unknown" });
  const state = await mount(); expect(state.serverReady).toBe(false); expect(state.serverStateLabel).toBe("panel.ftp.serverUnmeasured");
});
it("marks a retained list stale and clears that error after a successful retry", async () => {
  const state = await mount(); vi.mocked(getFtpUsers).mockRejectedValueOnce(new Error("list failed")); await state.refresh();
  expect(state.users).toHaveLength(2); expect(state.usersLoad.message.value).toBe("panel.ftp.listLoadFailed");
  await state.refresh(); expect(state.usersLoad.message.value).toBe("");
});
it("keeps a confirmed deletion visible when reloading the list fails", async () => {
  const state = await mount(); vi.mocked(getFtpUsers).mockRejectedValueOnce(new Error("list failed")); await state.removeUser("a.test_ftp");
  expect(state.notice).toContain("panel.ftp.deleted"); expect(state.usersLoad.message.value).toBe("panel.ftp.listLoadFailed");
  expect(state.users.some(item => item.userName === "a.test_ftp")).toBe(false);
});
it("renders a malformed legacy creation date without throwing", async () => {
  const state = await mount(); expect(() => state.formatCreatedAt("legacy-date")).not.toThrow();
  expect(state.formatCreatedAt("legacy-date")).toBe("legacy-date");
});
it("releases account-operation locks when password or deletion is cancelled", async () => {
  const state = await mount(); prompt.mockResolvedValueOnce(null); await state.changeFtpPassword("a.test_ftp");
  confirm.mockResolvedValueOnce(false); await state.removeUser("a.test_ftp");
  expect(setFtpPassword).not.toHaveBeenCalled(); expect(deleteFtpUser).not.toHaveBeenCalled(); expect(state.changingPassword).toBe(""); expect(state.deletingUser).toBe("");
});
it("pins account creation to the site scope and clears home paths when the global target changes", async () => {
  const state = await mount(); fill(state); state.form.siteName = "b.test"; vi.mocked(createFtpUser).mockResolvedValueOnce(user()); await state.createUser();
  expect(createFtpUser).toHaveBeenCalledWith(expect.objectContaining({ siteName: "a.test", homePath: "" }));
  await selectSite(undefined); state.form.siteName = "a.test"; state.form.homePath = "C:/sites/a.test"; state.form.siteName = "b.test";
  expect(state.form.homePath).toBe("");
});
it("does not use a password answer from an earlier visit to the same site", async () => {
  const state = await mount(), pending = deferred<string | null>(); prompt.mockReturnValueOnce(pending.promise);
  const changing = state.changeFtpPassword("a.test_ftp"); await selectSite("b.test"); await selectSite("a.test");
  pending.resolve("FixturePassword123!"); await changing; expect(setFtpPassword).not.toHaveBeenCalled();
});
it("does not apply an old TLS write to a site-scoped FTP screen", async () => {
  site.value = undefined; const state = await mount(), pending = deferred<{ requireSsl: boolean; verified: boolean }>(); vi.mocked(setFtpTls).mockReturnValueOnce(pending.promise);
  const saving = state.toggleTls(true); await selectSite("b.test"); pending.resolve({ requireSsl: true, verified: true }); await saving;
  expect(state.tlsLoaded).toBe(false); expect(state.requireSsl).toBe(false); expect(state.notice).toBe("");
});
it("does not dispatch a row action for an account outside the visible site", async () => {
  const state = await mount(); await state.changeFtpPassword("b.test_ftp"); await state.removeUser("b.test_ftp");
  expect(prompt).not.toHaveBeenCalled(); expect(confirm).not.toHaveBeenCalled();
});
it.each([
  [false, "ftp.user_created_password_expires"],
  [null, "ftp.user_created_expiry_unverified"]
])("shows the account-created expiry warning instead of plain success (%s)", async (passwordNeverExpires, message) => {
  const state = await mount(); fill(state); vi.mocked(createFtpUser).mockResolvedValueOnce({ ...user(), passwordNeverExpires, message });
  await state.createUser(); expect(state.notice).toBe(message); expect(state.noticeError).toBe(true);
  expect(state.form.password).toBe(""); expect(state.showCreateForm).toBe(false);
});
it.each([
  [false, "ftp.user_created_password_expires"],
  [null, "ftp.user_created_expiry_unverified"]
])("retains the measured expiry warning if the creation message is missing (%s)", async (passwordNeverExpires, message) => {
  const state = await mount(); fill(state); vi.mocked(createFtpUser).mockResolvedValueOnce({ ...user(), passwordNeverExpires });
  await state.createUser(); expect(state.notice).toBe(message); expect(state.noticeError).toBe(true);
});
it("shows normal success only when the returned expiry status is verified safe", async () => {
  const state = await mount(); fill(state); vi.mocked(createFtpUser).mockResolvedValueOnce({ ...user(), passwordNeverExpires: true, message: "ftp.user_created" });
  await state.createUser(); expect(state.notice).toBe("panel.ftp.created"); expect(state.noticeError).toBe(false);
});
it("keeps the creation warning visible when the subsequent user-list refresh fails", async () => {
  const state = await mount(); fill(state); vi.mocked(createFtpUser).mockResolvedValueOnce({ ...user(), passwordNeverExpires: false, message: "ftp.user_created_password_expires" });
  vi.mocked(getFtpUsers).mockRejectedValueOnce(new Error("refresh failed")); await state.createUser();
  expect(state.notice).toContain("ftp.user_created_password_expires"); expect(state.notice).toContain("panel.ftp.listLoadFailed"); expect(state.noticeError).toBe(true);
});
