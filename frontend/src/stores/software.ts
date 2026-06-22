import { defineStore } from "pinia";
import { apiClient } from "../api/client";
import type { OperationProgress } from "./setup";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type ComponentManifestItem = {
  id: string;
  displayName: string;
  version: string;
  type: string;
  downloadUrl: string;
  sha256: string;
  installDir: string;
  postInstall: string[];
  dependencies: string[];
  serviceName: string;
  silentArgs: string;
  note: string;
  default: boolean;
};

export type InstalledComponent = {
  id: string;
  componentId: string;
  displayName: string;
  version: string;
  type: string;
  installDir: string;
  serviceName: string;
  status: string;
  lastError: string;
  generatedPassword: string;
  isSystemDetected: boolean;
  detectionSource: string;
  availableVersion: string;
  updateAvailable: boolean;
  installedAtUtc: string;
};

export type SoftwareCatalog = {
  manifest: ComponentManifestItem[];
  defaults: ComponentManifestItem[];
  installed: InstalledComponent[];
};

export type ComponentInstallResult = {
  componentId: string;
  displayName: string;
  version: string;
  status: string;
  installDir: string;
  serviceName: string;
  generatedPassword: string;
  message: string;
};

export const useSoftwareStore = defineStore("software", {
  state: () => ({
    catalog: null as SoftwareCatalog | null,
    loading: false,
    installingComponentId: "",
    removingComponentId: "",
    installingDefaults: false,
    checkingUpdates: false,
    liveProgress: null as OperationProgress | null
  }),
  getters: {
    updatableCount: (state) =>
      (state.catalog?.installed ?? []).filter((item) => item.updateAvailable).length
  },
  actions: {
    async fetchCatalog() {
      this.loading = true;

      try {
        const response = await apiClient.get<ApiEnvelope<SoftwareCatalog>>("/software");
        this.catalog = response.data.data;
        return this.catalog;
      } finally {
        this.loading = false;
      }
    },
    async fetchOperationProgress() {
      const response = await apiClient.get<ApiEnvelope<OperationProgress>>("/software/operations/progress");
      this.liveProgress = response.data.data;
      return this.liveProgress;
    },
    async checkForUpdates() {
      this.checkingUpdates = true;

      try {
        const response = await apiClient.post<ApiEnvelope<SoftwareCatalog>>("/software/updates/check", {});
        this.catalog = response.data.data;
        return this.catalog;
      } finally {
        this.checkingUpdates = false;
      }
    },
    async installComponent(componentId: string) {
      this.installingComponentId = componentId;

      try {
        const response = await apiClient.post<ApiEnvelope<ComponentInstallResult>>("/software/install", { componentId });
        await this.fetchCatalog();
        return response.data.data;
      } finally {
        this.installingComponentId = "";
      }
    },
    async installDefaultStack() {
      this.installingDefaults = true;

      try {
        const response = await apiClient.post<ApiEnvelope<ComponentInstallResult[]>>("/software/install-defaults", {});
        await this.fetchCatalog();
        return response.data.data;
      } finally {
        this.installingDefaults = false;
      }
    },
    async removeComponent(componentId: string) {
      this.removingComponentId = componentId;

      try {
        await apiClient.delete<ApiEnvelope<object>>(`/software/${encodeURIComponent(componentId)}`);
        await this.fetchCatalog();
      } finally {
        this.removingComponentId = "";
      }
    }
  }
});
