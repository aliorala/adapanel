import { afterAll, afterEach, beforeAll, beforeEach, expect, it, vi } from "vitest";
import type { AxiosResponse, InternalAxiosRequestConfig } from "axios";
import type { CreateSitePayload, SiteRecord } from "../api/sites";
import type { DatabaseEngineStatus, DatabaseWorkspace } from "../api/databases";
import type { ComponentInstallResult, SoftwareCatalog } from "./software";
import type { OperationProgress } from "./setup";

// NODE_ENV=test disables the installed Pinia action-grouping plugin. Load the
// actual development plugin so every action, including nested actions, gets its
// own `this` Proxy just as it does in the browser.
let vue: typeof import("vue"), pinia: typeof import("pinia"), client: typeof import("../api/client");
let sites: typeof import("./sites"), software: typeof import("./software"), databases: typeof import("./databases");
let originalAdapter: typeof client.apiClient.defaults.adapter;
let reply: (config: InternalAxiosRequestConfig) => unknown;
let calls: string[];
beforeAll(async () => {
  vi.stubEnv("NODE_ENV", "development");
  vi.stubGlobal("window", { location: { hostname: "localhost", pathname: "/panel" } });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture-token" } }), { status: 200 })));
  vue = await import("vue"); pinia = await import("pinia"); client = await import("../api/client");
  sites = await import("./sites"); software = await import("./software"); databases = await import("./databases");
  originalAdapter = client.apiClient.defaults.adapter;
});
beforeEach(() => {
  client.invalidateSessionRequests(); calls = [];
  reply = config => { throw new Error(`Unexpected request ${key(config)}`); };
  client.apiClient.defaults.adapter = async config => {
    calls.push(key(config));
    const data = await reply(config);
    return { config, data: { success: true, data }, headers: {}, status: 200, statusText: "OK" } satisfies AxiosResponse;
  };
});
afterEach(() => { client.apiClient.defaults.adapter = originalAdapter; client.invalidateSessionRequests(); });
afterAll(() => { vi.unstubAllEnvs(); vi.unstubAllGlobals(); });

function key(config: InternalAxiosRequestConfig) { return `${config.method} ${config.url}`; }
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
async function reached(request: string, count = 1) { await vi.waitFor(() => expect(calls.filter(value => value === request)).toHaveLength(count)); }
function installedPinia() {
  const instance = pinia.createPinia();
  const renderer = vue.createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  renderer.createApp({ render: () => vue.h("section") }).use(instance);
  return instance;
}
const site = (name: string, state = "Started"): SiteRecord => ({ id: name === "a" ? 1 : 2, name, state, autoStart: true, physicalPath: `fixture/${name}`, applicationRootPath: `fixture/${name}`, applicationPoolName: name, bindings: [] });
const payload = (name: string): CreateSitePayload => ({ name, physicalPath: `fixture/${name}`, applicationPoolName: name, hostName: name, ipAddress: "*", port: 80, protocol: "http", autoStart: true, runtimeProfile: "auto", runtimeComponentId: "", installRuntimeIfMissing: false, startupFile: "" });
const created = (name: string) => ({ site: site(name), created: [], warnings: [], generatedCredentials: [] });
function siteStore() { const store = sites.useSitesStore(installedPinia()); store.sites = [site("a", "Stopped")]; store.selectedSiteName = "a"; store.loaded = true; return store; }
const catalog = (id: string): SoftwareCatalog => ({ manifest: [{ id, displayName: id, version: "1", type: "runtime", downloadUrl: "", sha256: "", installDir: "fixture", postInstall: [], dependencies: [], serviceName: "", silentArgs: "", note: "", default: false }], defaults: [], installed: [] });
const installed: ComponentInstallResult = { componentId: "fixture", displayName: "Fixture", version: "1", status: "Installed", installDir: "fixture", serviceName: "", generatedPassword: "", message: "installed" };
const progress = (title: string): OperationProgress => ({ title, running: true, completed: false, failed: false, error: "", steps: [], startedAtUtc: null });
const workspace = (name: string, engine = "mariadb"): DatabaseWorkspace => ({ engine, displayName: engine, componentId: engine, serviceName: engine, host: "localhost", port: 3306, adminUserName: "root", adminPassword: "fixture-password", supportsHostScopedUsers: true, databases: [{ engine, name, owner: "root", encoding: "utf8", collation: "", users: [] }], users: [] });
const status = (ready: boolean): DatabaseEngineStatus => ({ engine: "mariadb", displayName: "MariaDB", installed: true, ready, systemDetected: false, host: "localhost", port: 3306, adminUserName: "root", supportsHardening: true });

