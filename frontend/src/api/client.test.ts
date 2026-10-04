import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import axios, { AxiosError, type AxiosResponse, type InternalAxiosRequestConfig } from "axios";
import { createPinia, setActivePinia } from "pinia";
import { apiClient, clearCsrfToken, invalidateSessionRequests, setReadOnlyMode, setSessionExpiredHandler } from "./client";
import { useSitesStore } from "../stores/sites";
import { resetAllStores } from "../stores/resetAll";

const originalAdapter = apiClient.defaults.adapter;
beforeEach(() => {
  vi.stubGlobal("window", { location: { pathname: "/secret-entry" } });
  setActivePinia(createPinia());
  invalidateSessionRequests();
});
afterEach(() => { apiClient.defaults.adapter = originalAdapter; setSessionExpiredHandler(null); invalidateSessionRequests(); vi.unstubAllGlobals(); vi.useRealTimers(); });
function deferred() {
  let resolve!: (value: AxiosResponse) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<AxiosResponse>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
describe("session isolation", () => {
  it("does not repopulate a reset site store with a delayed previous user's response", async () => {
    const delayed = deferred();
    let config!: InternalAxiosRequestConfig;
    apiClient.defaults.adapter = async (request) => { config = request; return delayed.promise; };
    const sites = useSitesStore();
    const result = sites.fetchSites().catch((error) => error);
    await vi.waitFor(() => expect(config).toBeDefined());
    resetAllStores();
    delayed.resolve({ config, status: 200, statusText: "OK", headers: {}, data: { data: [{ name: "private-a.example" }] } });
    expect(axios.isCancel(await result)).toBe(true);
    expect(sites.sites).toEqual([]);
    expect(sites.selectedSiteName).toBe("");
  });
  it("a delayed old 401 cannot expire the new session", async () => {
    const delayed = deferred();
    const expired = vi.fn();
    setSessionExpiredHandler(expired);
    let config!: InternalAxiosRequestConfig;
    apiClient.defaults.adapter = async (request) => { config = request; return delayed.promise; };
    const result = apiClient.get("/sites").catch((error) => error);
    await vi.waitFor(() => expect(config).toBeDefined());
    invalidateSessionRequests();
    delayed.reject(new AxiosError("Unauthorized", "ERR_BAD_REQUEST", config, null, { config, status: 401, statusText: "Unauthorized", headers: {}, data: {} }));
    expect(axios.isCancel(await result)).toBe(true);
    expect(expired).not.toHaveBeenCalled();
  });
  it("still expires the current session on a protected 401", async () => {
    const expired = vi.fn(resetAllStores);
    setSessionExpiredHandler(expired);
    apiClient.defaults.adapter = async (config) => { throw new AxiosError("Unauthorized", "ERR_BAD_REQUEST", config, null, { config, status: 401, statusText: "Unauthorized", headers: {}, data: {} }); };
    await expect(apiClient.get("/sites")).rejects.toMatchObject({ response: { status: 401 } });
    expect(expired).toHaveBeenCalledOnce();
  });
});

function csrfResponse(token: string) {
  return new Response(JSON.stringify({ data: { requestToken: token } }));
}
function pendingCsrf() {
  let resolve!: (response: Response) => void;
  const promise = new Promise<Response>(done => { resolve = done; });
  return { promise, resolve };
}
function recordingAdapter() {
  const adapter = vi.fn(async (config: InternalAxiosRequestConfig) => ({ config, status: 200, statusText: "OK", headers: {}, data: {} }));
  apiClient.defaults.adapter = adapter;
  return adapter;
}

it("does not dispatch or cache a CSRF response invalidated while it was loading", async () => {
  const old = pendingCsrf();
  const fetcher = vi.fn().mockReturnValueOnce(old.promise).mockResolvedValue(csrfResponse("new-token"));
  vi.stubGlobal("fetch", fetcher);
  const adapter = recordingAdapter();
  const obsolete = apiClient.post("/sites", {}).catch(error => error);
  await vi.waitFor(() => expect(fetcher).toHaveBeenCalledOnce());
  clearCsrfToken();
  await apiClient.post("/sites", {});
  old.resolve(csrfResponse("old-token"));
  expect(axios.isCancel(await obsolete)).toBe(true);
  await apiClient.post("/sites", {});
  expect(adapter).toHaveBeenCalledTimes(2);
  expect(adapter.mock.calls.map(([config]) => config.headers.get("X-CSRF-TOKEN"))).toEqual(["new-token", "new-token"]);
  expect(fetcher).toHaveBeenCalledTimes(2);
});

it("rechecks read-only mode after waiting for CSRF", async () => {
  const pending = pendingCsrf(); const fetcher = vi.fn().mockReturnValue(pending.promise);
  vi.stubGlobal("fetch", fetcher); const adapter = recordingAdapter();
  const result = apiClient.post("/sites", {}).catch(error => error);
  await vi.waitFor(() => expect(fetcher).toHaveBeenCalledOnce());
  setReadOnlyMode("subscription.suspended_readonly");
  pending.resolve(csrfResponse("token"));
  expect(await result).toMatchObject({ response: { status: 403, data: { message: "subscription.suspended_readonly" } } });
  expect(adapter).not.toHaveBeenCalled();
});

it("cancels a request waiting for CSRF without cancelling another waiting request", async () => {
  const pending = pendingCsrf(); const fetcher = vi.fn().mockReturnValue(pending.promise);
  vi.stubGlobal("fetch", fetcher); const adapter = recordingAdapter();
  const controller = new AbortController();
  let cancelled = false;
  const first = apiClient.post("/sites", {}, { signal: controller.signal }).catch(error => { cancelled = axios.isCancel(error); });
  const second = apiClient.post("/sites", {});
  await vi.waitFor(() => expect(fetcher).toHaveBeenCalledOnce());
  controller.abort();
  try {
    await vi.waitFor(() => expect(cancelled).toBe(true), { timeout: 200 });
    expect(adapter).not.toHaveBeenCalled();
  } finally {
    pending.resolve(csrfResponse("shared-token"));
    await Promise.all([first, second]);
  }
  expect(adapter).toHaveBeenCalledOnce();
});

it("times out a stalled CSRF read without dispatching a mutation and permits a fresh retry", async () => {
  vi.useFakeTimers();
  const pending = pendingCsrf(); const fetcher = vi.fn().mockReturnValueOnce(pending.promise).mockResolvedValue(csrfResponse("retry-token"));
  vi.stubGlobal("fetch", fetcher); const adapter = recordingAdapter();
  let error: unknown;
  const request = apiClient.post("/sites", {}).catch(failure => { error = failure; });
  await vi.advanceTimersByTimeAsync(15_001);
  try {
    expect(error).toMatchObject({ code: "ETIMEDOUT" }); expect(adapter).not.toHaveBeenCalled();
    await apiClient.post("/sites", {}); expect(adapter).toHaveBeenCalledOnce();
    expect(adapter.mock.calls[0][0].headers.get("X-CSRF-TOKEN")).toBe("retry-token");
  } finally { pending.resolve(csrfResponse("obsolete-token")); await request; }
});

it.each([
  [503, JSON.stringify({ message: "common.server_error", data: null })],
  [200, JSON.stringify({ data: {} })],
  [200, "<html>proxy error</html>"]
])("does not dispatch a mutation with an invalid CSRF response (%s, %s)", async (status, body) => {
  vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(body, { status })));
  const adapter = recordingAdapter();
  await expect(apiClient.post("/sites", {})).rejects.toBeInstanceOf(AxiosError);
  expect(adapter).not.toHaveBeenCalled();
});

