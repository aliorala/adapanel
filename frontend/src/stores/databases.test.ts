import { afterEach, beforeEach, expect, it, vi } from "vitest";
import axios, { type AxiosResponse, type InternalAxiosRequestConfig } from "axios";
import { createPinia, setActivePinia } from "pinia";
import * as databaseApi from "../api/databases";
import { apiClient, invalidateSessionRequests } from "../api/client";
import { resetAllStores } from "./resetAll";
import { useDatabasesStore } from "./databases";

vi.mock("../api/databases", () => ({
  getDatabaseEngines: vi.fn(), getDatabaseWorkspace: vi.fn(), registerDatabaseCredentials: vi.fn(),
  createDatabase: vi.fn(), createDatabaseUser: vi.fn(), importDatabase: vi.fn(), deleteDatabase: vi.fn(),
  grantDatabaseAccess: vi.fn(), deleteDatabaseUser: vi.fn(), setDatabaseUserPassword: vi.fn()
}));

const originalAdapter = apiClient.defaults.adapter;
let extraInterceptor: number | undefined;
const workspace = (name: string, engine = "mariadb"): databaseApi.DatabaseWorkspace => ({
  engine, displayName: engine, componentId: engine, serviceName: engine, host: "localhost", port: 3306,
  adminUserName: "root", adminPassword: "fixture-password", supportsHostScopedUsers: true,
  databases: [{ engine, name, owner: "root", encoding: "utf8", collation: "", users: [] }], users: []
});
const status = (ready: boolean): databaseApi.DatabaseEngineStatus => ({ engine: "mariadb", displayName: "MariaDB", installed: true, ready, systemDetected: false, host: "localhost", port: 3306, adminUserName: "root", supportsHardening: true });
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }

beforeEach(() => {
  setActivePinia(createPinia());
  vi.stubGlobal("window", { location: { pathname: "/entry" } });
  invalidateSessionRequests();
  vi.mocked(databaseApi.getDatabaseWorkspace).mockResolvedValue(workspace("current"));
  vi.mocked(databaseApi.getDatabaseEngines).mockResolvedValue([status(true)]);
  vi.mocked(databaseApi.registerDatabaseCredentials).mockResolvedValue(status(true));
  vi.mocked(databaseApi.deleteDatabase).mockResolvedValue();
  vi.mocked(databaseApi.deleteDatabaseUser).mockResolvedValue();
});
afterEach(() => {
  if (extraInterceptor !== undefined) apiClient.interceptors.response.eject(extraInterceptor);
  extraInterceptor = undefined;
  apiClient.defaults.adapter = originalAdapter;
  invalidateSessionRequests();
  vi.resetAllMocks(); vi.unstubAllGlobals();
});

it("keeps the newest workspace when two reads finish out of order", async () => {
  const store = useDatabasesStore(), old = deferred<databaseApi.DatabaseWorkspace>();
  vi.mocked(databaseApi.getDatabaseWorkspace).mockReturnValueOnce(old.promise);
  const first = store.fetchWorkspace("mariadb");
  await store.fetchWorkspace("mariadb"); old.resolve(workspace("old")); await first;
  expect(store.workspaces.mariadb.databases[0].name).toBe("current");
});

it("keeps loading until the newest read finishes", async () => {
  const store = useDatabasesStore(), first = deferred<databaseApi.DatabaseWorkspace>(), second = deferred<databaseApi.DatabaseWorkspace>();
  vi.mocked(databaseApi.getDatabaseWorkspace).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
  const older = store.fetchWorkspace("mariadb"), newer = store.fetchWorkspace("mariadb");
  first.resolve(workspace("old")); await older; expect(store.loadingEngine).toBe("mariadb");
  second.resolve(workspace("new")); await newer; expect(store.loadingEngine).toBe("");
});

it("keeps the newest engine list", async () => {
  const store = useDatabasesStore(), old = deferred<databaseApi.DatabaseEngineStatus[]>();
  vi.mocked(databaseApi.getDatabaseEngines).mockReturnValueOnce(old.promise);
  const first = store.fetchEngines(); await store.fetchEngines(); old.resolve([status(false)]); await first;
  expect(store.engines[0].ready).toBe(true);
});

