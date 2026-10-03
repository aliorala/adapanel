import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { isCancel } from "axios";
import { useSitesStore } from "./sites";
import { getSites, type SiteRecord } from "../api/sites";
const shared = vi.hoisted(() => ({ epoch: 0 }));
vi.mock("../api/client", async original => ({ ...await original<object>(), getSessionEpoch: () => shared.epoch }));
vi.mock("../api/sites", async original => ({ ...await original<object>(), getSites: vi.fn() }));
const site = (name: string) => ({ name } as SiteRecord);
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
beforeEach(() => { setActivePinia(createPinia()); shared.epoch = 0; });
afterEach(() => vi.resetAllMocks());
it("ignores a slow earlier site list and preserves its newer selection", async () => {
  const store = useSitesStore(), old = deferred<SiteRecord[]>(); vi.mocked(getSites).mockReturnValueOnce(old.promise).mockResolvedValueOnce([site("new")]);
  const pending = store.fetchSites(); await store.fetchSites(); old.resolve([site("old")]); await pending;
  expect(store.sites.map(s => s.name)).toEqual(["new"]); expect(store.selectedSiteName).toBe("new");
});
it("an old session cannot publish sites or select a site in the new session", async () => {
  const store = useSitesStore(), old = deferred<SiteRecord[]>(); vi.mocked(getSites).mockReturnValueOnce(old.promise); const pending = store.fetchSites().catch(e => e);
  shared.epoch++; store.$reset(); old.resolve([site("old")]); expect(isCancel(await pending)).toBe(true); expect(store.sites).toEqual([]); expect(store.selectedSiteName).toBe("");
});
it("an older read cannot clear a newer read's loading flag", async () => {
  const store = useSitesStore(), old = deferred<SiteRecord[]>(), next = deferred<SiteRecord[]>(); vi.mocked(getSites).mockReturnValueOnce(old.promise).mockReturnValueOnce(next.promise);
  const first = store.fetchSites(), second = store.fetchSites(); old.resolve([site("old")]); await first; expect(store.loading).toBe(true); next.resolve([site("next")]); await second; expect(store.loading).toBe(false);
});
it("a failed refresh leaves the last list unverified", async () => {
  const store = useSitesStore(); vi.mocked(getSites).mockResolvedValueOnce([site("old")]).mockRejectedValueOnce(new Error("offline")); await store.fetchSites(); await expect(store.fetchSites()).rejects.toThrow("offline"); expect(store.loaded).toBe(false);
});
it("preserves a user's still-valid selection on a successful read", async () => {
  const store = useSitesStore(); store.selectedSiteName = "b"; vi.mocked(getSites).mockResolvedValueOnce([site("b"), site("a")]); await store.fetchSites(); expect(store.sites.map(s => s.name)).toEqual(["a", "b"]); expect(store.selectedSiteName).toBe("b"); expect(store.loaded).toBe(true);
});
