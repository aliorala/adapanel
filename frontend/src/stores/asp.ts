import { defineStore } from "pinia";
import {
  enableMixedHandlers,
  getSiteAspConfiguration,
  updateClassicAsp,
  updateDotNetRuntime,
  type AspSiteConfiguration,
  type EnableMixedHandlersPayload,
  type UpdateClassicAspPayload,
  type UpdateDotNetRuntimePayload
} from "../api/asp";

export const useAspStore = defineStore("asp", {
  state: () => ({
    configurations: {} as Record<string, AspSiteConfiguration>,
    loadingSiteName: "",
    classicBusySiteName: "",
    dotNetBusySiteName: "",
    mixedBusySiteName: ""
  }),
  getters: {
    getConfiguration: (state) => (siteName: string) => state.configurations[siteName] ?? null
  },
  actions: {
    async fetchConfiguration(siteName: string) {
      this.loadingSiteName = siteName;

      try {
        const configuration = await getSiteAspConfiguration(siteName);
        this.configurations[siteName] = configuration;
        return configuration;
      } finally {
        if (this.loadingSiteName === siteName) {
          this.loadingSiteName = "";
        }
      }
    },
    async setClassicAsp(siteName: string, payload: UpdateClassicAspPayload) {
      this.classicBusySiteName = siteName;

      try {
        const configuration = await updateClassicAsp(siteName, payload);
        this.configurations[siteName] = configuration;
        return configuration;
      } finally {
        if (this.classicBusySiteName === siteName) {
          this.classicBusySiteName = "";
        }
      }
    },
    async setDotNetRuntime(siteName: string, payload: UpdateDotNetRuntimePayload) {
      this.dotNetBusySiteName = siteName;

      try {
        const configuration = await updateDotNetRuntime(siteName, payload);
        this.configurations[siteName] = configuration;
        return configuration;
      } finally {
        if (this.dotNetBusySiteName === siteName) {
          this.dotNetBusySiteName = "";
        }
      }
    },
    async enableMixedMode(siteName: string, payload: EnableMixedHandlersPayload) {
      this.mixedBusySiteName = siteName;

      try {
        const configuration = await enableMixedHandlers(siteName, payload);
        this.configurations[siteName] = configuration;
        return configuration;
      } finally {
        if (this.mixedBusySiteName === siteName) {
          this.mixedBusySiteName = "";
        }
      }
    }
  }
});
