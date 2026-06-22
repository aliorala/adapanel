import { defineStore } from "pinia";
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

export const useSitesStore = defineStore("sites", {
  state: () => ({
    sites: [] as SiteRecord[],
    selectedSiteName: "",
    loading: false,
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
      this.selectedSiteName = siteName;
    },
    async fetchSites() {
      this.loading = true;

      try {
        this.sites = sortSites(await getSites());
        if (!this.selectedSiteName && this.sites.length > 0) {
          this.selectedSiteName = this.sites[0].name;
        }

        if (this.selectedSiteName && !this.sites.some((site) => site.name === this.selectedSiteName)) {
          this.selectedSiteName = this.sites[0]?.name ?? "";
        }

        return this.sites;
      } finally {
        this.loading = false;
      }
    },
    async fetchSite(siteName: string) {
      const site = await getSite(siteName);
      this.upsertSite(site);
      this.selectedSiteName = site.name;
      return site;
    },
    async importExistingSites() {
      this.importing = true;

      try {
        this.sites = sortSites(await importSites());
        if (!this.selectedSiteName && this.sites.length > 0) {
          this.selectedSiteName = this.sites[0].name;
        }

        return this.sites;
      } finally {
        this.importing = false;
      }
    },
    async createSite(payload: CreateSitePayload) {
      this.creating = true;

      try {
        const result = await createSite(payload);
        this.upsertSite(result.site);
        this.selectedSiteName = result.site.name;
        return result;
      } finally {
        this.creating = false;
      }
    },
    async startSite(siteName: string) {
      this.busySiteName = siteName;

      try {
        const site = await startSite(siteName);
        this.upsertSite(site);
        return site;
      } finally {
        this.busySiteName = "";
      }
    },
    async prepareSiteDefaults(siteName: string) {
      this.busySiteName = siteName;

      try {
        return await ensureSiteDefaults(siteName);
      } finally {
        this.busySiteName = "";
      }
    },
    async stopSite(siteName: string) {
      this.busySiteName = siteName;

      try {
        const site = await stopSite(siteName);
        this.upsertSite(site);
        return site;
      } finally {
        this.busySiteName = "";
      }
    },
    async deleteSite(
      siteName: string,
      options: { deleteFiles?: boolean; deleteDatabases?: boolean } = {},
    ) {
      this.busySiteName = siteName;

      try {
        const result = await deleteSite(siteName, options);
        this.sites = this.sites.filter((site) => site.name !== siteName);
        if (this.selectedSiteName === siteName) {
          this.selectedSiteName = this.sites[0]?.name ?? "";
        }
        return result;
      } finally {
        this.busySiteName = "";
      }
    },
    async addBinding(siteName: string, payload: SiteBindingPayload) {
      this.busyBindingKey = bindingKey(payload);

      try {
        const site = await addBinding(siteName, payload);
        this.upsertSite(site);
        return site;
      } finally {
        this.busyBindingKey = "";
      }
    },
    async removeBinding(siteName: string, payload: SiteBindingPayload) {
      this.busyBindingKey = bindingKey(payload);

      try {
        const site = await removeBinding(siteName, payload);
        this.upsertSite(site);
        return site;
      } finally {
        this.busyBindingKey = "";
      }
    },
    upsertSite(site: SiteRecord) {
      const nextSites = this.sites.some((item) => item.name === site.name)
        ? this.sites.map((item) => item.name === site.name ? site : item)
        : [...this.sites, site];

      this.sites = sortSites(nextSites);
    }
  }
});
