import { defineStore } from "pinia";
import {
  getSiteProxyConfiguration,
  removeSiteReverseProxy,
  saveSiteReverseProxy,
  saveSiteRewriteRule,
  type ReverseProxyConfiguration,
  type SaveReverseProxyPayload,
  type SaveRewriteRulePayload
} from "../api/proxy";

export const useProxyStore = defineStore("proxy", {
  state: () => ({
    configurations: {} as Record<string, ReverseProxyConfiguration>,
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
      this.loadingSiteName = siteName;

      try {
        const configuration = await getSiteProxyConfiguration(siteName);
        this.configurations[siteName] = configuration;
        return configuration;
      } finally {
        if (this.loadingSiteName === siteName) {
          this.loadingSiteName = "";
        }
      }
    },
    async saveProxy(siteName: string, payload: SaveReverseProxyPayload) {
      this.savingSiteName = siteName;

      try {
        const configuration = await saveSiteReverseProxy(siteName, payload);
        this.configurations[siteName] = configuration;
        return configuration;
      } finally {
        if (this.savingSiteName === siteName) {
          this.savingSiteName = "";
        }
      }
    },
    async removeProxy(siteName: string) {
      this.removingSiteName = siteName;

      try {
        const configuration = await removeSiteReverseProxy(siteName);
        this.configurations[siteName] = configuration;
        return configuration;
      } finally {
        if (this.removingSiteName === siteName) {
          this.removingSiteName = "";
        }
      }
    },
    async saveRewriteRule(siteName: string, payload: SaveRewriteRulePayload) {
      this.rewritingSiteName = siteName;

      try {
        const configuration = await saveSiteRewriteRule(siteName, payload);
        this.configurations[siteName] = configuration;
        return configuration;
      } finally {
        if (this.rewritingSiteName === siteName) {
          this.rewritingSiteName = "";
        }
      }
    }
  }
});
