import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import ClusterView from "./ClusterView.vue";
import * as api from "../api/cluster";
import { useAuthStore } from "../stores/auth";

// G18: https düğüm onaylı parmak izi olmadan kaydedilemez; yeniden onay yöneticinin gördüğü
// parmak iziyle yapılır ve vazgeçilirse hiçbir şey gönderilmez.
const mocks = vi.hoisted(() => ({ confirm: vi.fn() }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: mocks.confirm }) }));
vi.mock("../api/cluster", () => ({
  getClusterOverview: vi.fn(), registerClusterNode: vi.fn(), updateClusterNode: vi.fn(), removeClusterNode: vi.fn(),
  probeClusterNode: vi.fn(), getClusterNodeLink: vi.fn(), createClusterSite: vi.fn(), getClusterPlacement: vi.fn(),
  probeClusterCertificate: vi.fn(), approveClusterCertificate: vi.fn()
}));

type State = {
  notice: string; noticeError: boolean; form: api.SaveClusterNodePayload; editingId: string;
  certProbe: api.ClusterCertificateProbe | null;
  save(): Promise<void>; readCertificate(): Promise<void>; approveProbed(): void;
  reapprove(row: api.ClusterNodeStatus): Promise<void>; edit(row: api.ClusterNodeStatus): void;
};

const FP = "SHA256:AA:BB:CC";
const node = (state: api.ClusterNode["certificateState"] = "pinned", fingerprint = FP): api.ClusterNode => ({
  id: "node-a", label: "node-a", address: "https://node-a.test", enabled: true, certificateFingerprint: state === "pinned" ? fingerprint : "",
  certificateState: state, certificateApprovedAtUtc: null, hasToken: true, createdAtUtc: "fixture", lastSeenAtUtc: null, lastError: ""
});
const row = (state: api.ClusterNode["certificateState"] = "pinned"): api.ClusterNodeStatus =>
  ({ node: node(state), reachable: true, statusKey: "", detail: "", durationMs: 1, info: null });
const probe = (fingerprint = FP): api.ClusterCertificateProbe =>
  ({ address: "https://node-a.test", https: true, fingerprint, subject: "CN=x", issuer: "CN=x", notBeforeUtc: null, notAfterUtc: null, chainTrusted: false });

let app: App | undefined;
async function flush() { for (let i = 0; i < 12; i++) await nextTick(); }
async function mount() {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(), auth = useAuthStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "admin", role: "Administrator", userName: "admin", email: "a@example.test", preferredLanguage: "en", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } };
  app = renderer.createApp({ ...ClusterView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return state;
}
function fill(state: State, address = "https://fixture.test") {
  Object.assign(state.form, { label: "fixture", address, apiToken: "fixture", enabled: true });
}

beforeEach(() => {
  vi.mocked(api.getClusterOverview).mockResolvedValue({ nodes: [row()], totalSites: 0, reachableCount: 1, unreachableCount: 0, generatedAtUtc: "fixture" });
  vi.mocked(api.getClusterPlacement).mockResolvedValue({ nodeId: null, nodeLabel: "", reasonKey: "", candidateCount: 0 });
  vi.mocked(api.registerClusterNode).mockResolvedValue(node());
  vi.mocked(api.updateClusterNode).mockResolvedValue(node());
  vi.mocked(api.probeClusterCertificate).mockResolvedValue(probe());
  vi.mocked(api.approveClusterCertificate).mockResolvedValue(node());
  mocks.confirm.mockResolvedValue(true);
});
afterEach(() => { app?.unmount(); app = undefined; vi.resetAllMocks(); });

it("refuses to register an https node that has no approved fingerprint", async () => {
  const state = await mount(); fill(state);
  await state.save();
  expect(api.registerClusterNode).not.toHaveBeenCalled();
  expect(state.noticeError).toBe(true); expect(state.notice).toBe("cluster.certificate_approval_required");
});

it("sends the fingerprint the administrator approved after reading the certificate", async () => {
  const state = await mount(); fill(state);
  await state.readCertificate();
  expect(api.probeClusterCertificate).toHaveBeenCalledWith("https://fixture.test");
  expect(state.certProbe?.fingerprint).toBe(FP);
  // Okumak onaylamak DEĞİLDİR: onay verilmeden alan boş kalır.
  expect(state.form.certificateFingerprint).toBe("");
  state.approveProbed();
  await state.save();
  expect(api.registerClusterNode).toHaveBeenCalledWith(expect.objectContaining({ certificateFingerprint: FP, address: "https://fixture.test" }));
});

it("keeps the existing approval when the address is unchanged but demands a new one when it changes", async () => {
  const state = await mount();
  state.edit(row()); await flush();
  await state.save();
  expect(api.updateClusterNode).toHaveBeenCalledWith("node-a", expect.objectContaining({ certificateFingerprint: "" }));

  vi.mocked(api.updateClusterNode).mockClear();
  state.edit(row()); await flush();
  state.form.address = "https://moved.test"; await flush();
  await state.save();
  expect(api.updateClusterNode).not.toHaveBeenCalled();
  expect(state.notice).toBe("cluster.certificate_approval_required");
});

it("drops a read certificate and a given approval as soon as the address changes", async () => {
  const state = await mount(); fill(state);
  await state.readCertificate(); state.approveProbed();
  expect(state.form.certificateFingerprint).toBe(FP);
  state.form.address = "https://other.test"; await flush();
  expect(state.form.certificateFingerprint).toBe(""); expect(state.certProbe).toBeNull();
});

it("re-approves with the fingerprint the administrator was shown, and only after confirming", async () => {
  const state = await mount();
  await state.reapprove(row("reapproval_required"));
  expect(mocks.confirm).toHaveBeenCalledTimes(1);
  expect(api.approveClusterCertificate).toHaveBeenCalledWith("node-a", FP);
});

it("does not approve anything when the administrator declines the re-approval", async () => {
  const state = await mount(); mocks.confirm.mockResolvedValueOnce(false);
  await state.reapprove(row("reapproval_required"));
  expect(api.approveClusterCertificate).not.toHaveBeenCalled();
});
