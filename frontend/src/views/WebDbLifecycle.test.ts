import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import WebDbView from "./WebDbView.vue";
import { getWebDbWorkspace, deployAdminer, deployPhpMyAdmin, type WebDbWorkspace, type WebDbDeployment } from "../api/webdb";
import { getDatabaseEngines, openPhpMyAdmin, type DatabaseEngineStatus } from "../api/databases";
import { useSoftwareStore } from "../stores/software";
import { useSubscriptionStore } from "../stores/subscription";
const shared = vi.hoisted(() => ({ epoch: 0 }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../api/client", async original => ({ ...await original<object>(), getSessionEpoch: () => shared.epoch }));
vi.mock("../api/webdb", () => ({ getWebDbWorkspace: vi.fn(), deployAdminer: vi.fn(), deployPhpMyAdmin: vi.fn() }));
vi.mock("../api/databases", () => ({ getDatabaseEngines: vi.fn(), openPhpMyAdmin: vi.fn() }));
type State = { workspace: WebDbWorkspace | null; loading: boolean; notice: string; busyProvider: string; selectedEngine: string; canOpenPhpMyAdmin: boolean; refreshWorkspace(): Promise<unknown>; runAction(provider: string, componentId: string): Promise<void>; openPhpMyAdminRoot(): Promise<void> };
let app: App | undefined;
let tab: { opener: unknown; closed: boolean; close: ReturnType<typeof vi.fn>; location: { replace: ReturnType<typeof vi.fn> } };
function deployment(provider: string, extra: Partial<WebDbDeployment> = {}): WebDbDeployment { return { provider, displayName: provider, componentId: provider === "pgadmin" ? "pgadmin4" : provider, siteName: provider, port: 8896, url: "http://127.0.0.1:8896", installDir: "fixture", entryFilePath: "fixture/index.php", phpComponentId: "php-8.3", supportedEngines: "MySQL / MariaDB", accessMode: "iis-web", isInstalled: true, isDeployed: true, isHealthy: true, healthMessage: "", healthDetails: "", ...extra }; }
const measured = (): WebDbWorkspace => ({ deployments: [deployment("phpmyadmin"), deployment("adminer"), deployment("pgadmin", { isInstalled: false, isDeployed: false, isHealthy: false })] });
function engine(name: string, ready = true): DatabaseEngineStatus { return { engine: name, displayName: name, installed: true, ready, systemDetected: false, host: "127.0.0.1", port: 3306, adminUserName: "root", supportsHardening: true }; }
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
async function flush() { for (let i = 0; i < 16; i++) await nextTick(); }
beforeEach(() => {
  shared.epoch = 0; vi.mocked(getWebDbWorkspace).mockResolvedValue(measured()); vi.mocked(getDatabaseEngines).mockResolvedValue([engine("mariadb"), engine("mysql")]);
  vi.mocked(deployAdminer).mockResolvedValue(deployment("adminer")); vi.mocked(deployPhpMyAdmin).mockResolvedValue(deployment("phpmyadmin")); vi.mocked(openPhpMyAdmin).mockResolvedValue({ url: "https://panel.test/api/v1/dbtools/phpmyadmin/signon" });
  tab = { opener: {}, closed: false, close: vi.fn(), location: { replace: vi.fn() } }; vi.stubGlobal("window", { open: vi.fn(() => tab), location: { origin: "https://panel.test", hostname: "panel.test", protocol: "https:" } });
});
afterEach(() => { app?.unmount(); app = undefined; vi.restoreAllMocks(); vi.resetAllMocks(); vi.unstubAllGlobals(); });
async function mount() {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia); const software = useSoftwareStore(); vi.spyOn(software, "installComponent").mockResolvedValue({} as never);
  app = renderer.createApp({ ...WebDbView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return { state, software };
}
it("does not deploy before the workspace has been read", async () => {
  const pending = deferred<WebDbWorkspace>(); vi.mocked(getWebDbWorkspace).mockReturnValueOnce(pending.promise); const { state } = await mount(); await state.runAction("adminer", "adminer"); expect(deployAdminer).not.toHaveBeenCalled(); pending.resolve(measured()); await flush();
});
it("does not deploy with stale information after a failed refresh", async () => {
  const { state } = await mount(); vi.mocked(getWebDbWorkspace).mockRejectedValueOnce(new Error("unreadable")); await state.refreshWorkspace(); await state.runAction("adminer", "adminer"); expect(deployAdminer).not.toHaveBeenCalled();
});
it("does not overlap deployments even for different providers", async () => {
  const { state, software } = await mount(), pending = deferred<WebDbDeployment>(); vi.mocked(deployAdminer).mockReturnValueOnce(pending.promise);
  const deploying = state.runAction("adminer", "adminer"); await state.runAction("adminer", "adminer"); await state.runAction("phpmyadmin", "phpmyadmin"); await state.runAction("pgadmin", "pgadmin4");
  expect(deployAdminer).toHaveBeenCalledOnce(); expect(deployPhpMyAdmin).not.toHaveBeenCalled(); expect(software.installComponent).not.toHaveBeenCalled(); pending.resolve(deployment("adminer")); await deploying;
});
it("does not map an unsupported provider to phpMyAdmin", async () => {
  const { state } = await mount(); await state.runAction("unrecognized", "whatever"); expect(deployPhpMyAdmin).not.toHaveBeenCalled();
});
it("cannot send a different install component through the pgAdmin action", async () => {
  const { state, software } = await mount(); await state.runAction("pgadmin", "mysql-8"); expect(software.installComponent).not.toHaveBeenCalled();
});
it("does not reinstall an already installed desktop tool", async () => {
  const snapshot = measured(); snapshot.deployments[2].isInstalled = true; vi.mocked(getWebDbWorkspace).mockResolvedValue(snapshot);
  const { state, software } = await mount(); await state.runAction("pgadmin", "pgadmin4"); expect(software.installComponent).not.toHaveBeenCalled();
});
it("does not hide a refresh failure behind deployment success", async () => {
  const { state } = await mount(); vi.mocked(getWebDbWorkspace).mockRejectedValueOnce(new Error("refresh failed")); await state.runAction("adminer", "adminer"); expect(state.notice).not.toBe("panel.webdb.deployedMessage"); expect(state.notice).toContain("common.unexpected_error");
});
it("does not claim deployment health when the measured tool still needs repair", async () => {
  const { state } = await mount(), snapshot = measured(); snapshot.deployments[1].isHealthy = false; vi.mocked(getWebDbWorkspace).mockResolvedValueOnce(snapshot);
  await state.runAction("adminer", "adminer"); expect(state.notice).toBe("panel.webdb.resultUnconfirmed");
});
it("does not claim pgAdmin installation when the measured component is absent", async () => {
  const { state } = await mount(); await state.runAction("pgadmin", "pgadmin4"); expect(state.notice).toBe("panel.webdb.resultUnconfirmed");
});
it("preserves a successful normal deployment", async () => {
  const { state } = await mount(); await state.runAction("adminer", "adminer"); expect(deployAdminer).toHaveBeenCalledOnce(); expect(state.notice).toBe("panel.webdb.deployedMessage");
});
it("confirms a desktop installation from the refreshed component state", async () => {
  const { state, software } = await mount(), snapshot = measured(); snapshot.deployments[2].isInstalled = true; vi.mocked(getWebDbWorkspace).mockResolvedValueOnce(snapshot);
  await state.runAction("pgadmin", "pgadmin4"); expect(software.installComponent).toHaveBeenCalledExactlyOnceWith("pgadmin4"); expect(state.notice).toBe("panel.webdb.installedMessage");
});
it("does not refresh a closed screen after deployment finishes", async () => {
  const { state } = await mount(), pending = deferred<WebDbDeployment>(); vi.mocked(deployAdminer).mockReturnValueOnce(pending.promise); const deploying = state.runAction("adminer", "adminer");
  app!.unmount(); app = undefined; pending.resolve(deployment("adminer")); await deploying; expect(getWebDbWorkspace).toHaveBeenCalledOnce();
});
it("does not replace a newer workspace with a slow earlier response", async () => {
  const { state } = await mount(), pending = deferred<WebDbWorkspace>(); vi.mocked(getWebDbWorkspace).mockReturnValueOnce(pending.promise);
  const old = state.refreshWorkspace(); await state.refreshWorkspace(); pending.resolve({ deployments: [] }); await old; expect(state.workspace?.deployments).toHaveLength(3);
});
it("does not install or publish a read from an obsolete session", async () => {
  const { state } = await mount(), pending = deferred<WebDbWorkspace>(); vi.mocked(getWebDbWorkspace).mockReturnValueOnce(pending.promise); const read = state.refreshWorkspace(); shared.epoch++;
  pending.resolve({ deployments: [] }); await read; await state.runAction("adminer", "adminer"); expect(deployAdminer).not.toHaveBeenCalled(); expect(state.workspace?.deployments).not.toHaveLength(0);
});
it("does not deploy when the subscription is readonly", async () => {
  const { state } = await mount(); useSubscriptionStore().usage = { isSuspended: true } as never; await state.runAction("adminer", "adminer"); expect(deployAdminer).not.toHaveBeenCalled();
});
it("opens MySQL when it is the only configured phpMyAdmin-compatible engine", async () => {
  vi.mocked(getDatabaseEngines).mockResolvedValue([engine("mysql")]); const { state } = await mount(); await state.openPhpMyAdminRoot(); expect(openPhpMyAdmin).toHaveBeenCalledExactlyOnceWith("mysql");
});
it("uses the engine selected by the user when both engines are ready", async () => {
  const { state } = await mount(); state.selectedEngine = "mysql"; await state.openPhpMyAdminRoot(); expect(openPhpMyAdmin).toHaveBeenCalledExactlyOnceWith("mysql");
});
it("does not reserve a login tab when no compatible engine is ready", async () => {
  vi.mocked(getDatabaseEngines).mockResolvedValue([engine("postgresql"), engine("mysql", false)]); const { state } = await mount(); await state.openPhpMyAdminRoot(); expect(window.open).not.toHaveBeenCalled(); expect(openPhpMyAdmin).not.toHaveBeenCalled();
});
it("does not use stale engine readiness after a failed engine refresh", async () => {
  const { state } = await mount(); vi.mocked(getDatabaseEngines).mockRejectedValueOnce(new Error("engine inspection failed")); await state.refreshWorkspace(); await state.openPhpMyAdminRoot(); expect(openPhpMyAdmin).not.toHaveBeenCalled();
});
it("closes a reserved tab if the view is closed during signon", async () => {
  const { state } = await mount(), pending = deferred<{ url: string }>(); vi.mocked(openPhpMyAdmin).mockReturnValueOnce(pending.promise); const opening = state.openPhpMyAdminRoot();
  app!.unmount(); app = undefined; pending.resolve({ url: "https://db.test/login" }); await opening; expect(tab.close).toHaveBeenCalledOnce(); expect(tab.location.replace).not.toHaveBeenCalled();
});
it("closes a reserved tab when its selected engine changes", async () => {
  const { state } = await mount(), pending = deferred<{ url: string }>(); vi.mocked(openPhpMyAdmin).mockReturnValueOnce(pending.promise); const opening = state.openPhpMyAdminRoot();
  state.selectedEngine = "mysql"; pending.resolve({ url: "https://db.test/login" }); await opening; expect(tab.close).toHaveBeenCalledOnce(); expect(tab.location.replace).not.toHaveBeenCalled();
});
it("preserves synchronous tab reservation and normal signon navigation", async () => {
  const { state } = await mount(); const opening = state.openPhpMyAdminRoot(); expect(window.open).toHaveBeenCalledExactlyOnceWith("", "_blank"); expect(tab.opener).toBeNull();
  await opening; expect(openPhpMyAdmin).toHaveBeenCalledExactlyOnceWith("mariadb"); expect(tab.location.replace).toHaveBeenCalledOnce(); expect(tab.close).not.toHaveBeenCalled();
});
it("does not trust a cached enabled button after the authenticated session changed", async () => {
  const { state } = await mount(); expect(state.canOpenPhpMyAdmin).toBe(true); shared.epoch++;
  await state.openPhpMyAdminRoot(); expect(window.open).not.toHaveBeenCalled(); expect(openPhpMyAdmin).not.toHaveBeenCalled();
});
it("does not revive a signon result after switching away and back to its engine", async () => {
  const { state } = await mount(), pending = deferred<{ url: string }>(); vi.mocked(openPhpMyAdmin).mockReturnValueOnce(pending.promise); const opening = state.openPhpMyAdminRoot();
  state.selectedEngine = "mysql"; state.selectedEngine = "mariadb"; pending.resolve({ url: "https://db.test/login" }); await opening;
  expect(tab.close).toHaveBeenCalledOnce(); expect(tab.location.replace).not.toHaveBeenCalled();
});
