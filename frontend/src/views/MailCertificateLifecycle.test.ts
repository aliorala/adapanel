import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { createRenderer, h, nextTick, ref, ssrContextKey, type App, type Component, type Ref } from "vue";
import MailView from "./MailView.vue";
import { getMailCertificate, issueMailCertificate, type MailCertificate, type MailWorkspace, type MailListPostingPolicy } from "../api/mail";
import { useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key, locale: ref("en") }) }));
vi.mock("../api/mail", async original => ({
  ...await original<typeof import("../api/mail")>(),
  getMailWorkspace: vi.fn(async () => ({ installed: false, configured: false, selectedDomain: "", domains: [], accounts: [], aliases: [], lists: [] })),
  getMailPublicBaseUrl: vi.fn(async () => ""),
  getMailClientSettings: vi.fn(async () => null),
  getOutboundLimit: vi.fn(async () => ({ domain: "example.test", messagesPerHour: 0, configured: false })),
  getMailCertificate: vi.fn(), issueMailCertificate: vi.fn(),
}));
type State = {
  selectedDomain: string; certificate: MailCertificate | null; certificateLoad: { message: Ref<string> };
  certForm: { hostName: string; contactEmail: string }; notice: string; noticeError: boolean; busy: boolean;
  loadCertificate(): Promise<void>; issueCert(reuse?: boolean): Promise<void>;
  workspace: MailWorkspace | null;
  apply(workspace: MailWorkspace): void;
  policyLabel(policy: MailListPostingPolicy): string;
};
const cert = (active = true, hostName = "mail.example.test"): MailCertificate => ({
  active, hostName, domain: "example.test", source: "windows-store", notAfterUtc: null, suggestedContactEmail: "owner@example.test"
});
function deferred<T>() {
  let resolve!: (value: T) => void, reject!: (error: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
let app: App | undefined;
async function mount() {
  const pinia = createPinia(), auth = useAuthStore(pinia), sites = useSitesStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "actor", userName: "actor", email: "actor@example.test", role: "Administrator", preferredLanguage: "en", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } };
  sites.sites = [{ id: 1, name: "example.test", state: "Started", physicalPath: "", applicationRootPath: "", applicationPoolName: "example.test", bindings: [], autoStart: true }];
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...MailView, render: () => h("section") } as Component).use(pinia);
  app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState;
  for (let i = 0; i < 24; i++) await nextTick();
  state.selectedDomain = "example.test";
  state.certForm.contactEmail = "owner@example.test";
  return { state, auth };
}
beforeEach(() => { vi.clearAllMocks(); vi.mocked(getMailCertificate).mockResolvedValue(cert()); vi.mocked(issueMailCertificate).mockResolvedValue(cert()); });
afterEach(() => { app?.unmount(); app = undefined; });

it("preserves measured lists during a partial mailbox mutation, but never copies them to a different domain", async () => {
  const { state } = await mount();
  const original: MailWorkspace = { provider: "mailenable", installed: true, configured: false, selectedDomain: "example.test", domains: [], accounts: [], aliases: [],
    lists: [{ address: "news@example.test", description: "", recipients: [], aliases: [], postingPolicy: "native-password", allowedSenders: [] }], listsMeasured: true };
  state.apply(original);
  state.apply({ ...original, lists: [], listsMeasured: false });
  expect(state.workspace?.lists).toEqual(original.lists);
  state.apply({ ...original, selectedDomain: "other.test", lists: [], listsMeasured: false });
  expect(state.workspace?.lists).toEqual([]);
});

it.each(["native-members", "native-password", "native-moderated", "native-disabled", "native-unknown"] as const)
("does not present %s as an open or authenticated-subscriber rule", async policy => {
  const { state } = await mount();
  expect(state.policyLabel(policy)).not.toBe("panel.mail.listPolicyAnyone");
  expect(state.policyLabel(policy)).not.toBe("panel.mail.listPolicySubscribers");
});

it.each([true, false])("reuses the saved custom hostname for its own domain (active: %s)", async active => {
  const { state } = await mount();
  vi.mocked(getMailCertificate).mockResolvedValueOnce(cert(active, "smtp.example.test"));
  await state.loadCertificate(); await state.issueCert(true);
  expect(issueMailCertificate).toHaveBeenCalledWith("example.test", "smtp.example.test", "owner@example.test", true);
});
it.each(["other.test", "", "  EXAMPLE.TEST  "])("scopes saved hostname selection to the selected domain: %s", async domain => {
  const { state } = await mount();
  vi.mocked(getMailCertificate).mockResolvedValueOnce({ ...cert(true, "smtp.example.test"), domain });
  await state.loadCertificate();
  expect(state.certForm.hostName).toBe(domain.trim().toLowerCase() === "example.test" ? "smtp.example.test" : "mail.example.test");
});
it("does not overwrite a hostname typed while the saved certificate is loading", async () => {
  const { state } = await mount(), pending = deferred<MailCertificate>();
  vi.mocked(getMailCertificate).mockReturnValueOnce(pending.promise);
  const loading = state.loadCertificate(); state.certForm.hostName = "chosen.example.test";
  pending.resolve(cert(true, "smtp.example.test")); await loading;
  expect(state.certForm.hostName).toBe("chosen.example.test");
});
it("uses the selected domain default when the saved hostname is blank", async () => {
  const { state } = await mount();
  vi.mocked(getMailCertificate).mockResolvedValueOnce(cert(false, "  "));
  await state.loadCertificate(); expect(state.certForm.hostName).toBe("mail.example.test");
});