it("keeps the engine-list loading flag owned by the newer request", async () => {
  const store = useDatabasesStore(), first = deferred<databaseApi.DatabaseEngineStatus[]>(), second = deferred<databaseApi.DatabaseEngineStatus[]>();
  vi.mocked(databaseApi.getDatabaseEngines).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
  const older = store.fetchEngines(), newer = store.fetchEngines();
  first.resolve([status(false)]); await older; expect(store.loadingEngines).toBe(true);
  second.resolve([status(true)]); await newer; expect(store.loadingEngines).toBe(false);
});

const mutations = [
  { name: "database creation", api: databaseApi.createDatabase, run: (store: ReturnType<typeof useDatabasesStore>) => store.createDatabase({ engine: "mariadb", name: "created" }) },
  { name: "user creation", api: databaseApi.createDatabaseUser, run: (store: ReturnType<typeof useDatabasesStore>) => store.createUser({ engine: "mariadb", userName: "user", password: "fixture", allowedHost: "localhost" }) },
  { name: "grant update", api: databaseApi.grantDatabaseAccess, run: (store: ReturnType<typeof useDatabasesStore>) => store.grantAccess("mariadb", "db", "user", "localhost") },
  { name: "database import", api: databaseApi.importDatabase, run: (store: ReturnType<typeof useDatabasesStore>) => store.importDatabase({ engine: "mariadb", databaseName: "db", userName: "user", password: "fixture" }) },
  { name: "password update", api: databaseApi.setDatabaseUserPassword, run: (store: ReturnType<typeof useDatabasesStore>) => store.setUserPassword("mariadb", "user", "fixture") }
];
it.each(mutations)("does not let an earlier read overwrite $name", async ({ api, run }) => {
  const store = useDatabasesStore(), old = deferred<databaseApi.DatabaseWorkspace>();
  vi.mocked(databaseApi.getDatabaseWorkspace).mockReturnValueOnce(old.promise);
  vi.mocked(api).mockResolvedValue(workspace("updated"));
  const read = store.fetchWorkspace("mariadb"); await run(store); old.resolve(workspace("old")); await read;
  expect(store.workspaces.mariadb.databases[0].name).toBe("updated");
});

it.each(mutations)("does not commit $name after the session changed", async ({ api, run }) => {
  const store = useDatabasesStore(), pending = deferred<databaseApi.DatabaseWorkspace>();
  vi.mocked(api).mockReturnValueOnce(pending.promise);
  const result = run(store).catch(error => error); resetAllStores(); pending.resolve(workspace("previous session"));
  expect(axios.isCancel(await result)).toBe(true);
  expect(store.workspaces).toEqual({});
  expect(databaseApi.getDatabaseWorkspace).not.toHaveBeenCalled();
});

it("also invalidates reads started while a mutation was pending", async () => {
  const store = useDatabasesStore(), write = deferred<databaseApi.DatabaseWorkspace>(), read = deferred<databaseApi.DatabaseWorkspace>();
  vi.mocked(databaseApi.createDatabase).mockReturnValue(write.promise);
  const save = store.createDatabase({ engine: "mariadb", name: "created" });
  vi.mocked(databaseApi.getDatabaseWorkspace).mockReturnValue(read.promise);
  const refresh = store.fetchWorkspace("mariadb"); write.resolve(workspace("created")); await save;
  read.resolve(workspace("before creation")); await refresh;
  expect(store.workspaces.mariadb.databases[0].name).toBe("created");
});

it("does not undo successfully registered credentials with an older engine list", async () => {
  const store = useDatabasesStore(), old = deferred<databaseApi.DatabaseEngineStatus[]>();
  vi.mocked(databaseApi.getDatabaseEngines).mockReturnValueOnce(old.promise);
  const engines = store.fetchEngines();
  await store.registerCredentials({ engine: "mariadb", password: "fixture-secret" });
  old.resolve([status(false)]); await engines;
  expect(store.engines[0].ready).toBe(true);
  expect(store.workspaces.mariadb.databases[0].name).toBe("current");
  expect(JSON.stringify(store.$state)).not.toContain("fixture-secret");
});