it("reports CSRF network failure without sending a second tokenless request", async () => {
  vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new TypeError("Failed to fetch")));
  const adapter = recordingAdapter(); await expect(apiClient.post("/sites", {})).rejects.toMatchObject({ code: "ERR_NETWORK" });
  expect(adapter).not.toHaveBeenCalled();
});

it("also bounds CSRF response-body reads and does not cache their late tokens", async () => {
  vi.useFakeTimers();
  let finishBody!: (value: unknown) => void;
  const body = new Promise(resolve => { finishBody = resolve; });
  const fetcher = vi.fn().mockResolvedValueOnce({ ok: true, json: () => body }).mockResolvedValue(csrfResponse("fresh-token"));
  vi.stubGlobal("fetch", fetcher); const adapter = recordingAdapter();
  let failure: unknown;
  const waiting = apiClient.post("/sites", {}).catch(error => { failure = error; });
  await vi.advanceTimersByTimeAsync(15_001);
  try {
    expect(failure).toMatchObject({ code: "ETIMEDOUT" }); expect(adapter).not.toHaveBeenCalled();
    await apiClient.post("/sites", {});
    finishBody({ data: { requestToken: "late-body-token" } }); await waiting;
    await apiClient.post("/sites", {});
    expect(adapter.mock.calls.map(([config]) => config.headers.get("X-CSRF-TOKEN"))).toEqual(["fresh-token", "fresh-token"]);
  } finally { finishBody({ data: { requestToken: "late-body-token" } }); await waiting; }
});