it("uses the installed Pinia plugin's distinct action contexts with the same raw store", () => {
  const useProbe = pinia.defineStore("three-store-proxy-probe", { state: () => ({ count: 0 }), actions: { context(): object { return this; } } });
  const store = useProbe(installedPinia()), first = store.context(), second = store.context();
  expect(first).not.toBe(second); expect(vue.toRaw(first)).toBe(vue.toRaw(second));
});

it("keeps a created site and its selection after an older list returns", async () => {
  const store = siteStore(), old = deferred<SiteRecord[]>(); let reads = 0;
  reply = config => config.method === "post" ? created("b") : ++reads === 1 ? old.promise : [site("a"), site("b")];
  const reading = store.fetchSites(); await reached("get /scope/sites");
  await store.createSite(payload("b")); expect(store.selectedSiteName).toBe("b");
  old.resolve([site("a", "Stopped")]); await reading;
  expect(store.sites.map(value => value.name)).toEqual(["a", "b"]); expect(store.selectedSiteName).toBe("b");
  expect(store.loaded).toBe(true); expect(store.loading).toBe(false); expect(reads).toBe(2);
});

it("keeps a user's newer selection while an earlier site creation completes", async () => {
  const store = siteStore(), creating = deferred<ReturnType<typeof created>>();
  reply = () => creating.promise;
  const work = store.createSite(payload("b")); await reached("post /sites"); store.selectSite("a");
  creating.resolve(created("b")); await work;
  expect(store.sites.map(value => value.name)).toEqual(["a", "b"]); expect(store.selectedSiteName).toBe("a");
});

it("keeps different-site writes visible and busy until both finish, then reconciles once", async () => {
  const store = siteStore(), a = deferred<SiteRecord>(), b = deferred<SiteRecord>(); store.sites.push(site("b", "Stopped"));
  reply = config => config.url === "/sites/a/start" ? a.promise : config.url === "/sites/b/start" ? b.promise : [site("a"), site("b")];
  const first = store.startSite("a"), second = store.startSite("b"); await reached("post /sites/b/start");
  a.resolve(site("a")); await first; const busy = store.busySiteName;
  b.resolve(site("b")); await second;
  expect(busy).toBe("b"); expect(store.busySiteName).toBe(""); expect(store.sites.map(value => value.state)).toEqual(["Started", "Started"]);
  expect(calls.filter(value => value === "get /scope/sites")).toHaveLength(1);
});

it("keeps the software catalog measured after installation when an older catalog returns", async () => {
  const store = software.useSoftwareStore(installedPinia()), old = deferred<SoftwareCatalog>(); let reads = 0;
  reply = config => config.method === "post" ? installed : ++reads === 1 ? old.promise : catalog("installed-current");
  const reading = store.fetchCatalog(); await reached("get /software");
  await store.installComponent("fixture"); expect(store.catalog?.manifest[0].id).toBe("installed-current");
  old.resolve(catalog("before-install")); await reading;
  expect(store.catalog?.manifest[0].id).toBe("installed-current"); expect(store.loaded).toBe(true); expect(store.installingComponentId).toBe("");
});

it("does not publish progress from before a software mutation", async () => {
  const store = software.useSoftwareStore(installedPinia()), old = deferred<OperationProgress>();
  reply = config => config.url === "/software/operations/progress" ? old.promise : config.method === "post" ? installed : catalog("current");
  const reading = store.fetchOperationProgress(); await reached("get /software/operations/progress");
  await store.installComponent("fixture"); old.resolve(progress("obsolete")); await reading;
  expect(store.liveProgress).toBeNull(); expect(store.catalog?.manifest[0].id).toBe("current");
});

