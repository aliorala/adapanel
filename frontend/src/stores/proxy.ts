import { defineStore } from "pinia";
import { toRaw } from "vue";
import { CanceledError } from "axios";
import { getSessionEpoch } from "../api/client";
import {
  getSiteProxyConfiguration,
  removeSiteReverseProxy,
  saveSiteReverseProxy,
  saveSiteRewriteRule,
  type ReverseProxyConfiguration,
  type SaveReverseProxyPayload,
  type SaveRewriteRulePayload
} from "../api/proxy";

type ProxyState = {
  configurations: Record<string, ReverseProxyConfiguration>;
  measured: Record<string, boolean>;
  loadingSiteName: string; savingSiteName: string; removingSiteName: string; rewritingSiteName: string;
};
type Requests = { epoch: number; records: object; sequence: number; reads: Map<string, number>; versions: Map<string, number>; loading: number; writing: string };
const requestsByStore = new WeakMap<object, Requests>();
function requestsFor(store: ProxyState): Requests {
  // Pinia's browser devtools gives each action a distinct `this` Proxy. The
  // metadata key must be raw, while every state write remains reactive.
  const key = toRaw(store), records = toRaw(store.configurations);
  let requests = requestsByStore.get(key);
  if (!requests || requests.epoch !== getSessionEpoch() || requests.records !== records) {
    requests = { epoch: getSessionEpoch(), records, sequence: 0, reads: new Map(), versions: new Map(), loading: 0, writing: "" };
    requestsByStore.set(key, requests);
  }
  return requests;
}
function assertCurrentStore(store: ProxyState, requests: Requests) {
  if (requestsFor(store) !== requests) throw new CanceledError("Proxy session or store changed");
}
function advance(requests: Requests, site: string) { requests.versions.set(site, (requests.versions.get(site) ?? 0) + 1); }
async function writeConfiguration(store: ProxyState, site: string, action: "savingSiteName" | "removingSiteName" | "rewritingSiteName", operation: () => Promise<ReverseProxyConfiguration>) {
  const requests = requestsFor(store);
  if (requests.writing) throw new CanceledError("Proxy configuration write already running");
  requests.writing = site; advance(requests, site); store[action] = site; store.measured[site] = false;
  try {
    const configuration = await operation(); assertCurrentStore(store, requests);
    store.configurations[site] = configuration; store.measured[site] = true;
    return configuration;
  } catch (error) { assertCurrentStore(store, requests); throw error; }
  finally {
    if (requestsFor(store) === requests) { advance(requests, site); requests.writing = ""; store[action] = ""; }
  }
}

export const useProxyStore = defineStore("proxy", {
  state: () => ({
    configurations: {} as Record<string, ReverseProxyConfiguration>,
    measured: {} as Record<string, boolean>,
    loadingSiteName: "",
    savingSiteName: "",
    removingSiteName: "",
    rewritingSiteName: ""
  }),
  getters: {
    getConfiguration: (state) => (siteName: string) => state.configurations[siteName] ?? null
  },
  actions: {
    async fetchConfiguration(siteName: string) {
      const requests = requestsFor(this), id = ++requests.sequence, version = requests.versions.get(siteName) ?? 0;
      requests.reads.set(siteName, id); requests.loading = id;
      this.loadingSiteName = siteName;
      const current = () => {
        assertCurrentStore(this, requests);
        if (requests.reads.get(siteName) !== id || (requests.versions.get(siteName) ?? 0) !== version || requests.writing === siteName)
          throw new CanceledError("Proxy read superseded");
      };
      try {
        const configuration = await getSiteProxyConfiguration(siteName);
        current();
        this.configurations[siteName] = configuration;
        this.measured[siteName] = true;
        return configuration;
      } catch (error) {
        current(); this.measured[siteName] = false; throw error;
      } finally {
        if (requestsFor(this) === requests && requests.loading === id) {
          this.loadingSiteName = "";
        }
      }
    },
    async saveProxy(siteName: string, payload: SaveReverseProxyPayload) {
      const submitted = { ...payload };
      return writeConfiguration(this, siteName, "savingSiteName", () => saveSiteReverseProxy(siteName, submitted));
    },
    async removeProxy(siteName: string) {
      return writeConfiguration(this, siteName, "removingSiteName", () => removeSiteReverseProxy(siteName));
    },
    async saveRewriteRule(siteName: string, payload: SaveRewriteRulePayload) {
      const submitted = { ...payload };
      return writeConfiguration(this, siteName, "rewritingSiteName", () => saveSiteRewriteRule(siteName, submitted));
    }
  }
});