it.each([false, true])("does not let an old read overwrite issuance (old read rejects: %s)", async rejects => {
  const { state } = await mount(), old = deferred<MailCertificate>();
  vi.mocked(getMailCertificate).mockReturnValueOnce(old.promise);
  const loading = state.loadCertificate();
  await state.issueCert();
  if (rejects) old.reject(new Error("old failure")); else old.resolve(cert(false, "old.example.test"));
  await loading;
  expect(state.certificate?.hostName).toBe("mail.example.test");
  expect(state.certificate?.active).toBe(true);
  expect(state.certificateLoad.message.value).toBe("");
});
it("keeps the latest read when responses arrive out of order", async () => {
  const { state } = await mount(), old = deferred<MailCertificate>();
  vi.mocked(getMailCertificate).mockReturnValueOnce(old.promise);
  const loading = state.loadCertificate();
  await state.loadCertificate(); old.resolve(cert(false)); await loading;
  expect(state.certificate?.active).toBe(true);
});
it("clears an old verified result when issuance fails", async () => {
  const { state } = await mount(); await state.loadCertificate();
  vi.mocked(issueMailCertificate).mockRejectedValueOnce(new Error("reload failed"));
  await state.issueCert();
  expect(state.certificate).toBeNull(); expect(state.noticeError).toBe(true);
});
it("clears an earlier load error after successful issuance", async () => {
  const { state } = await mount(); vi.mocked(getMailCertificate).mockRejectedValueOnce(new Error("offline"));
  await state.loadCertificate(); expect(state.certificateLoad.message.value).not.toBe("");
  await state.issueCert(); expect(state.certificateLoad.message.value).toBe("");
});
it("ignores duplicate issuance and reads while issuance is pending", async () => {
  const { state } = await mount(), pending = deferred<MailCertificate>();
  vi.mocked(issueMailCertificate).mockReturnValue(pending.promise);
  const issuing = state.issueCert(); const duplicate = state.issueCert();
  await state.loadCertificate(); pending.resolve(cert()); await Promise.all([issuing, duplicate]);
  expect(issueMailCertificate).toHaveBeenCalledTimes(1); expect(getMailCertificate).not.toHaveBeenCalled();
  expect(state.busy).toBe(false);
});
it.each(["domain", "roundtrip", "actor", "role", "logout", "unmount"].flatMap(change =>
  ["read", "issue"].flatMap(operation => [false, true].map(rejects => ({ change, operation, rejects })))))
("ignores $operation completion after $change changes (rejects: $rejects)", async ({ change, operation, rejects }) => {
  const { state, auth } = await mount(), pending = deferred<MailCertificate>();
  vi.mocked(operation === "issue" ? issueMailCertificate : getMailCertificate).mockReturnValueOnce(pending.promise);
  const running = operation === "issue" ? state.issueCert() : state.loadCertificate();
  if (change === "domain" || change === "roundtrip") state.selectedDomain = "other.test";
  if (change === "roundtrip") state.selectedDomain = "example.test";
  if (change === "actor") auth.session!.user!.id = "another";
  if (change === "role") auth.session!.user!.role = "Customer";
  if (change === "logout") auth.session!.isAuthenticated = false;
  if (change === "unmount") { app!.unmount(); app = undefined; }
  if (rejects) pending.reject(new Error("obsolete failure")); else pending.resolve(cert());
  await running;
  expect(state.certificate).toBeNull(); expect(state.notice).not.toBe("panel.mail.certIssued");
  expect(state.noticeError).toBe(false); expect(state.certificateLoad.message.value).toBe("");
  expect(state.certForm.hostName).toBe("");
});

it("captures issuance fields and reports an unverified response as a failure", async () => {
  const { state } = await mount(), pending = deferred<MailCertificate>();
  vi.mocked(issueMailCertificate).mockReturnValueOnce(pending.promise);
  state.certForm.hostName = "smtp.example.test";
  const issuing = state.issueCert(true);
  state.certForm.hostName = "edited.example.test"; state.certForm.contactEmail = "edited@example.test";
  pending.resolve(cert(false, "smtp.example.test")); await issuing;
  expect(issueMailCertificate).toHaveBeenCalledWith("example.test", "smtp.example.test", "owner@example.test", true);
  expect(state.noticeError).toBe(true); expect(state.notice).toBe("panel.mail.certUnverified");
  expect(state.certForm.hostName).toBe("edited.example.test");
});
