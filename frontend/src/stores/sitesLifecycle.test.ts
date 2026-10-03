import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { isCancel } from "axios";
import { useSitesStore } from "./sites";
import * as api from "../api/sites";
const shared = vi.hoisted(() => ({ epoch: 0 }));
vi.mock("../api/client", async original => ({ ...await original<object>(), getSessionEpoch: () => shared.epoch }));
vi.mock("../api/sites", () => ({ getSites: vi.fn(), getSite: vi.fn(), createSite: vi.fn(), importSites: vi.fn(), startSite: vi.fn(), stopSite: vi.fn(), deleteSite: vi.fn(), addBinding: vi.fn(), removeBinding: vi.fn(), ensureSiteDefaults: vi.fn() }));
type Store = ReturnType<typeof useSitesStore>;
const binding: api.SiteBindingPayload = { protocol: "http", hostName: "a.test", ipAddress: "*", port: 80 };
const site = (name: string, state = "Started"): api.SiteRecord => ({ id: name === "a" ? 1 : 2, name, state, autoStart: true, physicalPath: `fixture/${name}`, applicationRootPath: `fixture/${name}`, applicationPoolName: name, bindings: [{ ...binding, bindingInformation: "*:80:a.test" }] });
const payload = (name: string): api.CreateSitePayload => ({ name, physicalPath: `fixture/${name}`, applicationPoolName: name, hostName: name, ipAddress: "*", port: 80, protocol: "http", autoStart: true, runtimeProfile: "auto", runtimeComponentId: "", installRuntimeIfMissing: false, startupFile: "" });
const created = (name: string): api.SiteCreationResult => ({ site: site(name), created: [], warnings: [], generatedCredentials: [] });
const deleted = (name: string): api.SiteDeletionResult => ({ siteName: name, removed: [], warnings: [], filesDeletionMeasured: true, filesDeletionTruncatedReasonKey: "" });
const defaults = (name: string): api.SiteDefaultsResult => ({ siteName: name, readyFeatures: [], failedFeatures: [], isComplete: true });
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
const operations = [
  { name: "create", mock: () => vi.mocked(api.createSite), run: (store: Store, name: string) => store.createSite(payload(name)), result: (name: string): unknown => created(name), busy: "creating" as const },
  { name: "import", mock: () => vi.mocked(api.importSites), run: (store: Store, _name: string) => store.importExistingSites(), result: (name: string): unknown => [site(name)], busy: "importing" as const },
  { name: "start", mock: () => vi.mocked(api.startSite), run: (store: Store, name: string) => store.startSite(name), result: (name: string): unknown => site(name), busy: "busySiteName" as const },
  { name: "stop", mock: () => vi.mocked(api.stopSite), run: (store: Store, name: string) => store.stopSite(name), result: (name: string): unknown => site(name, "Stopped"), busy: "busySiteName" as const },
  { name: "delete", mock: () => vi.mocked(api.deleteSite), run: (store: Store, name: string) => store.deleteSite(name), result: (name: string): unknown => deleted(name), busy: "busySiteName" as const },
  { name: "addBinding", mock: () => vi.mocked(api.addBinding), run: (store: Store, name: string) => store.addBinding(name, binding), result: (name: string): unknown => ({ ...site(name), bindings: [{ ...binding, port: 8080, bindingInformation: "*:8080:a.test" }] }), busy: "busyBindingKey" as const },
  { name: "removeBinding", mock: () => vi.mocked(api.removeBinding), run: (store: Store, name: string) => store.removeBinding(name, binding), result: (name: string): unknown => ({ ...site(name), bindings: [] }), busy: "busyBindingKey" as const },
  { name: "defaults", mock: () => vi.mocked(api.ensureSiteDefaults), run: (store: Store, name: string) => store.prepareSiteDefaults(name), result: (name: string): unknown => defaults(name), busy: "busySiteName" as const }
];
beforeEach(() => {
  setActivePinia(createPinia()); shared.epoch = 0;
  vi.mocked(api.getSites).mockResolvedValue([site("a"), site("b")]); vi.mocked(api.getSite).mockImplementation(async name => site(name));
  for (const operation of operations) operation.mock().mockImplementation((async (arg: string | api.CreateSitePayload) => operation.result(typeof arg === "string" ? arg : arg?.name ?? "a")) as never);
});
afterEach(() => vi.resetAllMocks());
function initial() { const store = useSitesStore(); store.sites = [site("a", "Stopped")]; store.loaded = true; store.selectedSiteName = "a"; return store; }
for (const operation of operations) {
  it(`preserves normal ${operation.name} results and releases busy state without a redundant scan`, async () => {
    const store = initial(); const value = operation.result("a"); operation.mock().mockResolvedValueOnce(value as never);
    expect(await operation.run(store, "a")).toEqual(value); expect(api.getSites).not.toHaveBeenCalled(); expect(store.loaded).toBe(true); expect(store[operation.busy]).toBe(typeof store[operation.busy] === "boolean" ? false : "");
    if (operation.name === "delete") expect(store.sites).toEqual([]);
    else if (operation.name === "create") expect(store.sites).toEqual([created("a").site]);
    else if (operation.name === "import") expect(store.sites).toEqual(value);
    else if (operation.name !== "defaults") expect(store.sites).toEqual([value]);
  });
  it(`an older list cannot undo ${operation.name} or change its selection`, async () => {
    const store = initial(), pending = deferred<api.SiteRecord[]>(); const expected = operation.name === "delete" ? [] : operation.name === "create" ? [site("a", "Stopped"), site("b")] : operation.name === "defaults" ? [site("a")] : operation.name === "import" ? [site("b")] : [operation.result("a") as api.SiteRecord];
    vi.mocked(api.getSites).mockReturnValueOnce(pending.promise).mockResolvedValue(expected); const read = store.fetchSites();
    await operation.run(store, operation.name === "create" || operation.name === "import" ? "b" : "a"); const selected = store.selectedSiteName;
    pending.resolve([site("a", "old")]); await read; expect(store.sites).toEqual(expected); expect(store.selectedSiteName).toBe(selected); expect(store.loading).toBe(false); expect(store.loaded).toBe(true);
  });
  it(`old-session ${operation.name} cannot publish data or clear a new operation`, async () => {
    const store = initial(), old = deferred<never>(), next = deferred<never>(); operation.mock().mockReturnValueOnce(old.promise).mockReturnValueOnce(next.promise);
    const obsolete = operation.run(store, "a").catch(error => error); shared.epoch++; store.$reset(); const current = operation.run(store, "b"); const busy = store[operation.busy];
    old.resolve(operation.result("a") as never); expect(isCancel(await obsolete)).toBe(true); expect(store.sites).toEqual([]); expect(store.selectedSiteName).toBe(""); expect(store[operation.busy]).toBe(busy);
    next.resolve(operation.result("b") as never); await current;
  });
  it(`an earlier ${operation.name} cannot clear a later operation's busy state`, async () => {
    const store = initial(), old = deferred<never>(), next = deferred<never>(); operation.mock().mockReturnValueOnce(old.promise).mockReturnValueOnce(next.promise);
    const first = operation.run(store, "a"), second = operation.run(store, "b"), busy = store[operation.busy]; old.resolve(operation.result("a") as never); await first; expect(store[operation.busy]).toBe(busy);
    next.resolve(operation.result("b") as never); await second; expect(store[operation.busy]).toBe(typeof busy === "boolean" ? false : "");
  });
  it(`preserves the primary ${operation.name} failure when reconciliation also fails`, async () => {
    const store = initial(), error = new Error("primary failure"); operation.mock().mockRejectedValueOnce(error); vi.mocked(api.getSites).mockRejectedValueOnce(new Error("inspection failed"));
    await expect(operation.run(store, "a")).rejects.toBe(error); expect(store.loaded).toBe(false); expect(store.loading).toBe(false);
  });
}
it("keeps the creation poll visible while preventing its late reply from erasing the completed site", async () => {
  const store = initial(), create = deferred<api.SiteCreationResult>(), read = deferred<api.SiteRecord[]>(); vi.mocked(api.createSite).mockReturnValueOnce(create.promise); const work = store.createSite(payload("b"));
  vi.mocked(api.getSites).mockResolvedValueOnce([site("a"), site("b")]); await store.fetchSites(); expect(store.sites.some(item => item.name === "b")).toBe(true); expect(store.creating).toBe(true);
  vi.mocked(api.getSites).mockReturnValueOnce(read.promise); const late = store.fetchSites(); create.resolve(created("b")); await work; read.resolve([site("a")]); await late; expect(store.sites.some(item => item.name === "b")).toBe(true); expect(store.selectedSiteName).toBe("b"); expect(store.loaded).toBe(true);
});
it("keeps valid updates to different sites when their responses finish out of order", async () => {
  const store = initial(), a = deferred<api.SiteRecord>(), b = deferred<api.SiteRecord>(); store.sites.push(site("b", "Stopped")); vi.mocked(api.startSite).mockReturnValueOnce(a.promise).mockReturnValueOnce(b.promise);
  const first = store.startSite("a"), second = store.startSite("b"); b.resolve(site("b")); await second; expect(store.sites.find(item => item.name === "b")?.state).toBe("Started"); expect(store.busySiteName).toBe("a");
  a.resolve(site("a")); await first; expect(store.sites.map(item => item.state)).toEqual(["Started", "Started"]); expect(api.getSites).toHaveBeenCalledOnce();
});
it("reconciles overlapping same-site writes from a final current read", async () => {
  const store = initial(), start = deferred<api.SiteRecord>(), stop = deferred<api.SiteRecord>(); vi.mocked(api.startSite).mockReturnValueOnce(start.promise); vi.mocked(api.stopSite).mockReturnValueOnce(stop.promise); vi.mocked(api.getSites).mockResolvedValue([site("a", "Stopped")]);
  const first = store.startSite("a"), second = store.stopSite("a"); stop.resolve(site("a", "Stopped")); await second; start.resolve(site("a", "Started")); await first; expect(api.getSites).toHaveBeenCalledOnce(); expect(store.sites[0].state).toBe("Stopped");
});
it("does not let an overlapping import drop a completed new site", async () => {
  const store = initial(), pending = deferred<api.SiteRecord[]>(); vi.mocked(api.importSites).mockReturnValueOnce(pending.promise); const importing = store.importExistingSites(); await store.createSite(payload("b")); expect(store.sites.some(item => item.name === "b")).toBe(true);
  pending.resolve([site("a")]); await importing; expect(store.sites.map(item => item.name)).toEqual(["a", "b"]); expect(api.getSites).toHaveBeenCalledOnce();
});
it("does not switch the user's newer selection after an earlier creation completes", async () => {
  const store = initial(), pending = deferred<api.SiteCreationResult>(); vi.mocked(api.createSite).mockReturnValueOnce(pending.promise); const work = store.createSite(payload("b")); store.selectSite("a"); pending.resolve(created("b")); await work; expect(store.selectedSiteName).toBe("a"); expect(store.sites.some(item => item.name === "b")).toBe(true);
});
it("keeps the latest same-site detail read", async () => {
  const store = initial(), pending = deferred<api.SiteRecord>(); vi.mocked(api.getSite).mockReturnValueOnce(pending.promise).mockResolvedValueOnce(site("a", "new")); const old = store.fetchSite("a"); await store.fetchSite("a"); pending.resolve(site("a", "old")); await old; expect(store.sites[0].state).toBe("new");
});
it("keeps both distinct detail reads without selecting the older request's target", async () => {
  const store = initial(), pending = deferred<api.SiteRecord>(); vi.mocked(api.getSite).mockReturnValueOnce(pending.promise).mockResolvedValueOnce(site("b")); const old = store.fetchSite("a"); await store.fetchSite("b"); pending.resolve(site("a")); await old; expect(store.sites.map(item => item.name)).toEqual(["a", "b"]); expect(store.selectedSiteName).toBe("b");
});
it("an old detail response cannot resurrect a deleted site", async () => {
  const store = initial(), pending = deferred<api.SiteRecord>(); vi.mocked(api.getSite).mockReturnValueOnce(pending.promise); const old = store.fetchSite("a"); await store.deleteSite("a"); pending.resolve(site("a")); await old; expect(store.sites).toEqual([]);
});
it("an old-session detail response cannot publish data or choose a site", async () => {
  const store = initial(), pending = deferred<api.SiteRecord>(); vi.mocked(api.getSite).mockReturnValueOnce(pending.promise); const old = store.fetchSite("a").catch(error => error); shared.epoch++; store.$reset(); pending.resolve(site("a")); expect(isCancel(await old)).toBe(true); expect(store.sites).toEqual([]); expect(store.selectedSiteName).toBe("");
});
it("a detail request cannot override a newer explicit selection", async () => {
  const store = initial(), pending = deferred<api.SiteRecord>(); vi.mocked(api.getSite).mockReturnValueOnce(pending.promise); const old = store.fetchSite("a"); store.selectSite("b"); pending.resolve(site("a")); await old; expect(store.selectedSiteName).toBe("b");
});
it("releases an invalidated read's loading flag while retaining the active writer", async () => {
  const store = initial(), pending = deferred<api.SiteRecord[]>(), write = deferred<api.SiteRecord>(); vi.mocked(api.getSites).mockReturnValueOnce(pending.promise); vi.mocked(api.startSite).mockReturnValueOnce(write.promise); const read = store.fetchSites(), work = store.startSite("a"); pending.resolve([site("a", "old")]); await read; expect(store.loading).toBe(false); expect(store.busySiteName).toBe("a"); expect(store.sites[0].state).not.toBe("old"); write.resolve(site("a")); await work;
});
it("does not expose a successful write response to an obsolete session after its reconciliation wait", async () => {
  const store = initial(), oldRead = deferred<api.SiteRecord[]>(), finalRead = deferred<api.SiteRecord[]>(); vi.mocked(api.getSites).mockReturnValueOnce(oldRead.promise).mockReturnValueOnce(finalRead.promise); const read = store.fetchSites(); const writing = store.createSite(payload("b")).catch(error => error);
  for (let i = 0; i < 10; i++) await Promise.resolve(); shared.epoch++; store.$reset(); oldRead.resolve([site("a")]); finalRead.resolve([site("b")]); await read.catch(() => undefined); expect(isCancel(await writing)).toBe(true); expect(store.sites).toEqual([]);
});
it("preserves creation secrets and warnings when the follow-up inspection fails", async () => {
  const store = initial(), old = deferred<api.SiteRecord[]>(); vi.mocked(api.getSites).mockReturnValueOnce(old.promise).mockRejectedValueOnce(new Error("inspection failed"));
  const read = store.fetchSites(), response = { ...created("b"), warnings: ["fixture warning"], generatedCredentials: [{ service: "ftp", userName: "fixture", password: "FixtureOnly!" }] }; vi.mocked(api.createSite).mockResolvedValueOnce(response);
  expect(await store.createSite(payload("b"))).toEqual(response); expect(store.loaded).toBe(false); old.resolve([site("a")]); await read; expect(store.loaded).toBe(false); expect(store.sites.some(item => item.name === "b")).toBe(true);
});
it("does not let an older list overwrite a newer detail measurement", async () => {
  const store = initial(), old = deferred<api.SiteRecord[]>(); vi.mocked(api.getSites).mockReturnValueOnce(old.promise).mockResolvedValue([site("a", "new")]); vi.mocked(api.getSite).mockResolvedValueOnce(site("a", "new"));
  const read = store.fetchSites(); await store.fetchSite("a"); old.resolve([site("a", "old")]); await read; expect(store.sites[0].state).toBe("new"); expect(store.loaded).toBe(true); expect(store.loading).toBe(false);
});
it("does not let an older detail overwrite a newer completed full list", async () => {
  const store = initial(), old = deferred<api.SiteRecord>(); vi.mocked(api.getSite).mockReturnValueOnce(old.promise); vi.mocked(api.getSites).mockResolvedValueOnce([site("a", "new")]); const read = store.fetchSite("a"); await store.fetchSites(); old.resolve(site("a", "old")); await read; expect(store.sites[0].state).toBe("new");
});
it("old-session list failures are cancellations too", async () => {
  const store = initial(), old = deferred<api.SiteRecord[]>(); vi.mocked(api.getSites).mockReturnValueOnce(old.promise); const read = store.fetchSites().catch(error => error); shared.epoch++; store.$reset(); old.reject(new Error("old failure")); expect(isCancel(await read)).toBe(true); expect(store.loading).toBe(false);
});
it("an older reconciliation cannot clear a later read's loading state", async () => {
  const store = initial(), old = deferred<api.SiteRecord[]>(), reconciliation = deferred<api.SiteRecord[]>(), newest = deferred<api.SiteRecord[]>();
  vi.mocked(api.getSites).mockReturnValueOnce(old.promise).mockReturnValueOnce(reconciliation.promise).mockReturnValueOnce(newest.promise); const firstRead = store.fetchSites(), write = store.createSite(payload("b")); for (let i = 0; i < 10; i++) await Promise.resolve();
  const currentRead = store.fetchSites(); reconciliation.resolve([site("a"), site("b")]); await write; expect(store.loading).toBe(true); newest.resolve([site("a"), site("b")]); await currentRead; old.resolve([site("a")]); await firstRead; expect(store.loading).toBe(false); expect(store.loaded).toBe(true);
});
it("a synchronous direct update reconciles an overlapping full read without leaving the list unverified", async () => {
  const store = initial(), old = deferred<api.SiteRecord[]>(); vi.mocked(api.getSites).mockReturnValueOnce(old.promise).mockResolvedValue([site("a", "latest")]);
  const read = store.fetchSites(); expect(store.upsertSite(site("a", "latest"))).toBeUndefined(); old.resolve([site("a", "old")]); await read; for (let i = 0; i < 10; i++) await Promise.resolve();
  expect(store.sites[0].state).toBe("latest"); expect(store.loaded).toBe(true); expect(store.loading).toBe(false); expect(api.getSites).toHaveBeenCalledTimes(2);
});
it("a direct update during mutation forces final reconciliation instead of trusting the earlier snapshot", async () => {
  const store = initial(), pending = deferred<api.SiteRecord>(); vi.mocked(api.startSite).mockReturnValueOnce(pending.promise); vi.mocked(api.getSites).mockResolvedValue([site("a", "latest")]);
  const write = store.startSite("a"); store.upsertSite(site("a", "latest")); pending.resolve(site("a", "earlier")); await write;
  expect(store.sites[0].state).toBe("latest"); expect(api.getSites).toHaveBeenCalledOnce(); expect(store.loaded).toBe(true);
});
it("keeps a standalone direct update synchronous and does not add an unnecessary scan", () => {
  const store = initial(); expect(store.upsertSite(site("a", "latest"))).toBeUndefined(); expect(store.sites[0].state).toBe("latest"); expect(store.loaded).toBe(true); expect(api.getSites).not.toHaveBeenCalled();
});
