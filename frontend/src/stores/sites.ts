import { defineStore } from "pinia";
import { toRaw } from "vue";
import { CanceledError } from "axios";
import { getSessionEpoch } from "../api/client";
import {
  addBinding,
  createSite,
  deleteSite,
  ensureSiteDefaults,
  getSite,
  getSites,
  importSites,
  removeBinding,
  startSite,
  stopSite,
  type CreateSitePayload,
  type SiteBindingPayload,
  type SiteRecord
} from "../api/sites";

function bindingKey(binding: SiteBindingPayload): string {
  return `${binding.protocol}|${binding.ipAddress}|${binding.port}|${binding.hostName}`;
}

function sortSites(sites: SiteRecord[]): SiteRecord[] {
  return [...sites].sort((left, right) => left.name.localeCompare(right.name));
}

type BusyKey = "creating" | "importing" | "busySiteName" | "busyBindingKey";
type SiteState = {
  sites: SiteRecord[]; selectedSiteName: string; loaded: boolean; loading: boolean;
  creating: boolean; importing: boolean; busySiteName: string; busyBindingKey: string;
  fetchSites(): Promise<SiteRecord[]>;
};
type Requests = {
  epoch: number; sequence: number; revision: number; listRead: number; publishedList: number;
  selection: number; importRevision: number; trustedBeforeWrites: boolean; reconcile: boolean;
  activeWrites: Set<number>; pending: Map<number, { busy: BusyKey; value: string }>;
  siteVersions: Map<string, number>; siteWrites: Map<string, number>; detailReads: Map<string, number>;
};
const requestsByStore = new WeakMap<object, Requests>();
function requestsFor(store: object): Requests {
  const key = toRaw(store);
  let requests = requestsByStore.get(key);
  const epoch = getSessionEpoch();
  if (!requests || requests.epoch !== epoch) {
    requests = { epoch, sequence: 0, revision: 0, listRead: 0, publishedList: 0, selection: 0, importRevision: 0,
      trustedBeforeWrites: false, reconcile: false, activeWrites: new Set(), pending: new Map(),
      siteVersions: new Map(), siteWrites: new Map(), detailReads: new Map() };
    requestsByStore.set(key, requests);
  }
  return requests;
}
function assertSession(requests: Requests) {
  if (requests.epoch !== getSessionEpoch()) throw new CanceledError("Session changed");
}
function advanceSite(requests: Requests, name: string) {
  requests.siteVersions.set(name, (requests.siteVersions.get(name) ?? 0) + 1);
}
function syncBusy(store: SiteState, requests: Requests) {
  // A completed newer operation can leave an older one in flight. Keep that one visible.
  const pending = [...requests.pending.values()];
  store.creating = pending.some(item => item.busy === "creating");
  store.importing = pending.some(item => item.busy === "importing");
  store.busySiteName = pending.filter(item => item.busy === "busySiteName").pop()?.value ?? "";
  store.busyBindingKey = pending.filter(item => item.busy === "busyBindingKey").pop()?.value ?? "";
}
function selectExisting(store: SiteState) {
  if (!store.sites.some(site => site.name === store.selectedSiteName)) store.selectedSiteName = store.sites[0]?.name ?? "";
}
function upsert(store: SiteState, site: SiteRecord) {
  store.sites = sortSites(store.sites.some(item => item.name === site.name)
    ? store.sites.map(item => item.name === site.name ? site : item) : [...store.sites, site]);
}

async function mutate<T>(store: SiteState, busy: BusyKey, target: string | null, value: string,
  call: () => Promise<T>, apply: (result: T, maySelect: boolean) => void, selects = false): Promise<T> {
  const requests = requestsFor(store), id = ++requests.sequence;
  if (requests.activeWrites.size === 0) {
    requests.trustedBeforeWrites = store.loaded;
    requests.reconcile = store.loading;
  } else requests.reconcile = true;
  const revision = ++requests.revision;
  if (target !== null) { advanceSite(requests, target); requests.siteWrites.set(target, id); }
  else requests.importRevision++;
  requests.activeWrites.add(id);
  requests.pending.set(id, { busy, value });
  if (selects) requests.selection = id;
  store.loaded = false;
  syncBusy(store, requests);
  try {
    const result = await call();
    assertSession(requests);
    // Single-site replies may coexist. A full import snapshot cannot replace changes
    // made by another overlapping write; the final list read resolves that overlap.
    if (target !== null ? requests.siteWrites.get(target) === id : requests.revision === revision) {
      apply(result, requests.selection === id);
      if (target === null) requests.trustedBeforeWrites = true;
    }
    return result;
  } catch (error) {
    assertSession(requests);
    requests.reconcile = true;
    throw error;
  } finally {
    if (requests.epoch === getSessionEpoch()) {
      requests.revision++;
      if (target !== null) advanceSite(requests, target);
      else requests.importRevision++;
      requests.activeWrites.delete(id);
      if (requests.activeWrites.size === 0) {
        const reconcile = requests.reconcile || !requests.trustedBeforeWrites;
        requests.reconcile = false;
        if (reconcile) {
          // Refresh failure must not discard credentials, partial-success diagnostics,
          // or the original mutation error. loaded remains false until a valid read.
          await store.fetchSites().catch(() => undefined);
        } else store.loaded = true;
      }
      if (requests.epoch === getSessionEpoch()) { requests.pending.delete(id); syncBusy(store, requests); }
    }
    // Logout can occur while the follow-up read is pending, after the write succeeded.
    assertSession(requests);
  }
}

