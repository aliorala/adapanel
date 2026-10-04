import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import { AxiosError, type AxiosResponse, type InternalAxiosRequestConfig } from "axios";
import AccountView from "./AccountView.vue";
import { apiClient, invalidateSessionRequests } from "../api/client";
import { useAuthStore, type CurrentUser, type TwoFactorSetup } from "../stores/auth";
import { copyToClipboard } from "../utils/clipboard";

const shared = vi.hoisted(() => ({ confirm: vi.fn() }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: shared.confirm }) }));
vi.mock("../utils/clipboard", () => ({ copyToClipboard: vi.fn(async () => true) }));
type State = {
  accountConfirmed: boolean; accountError: string; busy: boolean; loadingAccount: boolean; setup: TwoFactorSetup | null;
  recoveryCodes: string[]; twoFactorVerificationCode: string; disableTwoFactorPassword: string;
  currentPassword: string; newPassword: string; confirmPassword: string;
  startTwoFactor(): Promise<void>; confirmTwoFactor(): Promise<void>; regenerateRecoveryCodes(): Promise<void>;
  disableTwoFactor(): Promise<void>; changePassword(): Promise<void>; refreshAccount(): Promise<void>;
  copyRecoveryCodes(): Promise<void>; cancelSetup(): void;
};
const user = (enabled = false): CurrentUser => ({ id: "actor", userName: "fixture", email: "fixture@example.test", role: "Customer", preferredLanguage: "en", isTwoFactorEnabled: enabled, isActive: true, lastLoginAtUtc: null });
const setup = (): TwoFactorSetup => ({ isTwoFactorEnabled: false, hasPendingSetup: true, manualEntryKey: "JBSWY3DPEHPK3PXP", provisioningUri: "otpauth://totp/AdaPanel:fixture?secret=JBSWY3DPEHPK3PXP&issuer=AdaPanel" });
const session = (enabled = false) => ({ isAuthenticated: true, requiresSetupWizard: false, user: user(enabled) });
function response(config: InternalAxiosRequestConfig, data: unknown): AxiosResponse { return { config, data: { success: true, data }, headers: {}, status: 200, statusText: "OK" }; }
function deferred<T>() { let resolve!: (value: T) => void, reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
const originalAdapter = apiClient.defaults.adapter;
let app: App | undefined, calls: InternalAxiosRequestConfig[], notices: { text: string; error: boolean }[];
let read: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>, write: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>, enabled = false;
async function flush() { for (let i = 0; i < 40; i++) await nextTick(); }
function unmount() { app?.unmount(); app = undefined; }
async function mount(twoFactor = false) {
  enabled = twoFactor;
  const pinia = createPinia(), auth = useAuthStore(pinia); auth.session = session(enabled);
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...AccountView, render: () => h("section") } as Component, { onNotify: (text: string, error: boolean) => notices.push({ text, error }) }).use(pinia);
  app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return { state, auth };
}
const mutations = () => calls.filter(config => config.method !== "get");
beforeEach(() => {
  calls = []; notices = []; enabled = false; invalidateSessionRequests(); shared.confirm.mockResolvedValue(true);
  vi.stubGlobal("window", { location: { pathname: "/panel" } });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture-token" } }), { status: 200 })));
  read = async config => response(config, session(enabled));
  write = async config => {
    if (config.url === "/auth/2fa/begin") return response(config, setup());
    if (config.url === "/auth/2fa/confirm") { enabled = true; return response(config, { user: user(true), recoveryCodes: ["fixture-code-one", "fixture-code-two"] }); }
    if (config.url === "/auth/2fa/recovery-codes") return response(config, { codes: ["new-code-one", "new-code-two"] });
    if (config.url === "/auth/2fa/disable") { enabled = false; return response(config, user(false)); }
    return response(config, { revokedApiTokens: 2 });
  };
  apiClient.defaults.adapter = async config => { calls.push(config); return config.method === "get" ? read(config) : write(config); };
});
afterEach(() => { unmount(); apiClient.defaults.adapter = originalAdapter; invalidateSessionRequests(); vi.restoreAllMocks(); vi.resetAllMocks(); vi.unstubAllGlobals(); });

