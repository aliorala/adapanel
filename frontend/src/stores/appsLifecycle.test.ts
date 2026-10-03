import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { isCancel } from "axios";
import * as api from "../api/apps";
import { invalidateSessionRequests } from "../api/client";
import { useAppsStore } from "./apps";

vi.mock("../api/apps", () => ({ getSiteAppRuntimeConfiguration: vi.fn(), getSiteAppRuntimeInventory: vi.fn(), hostNodeApp: vi.fn(), hostPythonApp: vi.fn() }));
function configuration(siteName = "a", startupFile = "server.js", runtimeType = "node"): api.AppRuntimeSiteConfiguration {
  return { siteName, startupFile, runtimeType, componentId: "nodejs-lts", processPath: "node.exe", workingDirectory: ".", arguments: "", webConfigPath: "", logsDirectory: "", isEnabled: true, hasHttpPlatformHandler: true, managedRuntimeDisabled: true, environmentVariables: [] };
}
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
const payload = () => ({ startupFile: "new.js", workingDirectory: ".", arguments: "", componentId: "nodejs-lts", environmentVariables: ["MODE=new"] });
beforeEach(() => {
  invalidateSessionRequests(); setActivePinia(createPinia());
  vi.mocked(api.getSiteAppRuntimeConfiguration).mockImplementation(async site => configuration(site));
  vi.mocked(api.hostNodeApp).mockImplementation(async (site, input) => ({ ...configuration(site, input.startupFile), environmentVariables: [...input.environmentVariables] }));
  vi.mocked(api.hostPythonApp).mockImplementation(async (site, input) => configuration(site, input.startupFile, "python"));
});
afterEach(() => { invalidateSessionRequests(); vi.resetAllMocks(); });

it("keeps the newer same-site read and its loading owner", async () => {
  const store = useAppsStore(), old = deferred<api.AppRuntimeSiteConfiguration>(), recent = deferred<api.AppRuntimeSiteConfiguration>();
  vi.mocked(api.getSiteAppRuntimeConfiguration).mockReturnValueOnce(old.promise).mockReturnValueOnce(recent.promise);
  const first = store.fetchConfiguration("a").catch(e => e), second = store.fetchConfiguration("a");
  old.resolve(configuration("a", "old.js")); await first;
  expect(store.loadingSiteName).toBe("a"); expect(store.getConfiguration("a")).toBeNull();
  recent.resolve(configuration("a", "new.js")); await second;
  expect(store.getConfiguration("a")?.startupFile).toBe("new.js"); expect(store.loadingSiteName).toBe("");
});
it("does not overwrite a newer read with a late old read", async () => {
  const store = useAppsStore(), old = deferred<api.AppRuntimeSiteConfiguration>();
  vi.mocked(api.getSiteAppRuntimeConfiguration).mockReturnValueOnce(old.promise);
  const first = store.fetchConfiguration("a").catch(e => e); await store.fetchConfiguration("a");
  old.resolve(configuration("a", "old.js")); await first;
  expect(store.getConfiguration("a")?.startupFile).toBe("server.js");
});
it("keeps independent sites readable when their requests overlap", async () => {
  const store = useAppsStore(), old = deferred<api.AppRuntimeSiteConfiguration>();
  vi.mocked(api.getSiteAppRuntimeConfiguration).mockReturnValueOnce(old.promise);
  const first = store.fetchConfiguration("a"); await store.fetchConfiguration("b"); old.resolve(configuration()); await first;
  expect(store.getConfiguration("a")?.siteName).toBe("a"); expect(store.getConfiguration("b")?.siteName).toBe("b");
});

