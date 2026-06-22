import { defineStore } from "pinia";
import {
  getSiteAppRuntimeConfiguration,
  hostNodeApp,
  hostPythonApp,
  type AppRuntimeSiteConfiguration,
  type HostNodeAppPayload,
  type HostPythonAppPayload
} from "../api/apps";

export const useAppsStore = defineStore("apps", {
  state: () => ({
    configurations: {} as Record<string, AppRuntimeSiteConfiguration>,
    loadingSiteName: "",
    nodeBusySiteName: "",
    pythonBusySiteName: ""
  }),
  getters: {
    getConfiguration: (state) => (siteName: string) => state.configurations[siteName] ?? null
  },
  actions: {
    async fetchConfiguration(siteName: string) {
      this.loadingSiteName = siteName;

      try {
        const configuration = await getSiteAppRuntimeConfiguration(siteName);
        this.configurations[siteName] = configuration;
        return configuration;
      } finally {
        if (this.loadingSiteName === siteName) {
          this.loadingSiteName = "";
        }
      }
    },
    async configureNode(siteName: string, payload: HostNodeAppPayload) {
      this.nodeBusySiteName = siteName;

      try {
        const configuration = await hostNodeApp(siteName, payload);
        this.configurations[siteName] = configuration;
        return configuration;
      } finally {
        if (this.nodeBusySiteName === siteName) {
          this.nodeBusySiteName = "";
        }
      }
    },
    async configurePython(siteName: string, payload: HostPythonAppPayload) {
      this.pythonBusySiteName = siteName;

      try {
        const configuration = await hostPythonApp(siteName, payload);
        this.configurations[siteName] = configuration;
        return configuration;
      } finally {
        if (this.pythonBusySiteName === siteName) {
          this.pythonBusySiteName = "";
        }
      }
    }
  }
});
