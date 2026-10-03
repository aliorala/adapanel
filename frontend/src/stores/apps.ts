import { defineStore } from "pinia";
import { toRaw } from "vue";
import { CanceledError } from "axios";
import { getSessionEpoch } from "../api/client";
import {
  getSiteAppRuntimeConfiguration,
  getSiteAppRuntimeInventory,
  hostNodeApp,
  hostPythonApp,
  type AppRuntimeSiteConfiguration,
  type AppRuntimeInventoryItem,
  type HostNodeAppPayload,
  type HostPythonAppPayload
} from "../api/apps";

type Requests = { epoch: number; sequence: number; reads: Map<string, number>; inventoryReads: Map<string, number>; versions: Map<string, number>; loading: number; writing: string };
const requestsByStore = new WeakMap<object, Requests>();
function requestsFor(store: object): Requests {
  // Pinia devtools wraps each action's `this` in a different Proxy.
  // Normalize only the identity key; retain the reactive store for state writes.
  const key = toRaw(store);
  let requests = requestsByStore.get(key);
  if (!requests || requests.epoch !== getSessionEpoch()) {
    requests = { epoch: getSessionEpoch(), sequence: 0, reads: new Map(), inventoryReads: new Map(), versions: new Map(), loading: 0, writing: "" };
    requestsByStore.set(key, requests);
  }
  return requests;
}
function assertSession(requests: Requests) {
  if (requests.epoch !== getSessionEpoch()) throw new CanceledError("Session changed");
}
function advance(requests: Requests, siteName: string) {
  requests.versions.set(siteName, (requests.versions.get(siteName) ?? 0) + 1);
}
type AppState = { configurations: Record<string, AppRuntimeSiteConfiguration>; loadingSiteName: string; nodeBusySiteName: string; pythonBusySiteName: string };
async function configure(store: AppState, siteName: string, payload: HostNodeAppPayload, runtime: "node" | "python") {
  const requests = requestsFor(store);
  if (requests.writing) throw new CanceledError("Runtime configuration is already being saved");
  requests.writing = siteName;
  advance(requests, siteName);
  const busy = runtime === "node" ? "nodeBusySiteName" : "pythonBusySiteName";
  store[busy] = siteName;
  const submitted = { ...payload, environmentVariables: [...payload.environmentVariables] };
  try {
    const configuration = await (runtime === "node" ? hostNodeApp : hostPythonApp)(siteName, submitted);
    assertSession(requests);
    store.configurations[siteName] = configuration;
    return configuration;
  } catch (error) {
    assertSession(requests);
    throw error;
  } finally {
    if (requests.epoch === getSessionEpoch()) {
      // Reads started before or during the write cannot roll its result back.
      advance(requests, siteName);
      requests.writing = "";
      store[busy] = "";
    }
  }
}

export const useAppsStore = defineStore("apps", {
  state: () => ({
    configurations: {} as Record<string, AppRuntimeSiteConfiguration>,
    runtimeInventories: {} as Record<string, AppRuntimeInventoryItem[]>,
    runtimeInventoryLoaded: {} as Record<string, boolean>,
    loadingSiteName: "",
    nodeBusySiteName: "",
    pythonBusySiteName: ""
  }),
  getters: {
    getConfiguration: (state) => (siteName: string) => state.configurations[siteName] ?? null
  },
  actions: {
    async fetchRuntimeInventory(siteName: string) {
      const requests = requestsFor(this), id = ++requests.sequence, version = requests.versions.get(siteName) ?? 0;
      requests.inventoryReads.set(siteName, id);
      this.runtimeInventoryLoaded[siteName] = false;
      const assertCurrent = () => {
        assertSession(requests);
        if (requests.inventoryReads.get(siteName) !== id || (requests.versions.get(siteName) ?? 0) !== version || requests.writing === siteName)
          throw new CanceledError("Runtime inventory read superseded");
      };
      try {
        const inventory = await getSiteAppRuntimeInventory(siteName);
        assertCurrent();
        this.runtimeInventories[siteName] = inventory;
        this.runtimeInventoryLoaded[siteName] = true;
        return inventory;
      } catch (error) {
        assertCurrent();
        throw error;
      }
    },
    async fetchConfiguration(siteName: string) {
      const requests = requestsFor(this), id = ++requests.sequence, version = requests.versions.get(siteName) ?? 0;
      requests.reads.set(siteName, id);
      requests.loading = id;
      this.loadingSiteName = siteName;

      const assertCurrent = () => {
        assertSession(requests);
        if (requests.reads.get(siteName) !== id || (requests.versions.get(siteName) ?? 0) !== version || requests.writing === siteName)
          throw new CanceledError("Runtime configuration read superseded");
      };

      try {
        const configuration = await getSiteAppRuntimeConfiguration(siteName);
        assertCurrent();
        this.configurations[siteName] = configuration;
        return configuration;
      } catch (error) {
        assertCurrent();
        throw error;
      } finally {
        if (requests.epoch === getSessionEpoch() && requests.loading === id) {
          this.loadingSiteName = "";
        }
      }
    },
    async configureNode(siteName: string, payload: HostNodeAppPayload) {
      return configure(this, siteName, payload, "node");
    },
    async configurePython(siteName: string, payload: HostPythonAppPayload) {
      return configure(this, siteName, payload, "python");
    }
  }
});