it("retains unrelated engine statuses when a credentials update overlaps the initial engine list", async () => {
  const store = useDatabasesStore(), pending = deferred<databaseApi.DatabaseEngineStatus[]>();
  vi.mocked(databaseApi.getDatabaseEngines).mockReturnValueOnce(pending.promise);
  const read = store.fetchEngines();
  await store.registerCredentials({ engine: "mariadb", password: "fixture" });
  pending.resolve([status(false), { ...status(true), engine: "postgresql", displayName: "PostgreSQL", port: 5432 }]);
  await read;
  expect(store.engines.find(engine => engine.engine === "mariadb")?.ready).toBe(true);
  expect(store.engines.find(engine => engine.engine === "postgresql")?.port).toBe(5432);
});

it("does not clear another deletion's busy state when an older deletion finishes", async () => {
  const store = useDatabasesStore(), old = deferred<void>(), next = deferred<void>();
  vi.mocked(databaseApi.deleteDatabase).mockReturnValueOnce(old.promise).mockReturnValueOnce(next.promise);
  const first = store.deleteDatabase("mariadb", "first"), second = store.deleteDatabase("mariadb", "second");
  old.resolve(); await first; expect(store.deletingKey).toBe("mariadb:second");
  next.resolve(); await second; expect(store.deletingKey).toBe("");
});

it("refreshes current data when overlapping mutations return in reverse order", async () => {
  const store = useDatabasesStore(), old = deferred<databaseApi.DatabaseWorkspace>();
  vi.mocked(databaseApi.createDatabase).mockReturnValueOnce(old.promise).mockResolvedValueOnce(workspace("newer"));
  const first = store.createDatabase({ engine: "mariadb", name: "first" });
  await store.createDatabase({ engine: "mariadb", name: "second" });
  old.resolve(workspace("older snapshot")); await first;
  expect(store.workspaces.mariadb.databases[0].name).toBe("current");
  expect(databaseApi.getDatabaseWorkspace).toHaveBeenCalledExactlyOnceWith("mariadb");
});

it("also reconciles overlapping writes that return in request order", async () => {
  const store = useDatabasesStore(), older = deferred<databaseApi.DatabaseWorkspace>(), newer = deferred<databaseApi.DatabaseWorkspace>();
  vi.mocked(databaseApi.createDatabase).mockReturnValueOnce(older.promise).mockReturnValueOnce(newer.promise);
  const first = store.createDatabase({ engine: "mariadb", name: "first" });
  const second = store.createDatabase({ engine: "mariadb", name: "second" });
  older.resolve(workspace("first snapshot")); await first;
  expect(store.creatingEngine).toBe("mariadb");
  newer.resolve(workspace("second snapshot")); await second;
  expect(store.workspaces.mariadb.databases[0].name).toBe("current");
  expect(store.creatingEngine).toBe("");
  expect(databaseApi.getDatabaseWorkspace).toHaveBeenCalledTimes(2);
});

it("reconciles overlapping credential changes without restoring an older status", async () => {
  const store = useDatabasesStore(), older = deferred<databaseApi.DatabaseEngineStatus>(), newer = deferred<databaseApi.DatabaseEngineStatus>();
  vi.mocked(databaseApi.registerDatabaseCredentials).mockReturnValueOnce(older.promise).mockReturnValueOnce(newer.promise);
  const first = store.registerCredentials({ engine: "mariadb", password: "first" });
  const second = store.registerCredentials({ engine: "mariadb", password: "second" });
  older.resolve({ ...status(true), port: 3307 }); await first;
  expect(store.registeringEngine).toBe("mariadb");
  newer.resolve({ ...status(true), port: 3308 }); await second;
  expect(store.engines[0].port).toBe(3306);
  expect(store.registeringEngine).toBe("");
  expect(databaseApi.getDatabaseEngines).toHaveBeenCalledTimes(2);
});

it("a rejected earlier write does not clear a newer write's busy state", async () => {
  const store = useDatabasesStore(), older = deferred<databaseApi.DatabaseWorkspace>(), newer = deferred<databaseApi.DatabaseWorkspace>();
  vi.mocked(databaseApi.grantDatabaseAccess).mockReturnValueOnce(older.promise).mockReturnValueOnce(newer.promise);
  const first = store.grantAccess("mariadb", "first", "user", "localhost").catch(error => error);
  const second = store.grantAccess("mariadb", "second", "user", "localhost");
  const error = new Error("Grant rejected"); older.reject(error);
  expect(await first).toBe(error); expect(store.grantingKey).toBe("mariadb:second");
  newer.resolve(workspace("updated")); await second;
  expect(store.grantingKey).toBe(""); expect(store.workspaces.mariadb.databases[0].name).toBe("updated");
});