it("keeps the second software installation busy after the first finishes", async () => {
  const store = software.useSoftwareStore(installedPinia()), first = deferred<ComponentInstallResult>(), second = deferred<ComponentInstallResult>(); let writes = 0;
  reply = config => config.method === "post" ? ++writes === 1 ? first.promise : second.promise : catalog("current");
  const older = store.installComponent("old"), newer = store.installComponent("new"); await reached("post /software/install", 2);
  first.resolve({ ...installed, componentId: "old" }); await older; const busy = store.installingComponentId;
  second.resolve({ ...installed, componentId: "new" }); await newer;
  expect(busy).toBe("new"); expect(store.installingComponentId).toBe(""); expect(store.loaded).toBe(true);
});

it("does not let an old database workspace erase a successfully created database", async () => {
  const store = databases.useDatabasesStore(installedPinia()), old = deferred<DatabaseWorkspace>();
  reply = config => config.method === "post" ? workspace("created") : old.promise;
  const reading = store.fetchWorkspace("mariadb"); await reached("get /db");
  await store.createDatabase({ engine: "mariadb", name: "created" }); expect(store.workspaces.mariadb.databases[0].name).toBe("created");
  old.resolve(workspace("before-create")); await reading;
  expect(store.workspaces.mariadb.databases[0].name).toBe("created"); expect(store.creatingEngine).toBe("");
});

it("does not let an old engine list undo verified database credentials", async () => {
  const store = databases.useDatabasesStore(installedPinia()), old = deferred<DatabaseEngineStatus[]>();
  reply = config => config.url === "/db/engines" ? old.promise : config.url === "/db/credentials" ? status(true) : workspace("verified");
  const reading = store.fetchEngines(); await reached("get /db/engines");
  await store.registerCredentials({ engine: "mariadb", password: "fixture-password" });
  old.resolve([status(false)]); await reading;
  expect(store.engines.find(value => value.engine === "mariadb")?.ready).toBe(true);
  expect(store.workspaces.mariadb.databases[0].name).toBe("verified");
});

it("keeps the newest workspace read busy until its response arrives", async () => {
  const store = databases.useDatabasesStore(installedPinia()), first = deferred<DatabaseWorkspace>(), second = deferred<DatabaseWorkspace>(); let reads = 0;
  reply = () => ++reads === 1 ? first.promise : second.promise;
  const older = store.fetchWorkspace("mariadb"), newer = store.fetchWorkspace("mariadb"); await reached("get /db", 2);
  first.resolve(workspace("old")); await older; const busy = store.loadingEngine;
  second.resolve(workspace("new")); await newer;
  expect(busy).toBe("mariadb"); expect(store.loadingEngine).toBe(""); expect(store.workspaces.mariadb.databases[0].name).toBe("new");
});

it("reconciles overlapping same-engine writes instead of publishing the last stale response", async () => {
  const store = databases.useDatabasesStore(installedPinia()), first = deferred<DatabaseWorkspace>(), second = deferred<DatabaseWorkspace>(); let writes = 0;
  reply = config => config.method === "post" ? ++writes === 1 ? first.promise : second.promise : workspace("final-measurement");
  const older = store.createDatabase({ engine: "mariadb", name: "first" }), newer = store.createDatabase({ engine: "mariadb", name: "second" }); await reached("post /db", 2);
  second.resolve(workspace("second-response")); await newer;
  first.resolve(workspace("first-stale-response")); await older;
  expect(store.workspaces.mariadb.databases[0].name).toBe("final-measurement"); expect(calls.filter(value => value === "get /db")).toHaveLength(1);
});

it("preserves independent engine writes when their responses finish out of order", async () => {
  const store = databases.useDatabasesStore(installedPinia()), maria = deferred<DatabaseWorkspace>(), postgres = deferred<DatabaseWorkspace>();
  reply = config => JSON.parse(config.data as string).engine === "mariadb" ? maria.promise : postgres.promise;
  const first = store.createDatabase({ engine: "mariadb", name: "maria" }), second = store.createDatabase({ engine: "postgresql", name: "Postgres" }); await reached("post /db", 2);
  postgres.resolve(workspace("Postgres", "postgresql")); await second;
  maria.resolve(workspace("maria")); await first;
  expect(store.workspaces.mariadb.databases[0].name).toBe("maria"); expect(store.workspaces.postgresql.databases[0].name).toBe("Postgres");
  expect(calls).toHaveLength(2);
});