it("reserves setup while the first Begin click is pending", async () => {
  const { state } = await mount(), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = config => (request = config, pending.promise);
  const first = state.startTwoFactor(); await flush(); const second = state.startTwoFactor(); await flush();
  expect(mutations()).toHaveLength(1); pending.resolve(response(request, setup())); await Promise.all([first, second]);
});
it("reserves recovery regeneration across confirmation and stops a duplicate click", async () => {
  const { state } = await mount(true), consent = deferred<boolean>(); shared.confirm.mockReturnValue(consent.promise);
  const first = state.regenerateRecoveryCodes(); await flush(); const second = state.regenerateRecoveryCodes(); await flush();
  expect(shared.confirm).toHaveBeenCalledOnce(); expect(mutations()).toHaveLength(0);
  consent.resolve(true); await Promise.all([first, second]); expect(mutations()).toHaveLength(1);
});
it("does not change the password while recovery regeneration is pending", async () => {
  const { state } = await mount(true), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = config => (request = config, pending.promise); const rotating = state.regenerateRecoveryCodes(); await flush();
  state.currentPassword = "OldPassword!123"; state.newPassword = state.confirmPassword = "NewPassword!123";
  const changing = state.changePassword(); await flush(); expect(mutations()).toHaveLength(1);
  pending.resolve(response(request, { codes: ["new-code-one"] })); await Promise.all([rotating, changing]);
});
it("preserves a new password draft typed while the submitted change finishes", async () => {
  const { state } = await mount(), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = config => (request = config, pending.promise);
  state.currentPassword = "OldPassword!123"; state.newPassword = state.confirmPassword = "FirstPassword!123";
  const changing = state.changePassword(); await flush(); state.currentPassword = "FirstPassword!123"; state.newPassword = state.confirmPassword = "LaterPassword!123";
  pending.resolve(response(request, { revokedApiTokens: 0 })); await changing;
  expect(state.newPassword).toBe("LaterPassword!123"); expect(state.currentPassword).toBe("FirstPassword!123");
  expect(JSON.parse(String(request.data))).toEqual({ currentPassword: "OldPassword!123", newPassword: "FirstPassword!123" });
});
for (const change of ["unmount", "session", "actor"]) it(`does not disable 2FA after confirmation outlives ${change}`, async () => {
  const { state, auth } = await mount(true), consent = deferred<boolean>(); shared.confirm.mockReturnValueOnce(consent.promise);
  state.disableTwoFactorPassword = "FixturePassword!123"; const disabling = state.disableTwoFactor(); await flush();
  if (change === "unmount") unmount(); else if (change === "session") invalidateSessionRequests(); else auth.session!.user = { ...user(true), id: "new-actor" };
  consent.resolve(true); await disabling; expect(mutations()).toHaveLength(0);
});
it("does not publish a setup secret returned after closing the account screen", async () => {
  const { state, auth } = await mount(), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = config => (request = config, pending.promise); const begin = state.startTwoFactor(); await flush(); unmount();
  pending.resolve(response(request, setup())); await begin; expect(auth.twoFactorSetup).toBeNull();
});
it("clears only its own published setup when the account screen closes", async () => {
  const { state, auth } = await mount(); await state.startTwoFactor(); unmount(); expect(auth.twoFactorSetup).toBeNull();
});
it("requires a fresh account measurement after an initial read failure", async () => {
  read = async () => { throw new Error("fixture unavailable"); }; const { state } = await mount();
  expect(state.accountConfirmed).toBe(false); expect(state.accountError).not.toBe(""); await state.startTwoFactor(); expect(mutations()).toHaveLength(0);
  read = async config => response(config, session()); await state.refreshAccount(); await state.startTwoFactor(); expect(mutations()).toHaveLength(1);
});
it("does not trust an account refresh for another user", async () => {
  read = async config => response(config, { ...session(), user: { ...user(), id: "different" } }); const { state, auth } = await mount();
  expect(state.accountConfirmed).toBe(false); expect(auth.session!.user!.id).toBe("actor"); await state.startTwoFactor(); expect(mutations()).toHaveLength(0);
});
it.each(["false-envelope", "missing-secret", "already-enabled"])("rejects unconfirmed setup: %s", async kind => {
  const { state, auth } = await mount(); write = async config => { const result = response(config, { ...setup(), ...(kind === "missing-secret" ? { manualEntryKey: "" } : kind === "already-enabled" ? { isTwoFactorEnabled: true } : {}) }); if (kind === "false-envelope") result.data.success = false; return result; };
  await state.startTwoFactor(); expect(auth.twoFactorSetup).toBeNull(); expect(notices[notices.length - 1]?.error).toBe(true);
});
it.each(["wrong-user", "not-enabled", "empty-codes", "missing-codes"])("does not confirm an invalid 2FA outcome: %s", async kind => {
  const { state, auth } = await mount(); await state.startTwoFactor(); state.twoFactorVerificationCode = "123456";
  write = async config => response(config, { user: { ...user(kind !== "not-enabled"), ...(kind === "wrong-user" ? { id: "different" } : {}) }, recoveryCodes: kind === "missing-codes" ? undefined : kind === "empty-codes" ? [] : ["fixture-code"] });
  await state.confirmTwoFactor(); expect(notices[notices.length - 1]?.error).toBe(true); expect(state.recoveryCodes).toEqual([]); expect(auth.session!.user!.id).toBe("actor");
});
it.each([{ codes: [] }, { codes: [""] }, { codes: ["duplicate", "duplicate"] }])("does not report regenerated codes when the result is invalid (%j)", async ({ codes }) => {
  const { state } = await mount(true); write = async config => response(config, { codes }); await state.regenerateRecoveryCodes();
  expect(state.recoveryCodes).toEqual([]); expect(notices[notices.length - 1]?.error).toBe(true);
});
it("does not report disabled when the server still measures enabled", async () => {
  const { state } = await mount(true); state.disableTwoFactorPassword = "FixturePassword!123"; write = async config => response(config, user(true));
  await state.disableTwoFactor(); expect(notices[notices.length - 1]?.error).toBe(true);
});
it.each([{}, { revokedApiTokens: -1 }, { revokedApiTokens: "2" }])("rejects an unmeasured password change result (%j)", async result => {
  const { state } = await mount(); state.currentPassword = "OldPassword!123"; state.newPassword = state.confirmPassword = "NewPassword!123";
  write = async config => response(config, result); await state.changePassword(); expect(notices[notices.length - 1]?.error).toBe(true); expect(state.newPassword).toBe("NewPassword!123");
});
it("keeps the complete measured setup/confirm/regenerate/disable path usable", async () => {
  const { state, auth } = await mount(); await state.startTwoFactor(); expect(state.setup?.manualEntryKey).toBe(setup().manualEntryKey);
  state.twoFactorVerificationCode = "123456"; await state.confirmTwoFactor(); expect(auth.session!.user!.isTwoFactorEnabled).toBe(true); expect(state.recoveryCodes).toHaveLength(2);
  await state.regenerateRecoveryCodes(); expect(state.recoveryCodes).toEqual(["new-code-one", "new-code-two"]);
  state.disableTwoFactorPassword = "FixturePassword!123"; await state.disableTwoFactor(); expect(auth.session!.user!.isTwoFactorEnabled).toBe(false); expect(state.recoveryCodes).toEqual([]); expect(notices[notices.length - 1]?.error).toBe(false);
});
it("clears an unchanged password draft after a measured successful change", async () => {
  const { state } = await mount(); state.currentPassword = "OldPassword!123"; state.newPassword = state.confirmPassword = "NewPassword!123";
  await state.changePassword(); expect(state.currentPassword).toBe(""); expect(state.newPassword).toBe(""); expect(notices[notices.length - 1]?.error).toBe(false);
});
it("cancels setup locally without issuing a server mutation", async () => {
  const { state, auth } = await mount(); await state.startTwoFactor(); state.cancelSetup(); expect(state.setup).toBeNull(); expect(auth.twoFactorSetup).toBeNull(); expect(mutations()).toHaveLength(1);
});
it("does not mutate when regeneration confirmation is declined", async () => {
  const { state } = await mount(true); shared.confirm.mockResolvedValueOnce(false); await state.regenerateRecoveryCodes(); expect(mutations()).toHaveLength(0);
});
it("keeps the pending setup after an invalid code and accepts a corrected retry", async () => {
  const { state } = await mount(); await state.startTwoFactor(); state.twoFactorVerificationCode = "000000";
  const normalWrite = write; write = async config => { throw new AxiosError("rejected", "ERR_BAD_REQUEST", config, null, { ...response(config, null), status: 400, data: { success: false, message: "auth.invalid_two_factor_code" } }); };
  await state.confirmTwoFactor(); expect(state.accountConfirmed).toBe(true); expect(state.setup?.manualEntryKey).toBe(setup().manualEntryKey); expect(state.twoFactorVerificationCode).toBe("000000");
  state.twoFactorVerificationCode = "123456"; write = normalWrite; await state.confirmTwoFactor(); expect(state.recoveryCodes).toHaveLength(2);
});
it("preserves a password draft after wrong current password so it can be corrected", async () => {
  const { state } = await mount(); state.currentPassword = "WrongPassword!123"; state.newPassword = state.confirmPassword = "NewPassword!123";
  const normalWrite = write; write = async config => { throw new AxiosError("rejected", "ERR_BAD_REQUEST", config, null, { ...response(config, null), status: 400, data: { success: false, message: "auth.invalid_credentials" } }); };
  await state.changePassword(); expect(state.accountConfirmed).toBe(true); expect(state.newPassword).toBe("NewPassword!123");
  state.currentPassword = "CorrectPassword!123"; write = normalWrite; await state.changePassword(); expect(state.newPassword).toBe("");
});
it("clears obsolete recovery codes and requires a new reading after an ambiguous regeneration failure", async () => {
  const { state } = await mount(true); await state.regenerateRecoveryCodes(); expect(state.recoveryCodes).toHaveLength(2);
  write = async () => { throw new Error("lost response"); }; await state.regenerateRecoveryCodes();
  expect(state.accountConfirmed).toBe(false); expect(state.recoveryCodes).toEqual([]); await state.copyRecoveryCodes(); expect(copyToClipboard).not.toHaveBeenCalled();
});
it("does not clear a newer setup owned by a different caller when this view closes", async () => {
  const { state, auth } = await mount(); await state.startTwoFactor(); const replacement = { ...setup(), manualEntryKey: "DIFFERENT" }; auth.twoFactorSetup = replacement;
  unmount(); expect(auth.twoFactorSetup).toEqual(replacement);
});
it("does not announce a copied recovery list after the user hides it", async () => {
  const { state } = await mount(true); await state.regenerateRecoveryCodes(); notices = [];
  const pending = deferred<boolean>(); vi.mocked(copyToClipboard).mockReturnValueOnce(pending.promise);
  const copying = state.copyRecoveryCodes(); state.recoveryCodes = []; pending.resolve(true); await copying; expect(notices).toEqual([]);
});
it("does not overwrite another actor with an old account action result", async () => {
  const { auth } = await mount(true), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = config => (request = config, pending.promise); const disabling = auth.disableTwoFactor("FixturePassword!123").catch(error => error); await flush();
  auth.session!.user = { ...user(true), id: "different" }; pending.resolve(response(request, user(false))); await disabling;
  expect(auth.session!.user!.id).toBe("different"); expect(auth.session!.user!.isTwoFactorEnabled).toBe(true);
});
it("keeps a new account operation reserved after an old reset operation finishes", async () => {
  const { auth } = await mount(), first = deferred<AxiosResponse>(), second = deferred<AxiosResponse>(); const requests: InternalAxiosRequestConfig[] = [];
  write = config => { requests.push(config); return requests.length === 1 ? first.promise : second.promise; };
  const obsolete = auth.beginTwoFactorSetup().catch(error => error); await flush(); auth.$reset(); auth.session = session();
  const latest = auth.beginTwoFactorSetup(); await flush(); first.resolve(response(requests[0]!, setup())); await obsolete;
  await expect(auth.beginTwoFactorSetup()).rejects.toThrow(); expect(mutations()).toHaveLength(2); expect(auth.twoFactorSetup).toBeNull();
  second.resolve(response(requests[1]!, setup())); await latest; expect(auth.twoFactorSetup?.manualEntryKey).toBe(setup().manualEntryKey);
});
it("does not reread account status while a mutation is pending", async () => {
  const { state } = await mount(), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = config => (request = config, pending.promise); const beginning = state.startTwoFactor(); await flush(); const reads = calls.filter(config => config.method === "get").length;
  await state.refreshAccount(); expect(calls.filter(config => config.method === "get")).toHaveLength(reads); pending.resolve(response(request, setup())); await beginning;
});