for (const runtime of ["node", "python"] as const) {
  const run = (store: ReturnType<typeof useAppsStore>, site = "a", input = payload()) => runtime === "node" ? store.configureNode(site, input) : store.configurePython(site, input);
  const action = runtime === "node" ? api.hostNodeApp : api.hostPythonApp;
  it(`${runtime} completion invalidates a GET begun before the write`, async () => {
    const store = useAppsStore(), old = deferred<api.AppRuntimeSiteConfiguration>();
    vi.mocked(api.getSiteAppRuntimeConfiguration).mockReturnValueOnce(old.promise);
    const reading = store.fetchConfiguration("a").catch(e => e); await run(store);
    old.resolve(configuration("a", "old.js")); await reading;
    expect(store.getConfiguration("a")?.startupFile).toBe("new.js");
  });
  it(`${runtime} completion invalidates a GET begun during the write`, async () => {
    const store = useAppsStore(), write = deferred<api.AppRuntimeSiteConfiguration>(), read = deferred<api.AppRuntimeSiteConfiguration>();
    vi.mocked(action).mockReturnValueOnce(write.promise); vi.mocked(api.getSiteAppRuntimeConfiguration).mockReturnValueOnce(read.promise);
    const saving = run(store), reading = store.fetchConfiguration("a").catch(e => e);
    write.resolve(configuration("a", "new.js", runtime)); await saving;
    read.resolve(configuration("a", "old.js")); await reading;
    expect(store.getConfiguration("a")?.startupFile).toBe("new.js");
  });
  it(`${runtime} freezes the submitted environment array`, async () => {
    const store = useAppsStore(), pending = deferred<api.AppRuntimeSiteConfiguration>(), input = payload();
    vi.mocked(action).mockReturnValueOnce(pending.promise);
    const saving = run(store, "a", input); input.environmentVariables.push("NEXT=draft"); input.startupFile = "next.js";
    const submitted = vi.mocked(action).mock.calls[0]![1];
    pending.resolve(configuration()); await saving;
    expect(submitted.environmentVariables).toEqual(["MODE=new"]); expect(submitted.startupFile).toBe("new.js");
  });
  it(`${runtime} prevents duplicate and competing runtime writes`, async () => {
    const store = useAppsStore(), pending = deferred<api.AppRuntimeSiteConfiguration>(); vi.mocked(action).mockReturnValueOnce(pending.promise);
    const saving = run(store); await run(store).catch(() => undefined);
    await (runtime === "node" ? store.configurePython("a", payload()) : store.configureNode("a", payload())).catch(() => undefined);
    expect(action).toHaveBeenCalledOnce(); expect(runtime === "node" ? api.hostPythonApp : api.hostNodeApp).not.toHaveBeenCalled();
    pending.resolve(configuration()); await saving;
  });
}

for (const kind of ["read", "node", "python"] as const) {
  const action = kind === "read" ? api.getSiteAppRuntimeConfiguration : kind === "node" ? api.hostNodeApp : api.hostPythonApp;
  const run = (store: ReturnType<typeof useAppsStore>) => kind === "read" ? store.fetchConfiguration("a") : kind === "node" ? store.configureNode("a", payload()) : store.configurePython("a", payload());
  it.each(["success", "failure"])(`${kind} cannot publish or clear the next session's operation (%s)`, async outcome => {
    const store = useAppsStore(), old = deferred<api.AppRuntimeSiteConfiguration>(), recent = deferred<api.AppRuntimeSiteConfiguration>();
    vi.mocked(action).mockReturnValueOnce(old.promise).mockReturnValueOnce(recent.promise);
    const first = run(store).catch(e => e); invalidateSessionRequests(); store.$reset(); const second = run(store);
    if (outcome === "success") old.resolve(configuration("a", "secret-old.js")); else old.reject(new Error("old failure"));
    const result = await first;
    expect(isCancel(result)).toBe(true); expect(store.getConfiguration("a")).toBeNull();
    expect(kind === "read" ? store.loadingSiteName : kind === "node" ? store.nodeBusySiteName : store.pythonBusySiteName).toBe("a");
    recent.resolve(configuration("a", "new-session.js")); await second;
    expect(store.getConfiguration("a")?.startupFile).toBe("new-session.js");
  });
}
