import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import ClusterView from "./ClusterView.vue";
import * as api from "../api/cluster";
import { useAuthStore } from "../stores/auth";
const mocks = vi.hoisted(() => ({ confirm: vi.fn() }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: mocks.confirm }) }));
vi.mock("../api/cluster", () => ({ getClusterOverview: vi.fn(), registerClusterNode: vi.fn(), updateClusterNode: vi.fn(), removeClusterNode: vi.fn(), probeClusterNode: vi.fn(), getClusterNodeLink: vi.fn(), createClusterSite: vi.fn() }));
type State = { overview: api.ClusterOverview | null; notice: string; noticeError: boolean; form: api.SaveClusterNodePayload; editingId: string; siteForm: { name: string; nodeId: string }; refresh(): Promise<unknown>; save(): Promise<void>; remove(row: api.ClusterNodeStatus): Promise<void>; createSite(): Promise<void>; open(row: api.ClusterNodeStatus): Promise<void>; edit(row: api.ClusterNodeStatus): void };
const node = (id = "node-a"): api.ClusterNode => ({ id, label: id, address: `https://${id}.test`, enabled: true, verifyTlsCertificate: true, hasToken: true, createdAtUtc: "fixture", lastSeenAtUtc: null, lastError: "" });
const row = (id = "node-a"): api.ClusterNodeStatus => ({ node: node(id), reachable: true, statusKey: "", detail: "", durationMs: 1, info: null });
const overview = (id = "node-a"): api.ClusterOverview => ({ nodes: [row(id)], totalSites: 1, reachableCount: 1, unreachableCount: 0, generatedAtUtc: "fixture" });
function deferred<T>() { let resolve!: (value: T) => void, reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
let app: App | undefined;
async function flush() { for (let i = 0; i < 12; i++) await nextTick(); }
function session(id = "admin", role = "Administrator") { return { isAuthenticated: true, requiresSetupWizard: false, user: { id, role, userName: id, email: "fixture@example.test", preferredLanguage: "en", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } }; }
async function mount(role = "Administrator") {
 const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
 const pinia = createPinia(), auth = useAuthStore(pinia); auth.session = session("admin", role);
 app = renderer.createApp({ ...ClusterView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
 const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return { state, auth };
}
function prepareSave(state: State) { Object.assign(state.form, { label: "fixture", address: "https://fixture.test", apiToken: "fixture", enabled: true, verifyTlsCertificate: true }); }
beforeEach(() => {
 vi.mocked(api.getClusterOverview).mockResolvedValue(overview()); vi.mocked(api.registerClusterNode).mockResolvedValue(node()); vi.mocked(api.updateClusterNode).mockResolvedValue(node()); vi.mocked(api.removeClusterNode).mockResolvedValue(); mocks.confirm.mockResolvedValue(true);
 vi.mocked(api.createClusterSite).mockResolvedValue({ nodeId: "node-a", nodeLabel: "node-a", siteName: "fixture.test", placementWasAutomatic: true, remoteMessage: "" });
});
afterEach(() => { app?.unmount(); app = undefined; vi.resetAllMocks(); vi.unstubAllGlobals(); });
it.each(["save", "remove", "create"])("retains %s's committed result and subsequent refresh failure", async action => {
 const { state } = await mount(); vi.mocked(api.getClusterOverview).mockRejectedValueOnce(new Error("read failed"));
 if (action === "save") { prepareSave(state); await state.save(); } else if (action === "remove") await state.remove(row()); else { state.siteForm.name = "fixture.test"; await state.createSite(); }
 expect(state.noticeError).toBe(true); expect(state.notice).toContain(action === "save" ? "panel.cluster.saved" : action === "remove" ? "panel.cluster.removed" : "panel.cluster.siteCreated");
 expect(state.notice.split("\n\n").length).toBeGreaterThan(1);
});
it.each(["success", "failure"])("ignores old refresh %s after a committed write and fresh overview", async outcome => {
 const { state } = await mount(); const pending = deferred<api.ClusterOverview>(); vi.mocked(api.getClusterOverview).mockReturnValueOnce(pending.promise).mockResolvedValueOnce(overview("new-node"));
 const reading = state.refresh(); prepareSave(state); await state.save(); if (outcome === "success") pending.resolve(overview("old-node")); else pending.reject(new Error("old failure")); await reading;
 expect(state.overview?.nodes[0]?.node.id).toBe("new-node"); expect(state.noticeError).toBe(false);
});
it.each(["logout", "new-admin", "unmount"])("does not execute a delayed delete confirmation after %s", async action => {
 const { state, auth } = await mount(); const pending = deferred<boolean>(); mocks.confirm.mockReturnValueOnce(pending.promise); const removing = state.remove(row());
 if (action === "logout") auth.session = null; else if (action === "new-admin") auth.session = session("new-admin"); else { app!.unmount(); app = undefined; }
 pending.resolve(true); await removing; expect(api.removeClusterNode).not.toHaveBeenCalled();
});
it("ignores an earlier administrator's read and write replies", async () => {
 const { state, auth } = await mount(); const reading = deferred<api.ClusterOverview>(), writing = deferred<api.ClusterNode>(); vi.mocked(api.getClusterOverview).mockReturnValueOnce(reading.promise); const load = state.refresh();
 vi.mocked(api.registerClusterNode).mockReturnValueOnce(writing.promise); prepareSave(state); const save = state.save(); auth.session = session("new-admin"); await flush(); reading.resolve(overview("old")); writing.resolve(node("old")); await Promise.all([load, save]);
 expect(state.overview?.nodes[0]?.node.id).toBe("node-a"); expect(state.notice).toBe("");
});
it("preserves another node's form selected during save", async () => {
 const { state } = await mount(); const pending = deferred<api.ClusterNode>(); vi.mocked(api.registerClusterNode).mockReturnValueOnce(pending.promise); prepareSave(state); const save = state.save(); state.edit(row("new-form")); pending.resolve(node()); await save;
 expect(state.editingId).toBe("new-form"); expect(state.form.label).toBe("new-form");
});
it("blocks duplicate writes while a mutation is pending", async () => {
 const { state } = await mount(); const pending = deferred<api.ClusterNode>(); vi.mocked(api.registerClusterNode).mockReturnValueOnce(pending.promise); prepareSave(state); const save = state.save(); await state.save(); await state.remove(row()); expect(api.registerClusterNode).toHaveBeenCalledTimes(1); expect(mocks.confirm).not.toHaveBeenCalled(); pending.resolve(node()); await save;
});
it("closes a reserved browser tab without navigating after logout", async () => {
 const tab = { opener: {}, closed: false, close: vi.fn(), location: { replace: vi.fn() } }; vi.stubGlobal("window", { open: vi.fn(() => tab), location: { origin: "https://panel.test", hostname: "panel.test" } });
 const { state, auth } = await mount(); const pending = deferred<api.ClusterNodeLink>(); vi.mocked(api.getClusterNodeLink).mockReturnValueOnce(pending.promise); const opening = state.open(row()); auth.session = null; pending.resolve({ nodeId: "node-a", url: "https://node-a.test/entry", requiresSignIn: true }); await opening;
 expect(tab.location.replace).not.toHaveBeenCalled(); expect(tab.close).toHaveBeenCalledTimes(1);
});
it("does not issue requests for a customer", async () => {
 const { state } = await mount("Customer"); prepareSave(state); await state.save(); await state.remove(row()); expect(api.getClusterOverview).not.toHaveBeenCalled(); expect(api.registerClusterNode).not.toHaveBeenCalled(); expect(mocks.confirm).not.toHaveBeenCalled();
});