it("the existing interceptor already rejects a network response from a previous session", async () => {
  const realApi = await vi.importActual<typeof databaseApi>("../api/databases");
  vi.mocked(databaseApi.getDatabaseWorkspace).mockImplementation(realApi.getDatabaseWorkspace);
  const store = useDatabasesStore(), pending = deferred<AxiosResponse>(); let config!: InternalAxiosRequestConfig;
  apiClient.defaults.adapter = async request => { config = request; return pending.promise; };
  const read = store.fetchWorkspace("mariadb").catch(error => error);
  await vi.waitFor(() => expect(config).toBeDefined()); resetAllStores();
  pending.resolve({ config, status: 200, statusText: "OK", headers: {}, data: { data: workspace("old private data") } });
  expect(axios.isCancel(await read)).toBe(true); expect(store.workspaces).toEqual({});
});

it("does not commit a response accepted by the interceptor just before a session reset", async () => {
  const realApi = await vi.importActual<typeof databaseApi>("../api/databases");
  vi.mocked(databaseApi.getDatabaseWorkspace).mockImplementation(realApi.getDatabaseWorkspace);
  apiClient.defaults.adapter = async config => ({ config, status: 200, statusText: "OK", headers: {}, data: { data: workspace("old private data") } });
  // This is AFTER the client's installed stale-response guard, before the API/store await resumes.
  extraInterceptor = apiClient.interceptors.response.use(response => { resetAllStores(); return response; });
  const store = useDatabasesStore();
  const result = await store.fetchWorkspace("mariadb").catch(error => error);
  expect(store.workspaces).toEqual({}); expect(axios.isCancel(result)).toBe(true);
});

it("a cancelled old request cannot clear the new session's loading state", async () => {
  const store = useDatabasesStore(), old = deferred<databaseApi.DatabaseWorkspace>(), next = deferred<databaseApi.DatabaseWorkspace>();
  vi.mocked(databaseApi.getDatabaseWorkspace).mockReturnValueOnce(old.promise).mockReturnValueOnce(next.promise);
  const previous = store.fetchWorkspace("mariadb").catch(error => error); resetAllStores();
  const current = store.fetchWorkspace("mariadb"); old.reject(new axios.CanceledError("Session changed")); await previous;
  expect(store.loadingEngine).toBe("mariadb"); next.resolve(workspace("current")); await current;
});

it("does not dispatch a credentials follow-up read after the session changed", async () => {
  const store = useDatabasesStore(), pending = deferred<databaseApi.DatabaseEngineStatus>();
  vi.mocked(databaseApi.registerDatabaseCredentials).mockReturnValueOnce(pending.promise);
  const register = store.registerCredentials({ engine: "mariadb", password: "fixture" }).catch(error => error);
  resetAllStores(); pending.resolve(status(true));
  expect(axios.isCancel(await register)).toBe(true); expect(store.engines).toEqual([]);
  expect(databaseApi.getDatabaseWorkspace).not.toHaveBeenCalled();
});

it("does not dispatch a deletion follow-up read after the session changed", async () => {
  const store = useDatabasesStore(), pending = deferred<void>();
  vi.mocked(databaseApi.deleteDatabaseUser).mockReturnValueOnce(pending.promise);
  const removal = store.deleteUser("mariadb", "user", "localhost").catch(error => error);
  resetAllStores(); pending.resolve();
  expect(axios.isCancel(await removal)).toBe(true); expect(databaseApi.getDatabaseWorkspace).not.toHaveBeenCalled();
});

it("keeps successful reads for different engines separate", async () => {
  const store = useDatabasesStore();
  vi.mocked(databaseApi.getDatabaseWorkspace).mockImplementation(async engine => workspace(engine + " database", engine));
  await Promise.all([store.fetchWorkspace("mariadb"), store.fetchWorkspace("postgresql")]);
  expect(Object.keys(store.workspaces).sort()).toEqual(["mariadb", "postgresql"]);
  expect(store.workspaces.postgresql.engine).toBe("postgresql");
});
