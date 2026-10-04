import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import DatabasesView from "./DatabasesView.vue";
import { apiClient } from "../api/client";
import { useSoftwareStore, type SoftwareCatalog } from "../stores/software";
import { useDatabasesStore } from "../stores/databases";
import { useSitesStore } from "../stores/sites";
import type { DatabaseWorkspace } from "../api/databases";

const shared = vi.hoisted(() => ({ epoch: 0 }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: vi.fn() }) }));
vi.mock("../components/PromptDialog.vue", () => ({ usePrompt: () => ({ prompt: vi.fn() }) }));
vi.mock("../api/client", async original => ({ ...await original<object>(), getSessionEpoch: () => shared.epoch }));

type State = {
  notice: string; noticeError: boolean; refreshing: boolean; activeModule: string; workspaceReady: boolean;
  refreshWorkspace(): Promise<void>; installSelectedEngine(): Promise<void>; selectEngine(engine: string): void;
};
let app: App | undefined;
function deferred<T>() {
  let resolve!: (value: T) => void, reject!: (error: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
async function flush() { for (let count = 0; count < 30; count++) await nextTick(); }
function catalog(installed = false): SoftwareCatalog {
  const manifest = [{ id: "mariadb-11", displayName: "MariaDB", version: "11.8.0", type: "zip",
    downloadUrl: "https://example.invalid/mariadb.zip", sha256: "", installDir: "C:\\Fixture\\MariaDB",
    postInstall: [], dependencies: [], serviceName: "MariaDB", silentArgs: "", note: "", default: true }];
  return { manifest, defaults: manifest, installed: installed ? [{ id: "11111111-1111-1111-1111-111111111111",
    componentId: "mariadb-11", displayName: "MariaDB", version: "11.8.0", type: "Zip", installDir: "C:\\Fixture\\MariaDB",
    serviceName: "MariaDB", status: "Installed", lastError: "", generatedPassword: "", isSystemDetected: false,
    detectionSource: "", availableVersion: "", updateAvailable: false, versionCheckSupported: true,
    versionCheckFailed: false, installedAtUtc: "2026-01-01T00:00:00Z" }] : [] };
}
const response = (value: SoftwareCatalog) => ({ data: { success: true, data: value, message: "", code: 0 } });
const workspace = (): DatabaseWorkspace => ({ engine: "mariadb", displayName: "MariaDB", componentId: "mariadb-11",
  serviceName: "MariaDB", host: "127.0.0.1", port: 3306, adminUserName: "root", adminPassword: "",
  supportsHostScopedUsers: true, databases: [], users: [] });
function markup(state: State) {
  const parts: unknown[] = [];
  (DatabasesView as unknown as { ssrRender: (...args: unknown[]) => void })
    .ssrRender(state, (part: unknown) => parts.push(part), app!._instance, {}, {}, state, {}, {});
  return parts.flat(Infinity).join("").replace(/<!--[\s\S]*?-->/g, "");
}
const refreshButton = (state: State) => markup(state).match(/<button[^>]*>\s*panel\.database\.refresh\s*<\/button>/)?.[0];
const installButton = (state: State) => markup(state).match(/<button[^>]*>\s*panel\.database\.installAction\s*<\/button>/)?.[0];
beforeEach(() => { shared.epoch = 0; });
afterEach(() => { app?.unmount(); app = undefined; vi.restoreAllMocks(); });
async function mount(options: { cached?: SoftwareCatalog; loaded?: boolean; read?: () => Promise<SoftwareCatalog> } = {}) {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}),
    insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia);
  const software = useSoftwareStore(); software.catalog = options.cached ?? null; software.loaded = options.loaded ?? false;
  const get = vi.spyOn(apiClient, "get").mockImplementation(async url => {
    expect(url).toBe("/software"); return response(await (options.read?.() ?? Promise.resolve(catalog()))) as never;
  });
  const install = vi.spyOn(software, "installComponent").mockResolvedValue({ componentId: "mariadb-11", displayName: "MariaDB",
    status: "Installed", version: "11.8.0", installDir: "C:\\Fixture\\MariaDB", serviceName: "MariaDB", generatedPassword: "", message: "software.install.completed" });
  const databases = useDatabasesStore();
  const engines = vi.spyOn(databases, "fetchEngines").mockResolvedValue([]);
  const readWorkspace = vi.spyOn(databases, "fetchWorkspace").mockImplementation(async () => {
    databases.workspaces.mariadb = workspace(); return databases.workspaces.mariadb;
  });
  vi.spyOn(useSitesStore(), "fetchSites").mockResolvedValue([]);
  app = renderer.createApp({ ...DatabasesView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState;
  await flush(); return { state, software, get, install, engines, readWorkspace };
}

it("shows unknown installation state and an enabled retry after the first catalog read fails", async () => {
  const { state, install } = await mount({ read: async () => { throw new Error("catalog offline"); } });
  expect(markup(state)).toContain("panel.database.catalogUnavailable");
  expect(markup(state)).not.toContain("panel.database.installTitle");
  expect(markup(state)).not.toContain("panel.database.notInstalled");
  expect(refreshButton(state)).toBeDefined(); expect(refreshButton(state)).not.toContain("disabled");
  await state.installSelectedEngine(); expect(install).not.toHaveBeenCalled();
});

it("retries the actual catalog GET and enables installation only after confirmed absence", async () => {
  let calls = 0;
  const { state, get, install, software } = await mount({ read: async () => { if (++calls === 1) throw new Error("offline"); return catalog(); } });
  await state.refreshWorkspace();
  expect(get).toHaveBeenCalledTimes(2); expect(software.loaded).toBe(true);
  expect(markup(state)).toContain("panel.database.installTitle");
  expect(installButton(state)).not.toContain("disabled");
  expect(state.notice).not.toContain("offline");
  await state.installSelectedEngine(); expect(install).toHaveBeenCalledExactlyOnceWith("mariadb-11");
});

it("remeasures a retained catalog whose last read failed before allowing an installation", async () => {
  const { state, get, install, engines } = await mount({ cached: catalog(), loaded: false, read: async () => { throw new Error("still offline"); } });
  expect(get).toHaveBeenCalledOnce(); expect(engines).not.toHaveBeenCalled();
  await state.installSelectedEngine(); expect(install).not.toHaveBeenCalled();
  expect(markup(state)).toContain("panel.database.catalogUnavailable");
});

it("does not install from retained rows when a later catalog read has become unconfirmed", async () => {
  const { state, software, install } = await mount({ cached: catalog(), loaded: true });
  software.loaded = false; await flush(); await state.installSelectedEngine();
  expect(install).not.toHaveBeenCalled(); expect(installButton(state)).toBeUndefined();
});

it("does not install while a shared catalog measurement is pending", async () => {
  const { state, software, install } = await mount({ cached: catalog(), loaded: true });
  software.loading = true; await flush(); await state.installSelectedEngine();
  expect(install).not.toHaveBeenCalled(); expect(markup(state)).toContain("panel.common.loading");
});

it("allows installation from an already confirmed catalog without another initial request", async () => {
  const { state, get, install } = await mount({ cached: catalog(), loaded: true });
  expect(get).not.toHaveBeenCalled(); await state.installSelectedEngine(); expect(install).toHaveBeenCalledExactlyOnceWith("mariadb-11");
});

it("refreshes catalog evidence before the selected installed engine workspace", async () => {
  const order: string[] = [];
  const { state, get, readWorkspace } = await mount({ cached: catalog(true), loaded: true, read: async () => { order.push("catalog"); return catalog(true); } });
  readWorkspace.mockImplementationOnce(async () => { order.push("workspace"); return workspace(); });
  await state.refreshWorkspace();
  expect(get).toHaveBeenCalledOnce(); expect(order).toEqual(["catalog", "workspace"]); expect(state.workspaceReady).toBe(true);
});

it("does not continue workspace refresh when the new catalog measurement fails", async () => {
  const { state, software, readWorkspace } = await mount({ cached: catalog(true), loaded: true, read: async () => { throw new Error("offline"); } });
  readWorkspace.mockClear(); await state.refreshWorkspace();
  expect(software.loaded).toBe(false); expect(readWorkspace).not.toHaveBeenCalled(); expect(state.workspaceReady).toBe(false);
  expect(markup(state)).toContain("panel.database.catalogUnavailable");
});

it("does not send duplicate retry reads while one is pending", async () => {
  const pending = deferred<SoftwareCatalog>(); let first = true;
  const { state, get } = await mount({ read: async () => { if (first) { first = false; throw new Error("offline"); } return pending.promise; } });
  const retry = state.refreshWorkspace(); await flush(); await state.refreshWorkspace();
  expect(get).toHaveBeenCalledTimes(2); expect(state.refreshing).toBe(true);
  pending.resolve(catalog()); await retry; expect(state.refreshing).toBe(false);
});

it.each(["session", "module", "unmount"] as const)("ignores retry follow-up after %s changes", async change => {
  const pending = deferred<SoftwareCatalog>(); let first = true;
  const { state, get, engines, readWorkspace } = await mount({ read: async () => { if (first) { first = false; throw new Error("offline"); } return pending.promise; } });
  const retry = state.refreshWorkspace(); await flush(); expect(get).toHaveBeenCalledTimes(2);
  if (change === "session") shared.epoch++;
  else if (change === "module") state.activeModule = "webdb";
  else { app!.unmount(); app = undefined; }
  pending.resolve(catalog(true)); await retry;
  expect(engines).not.toHaveBeenCalled(); expect(readWorkspace).not.toHaveBeenCalled(); expect(state.notice).not.toBe("panel.database.refreshed");
});

it("does not read database state before the first catalog measurement completes", async () => {
  const pending = deferred<SoftwareCatalog>();
  const { state, engines, readWorkspace, install } = await mount({ read: () => pending.promise });
  await state.installSelectedEngine(); expect(engines).not.toHaveBeenCalled(); expect(install).not.toHaveBeenCalled();
  pending.resolve(catalog(true)); await flush(); expect(engines).toHaveBeenCalledOnce(); expect(readWorkspace).toHaveBeenCalledOnce();
});