export const useSitesStore = defineStore("sites", {
  state: () => ({
    sites: [] as SiteRecord[],
    selectedSiteName: "",
    loading: false,
    /*
     * Son liste ölçümü başarıyla okundu mu.
     *
     * NEDEN mağazada, ekranda değil: `sites` otuz ekranda okunuyor ve boş dizi iki AYRI şeyi
     * anlatıyor — "hiç site yok" ve "daha gelmedi/okunamadı". Bayrağı ekrana koysaydık her
     * ekran kendi sayacını tutar, aynı veriye bakan iki ekran farklı cümle kurardı.
     *
     * `loading`den AYRI: `loading` istek bitince (hata alsa da) false olur; boş dizi + false
     * "hiç siteniz yok" gibi KESİN bir cümleye dönüşürdü. Bu bayrak yalnız BAŞARIDA true olur.
     */
    loaded: false,
    creating: false,
    importing: false,
    busySiteName: "",
    busyBindingKey: ""
  }),
  getters: {
    selectedSite: (state) => state.sites.find((site) => site.name === state.selectedSiteName) ?? null
  },
  actions: {
    selectSite(siteName: string) {
      const requests = requestsFor(this);
      requests.selection = ++requests.sequence;
      this.selectedSiteName = siteName;
    },
    async fetchSites(): Promise<SiteRecord[]> {
      const requests = requestsFor(this), id = ++requests.sequence, revision = requests.revision;
      requests.listRead = id;
      const ownsLoading = () => requests.epoch === getSessionEpoch() && requests.listRead === id;
      if (requests.activeWrites.size > 0) requests.reconcile = true;
      this.loading = true;
      this.loaded = false;

      try {
        const sites = sortSites(await getSites());
        assertSession(requests);
        if (!ownsLoading() || requests.revision !== revision) return this.sites;
        this.sites = sites;
        // Yalnız istek GERÇEKTEN döndüğünde: hata fırlarsa buraya gelinmez ve ekran "site yok"
        // yerine "yükleniyor" demeye devam eder.
        requests.publishedList = id;
        this.loaded = requests.activeWrites.size === 0;
        selectExisting(this);

        return this.sites;
      } catch (error) {
        assertSession(requests);
        throw error;
      } finally {
        if (ownsLoading()) this.loading = false;
      }
    },
    async fetchSite(siteName: string) {
      const requests = requestsFor(this), id = ++requests.sequence;
      const version = requests.siteVersions.get(siteName) ?? 0, importRevision = requests.importRevision;
      requests.detailReads.set(siteName, id);
      requests.selection = id;
      try {
        const site = await getSite(siteName);
        assertSession(requests);
        if (requests.detailReads.get(siteName) === id && (requests.siteVersions.get(siteName) ?? 0) === version &&
          requests.importRevision === importRevision && requests.publishedList < id) {
          requests.revision++;
          upsert(this, site);
          if (requests.selection === id) this.selectedSiteName = site.name;
          if (requests.activeWrites.size > 0) requests.reconcile = true;
          else if (this.loading) await this.fetchSites().catch(() => undefined);
        }
        assertSession(requests);
        return site;
      } catch (error) { assertSession(requests); throw error; }
    },
    async importExistingSites() {
      return mutate(this, "importing", null, "", importSites, sites => { this.sites = sortSites(sites); selectExisting(this); });
    },
    async createSite(payload: CreateSitePayload) {
      const submitted = { ...payload };
      return mutate(this, "creating", submitted.name, "", () => createSite(submitted), (result, maySelect) => {
        upsert(this, result.site);
        if (maySelect) this.selectedSiteName = result.site.name;
      }, true);
    },
    async startSite(siteName: string) {
      return mutate(this, "busySiteName", siteName, siteName, () => startSite(siteName), site => upsert(this, site));
    },
    async prepareSiteDefaults(siteName: string) {
      return mutate(this, "busySiteName", siteName, siteName, () => ensureSiteDefaults(siteName), () => undefined);
    },
    async stopSite(siteName: string) {
      return mutate(this, "busySiteName", siteName, siteName, () => stopSite(siteName), site => upsert(this, site));
    },
    async deleteSite(
      siteName: string,
      options: { deleteFiles?: boolean; deleteDatabases?: boolean } = {},
    ) {
      return mutate(this, "busySiteName", siteName, siteName, () => deleteSite(siteName, { ...options }), () => {
        this.sites = this.sites.filter((site) => site.name !== siteName);
        if (this.selectedSiteName === siteName) {
          this.selectedSiteName = this.sites[0]?.name ?? "";
        }
      });
    },
    async addBinding(siteName: string, payload: SiteBindingPayload) {
      const submitted = { ...payload };
      return mutate(this, "busyBindingKey", siteName, bindingKey(submitted), () => addBinding(siteName, submitted), site => upsert(this, site));
    },
    async removeBinding(siteName: string, payload: SiteBindingPayload) {
      const submitted = { ...payload };
      return mutate(this, "busyBindingKey", siteName, bindingKey(submitted), () => removeBinding(siteName, submitted), site => upsert(this, site));
    },
    upsertSite(site: SiteRecord) {
      const requests = requestsFor(this);
      requests.revision++;
      advanceSite(requests, site.name);
      requests.siteWrites.set(site.name, ++requests.sequence);
      upsert(this, site);
      if (requests.activeWrites.size > 0) requests.reconcile = true;
      else if (this.loading) void this.fetchSites().catch(() => undefined);
    }
  }
});