describe("suspended file inspection", () => {
  it.each(["/files/search", "/files/size"])("permits only the exact POST read %s with a CSRF token", async url => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(csrfResponse("read-token")));
    const adapter = recordingAdapter(); setReadOnlyMode("subscription.suspended_readonly");
    await apiClient.post(url, { path: "sites/a.test", query: "index" });
    expect(adapter).toHaveBeenCalledOnce();
    expect(adapter.mock.calls[0][0].url).toBe(url);
    expect(adapter.mock.calls[0][0].headers.get("X-CSRF-TOKEN")).toBe("read-token");
  });
  it.each([
    ["put", "/files/search"], ["delete", "/files/size"], ["patch", "/files/search"],
    ["post", "/prefix/files/search"], ["post", "/files/search/extra"], ["post", "/files/size-extra"],
    ["post", "/files/extract"], ["post", "/files/mkdir"], ["post", "/files/acl"]
  ])("continues rejecting %s %s before CSRF or network dispatch", async (method, url) => {
    const fetcher = vi.fn().mockResolvedValue(csrfResponse("never-used")); vi.stubGlobal("fetch", fetcher);
    const adapter = recordingAdapter(); setReadOnlyMode("subscription.suspended_readonly");
    await expect(apiClient.request({ method, url, data: {} })).rejects.toMatchObject({ response: { status: 403 } });
    expect(adapter).not.toHaveBeenCalled(); expect(fetcher).not.toHaveBeenCalled();
  });
  it.each(["/files/search", "/files/size"])("still cancels an old session's %s while it waits for CSRF", async url => {
    const token = pendingCsrf(), fetcher = vi.fn().mockReturnValue(token.promise); vi.stubGlobal("fetch", fetcher);
    const adapter = recordingAdapter();
    const pending = apiClient.post(url, { path: "sites/a.test" }).catch(error => error);
    await vi.waitFor(() => expect(fetcher).toHaveBeenCalledOnce());
    invalidateSessionRequests(); setReadOnlyMode("subscription.suspended_readonly"); token.resolve(csrfResponse("obsolete"));
    expect(axios.isCancel(await pending)).toBe(true); expect(adapter).not.toHaveBeenCalled();
  });
  it("does not let the allowed file read bypass CSRF failure", async () => {
    const fetcher = vi.fn().mockResolvedValue(new Response("invalid token", { status: 503 })); vi.stubGlobal("fetch", fetcher);
    const adapter = recordingAdapter(); setReadOnlyMode("subscription.suspended_readonly");
    await expect(apiClient.post("/files/search", { path: "sites/a.test", query: "index" })).rejects.toMatchObject({ response: { status: 503 } });
    expect(fetcher).toHaveBeenCalledOnce(); expect(adapter).not.toHaveBeenCalled();
  });
});
