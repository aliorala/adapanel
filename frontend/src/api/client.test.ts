import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import axios, { AxiosError, type AxiosResponse, type InternalAxiosRequestConfig } from "axios";
import { createPinia, setActivePinia } from "pinia";
import { apiClient, invalidateSessionRequests, setSessionExpiredHandler } from "./client";
import { useSitesStore } from "../stores/sites";
import { resetAllStores } from "../stores/resetAll";

const originalAdapter = apiClient.defaults.adapter;
beforeEach(() => {
  vi.stubGlobal("window", { location: { pathname: "/secret-entry" } });
  setActivePinia(createPinia());
  invalidateSessionRequests();
});
afterEach(() => { apiClient.defaults.adapter = originalAdapter; setSessionExpiredHandler(null); vi.unstubAllGlobals(); });
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
