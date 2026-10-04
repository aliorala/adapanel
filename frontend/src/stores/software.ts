import { defineStore } from "pinia";
import { toRaw } from "vue";
import { CanceledError } from "axios";
import { apiClient, getSessionEpoch } from "../api/client";
import type { OperationProgress } from "./setup";
import { installSoftwareComponent, installSoftwareDefaults, removeSoftwareComponent, type ComponentInstallResult, type ComponentRemovalResult } from "../api/software";
export type { ComponentInstallResult } from "../api/software";
import { UserFacingError } from "../utils/apiError";

// A measured DELETE result survives a failed follow-up read, while existing callers
// still receive a rejection and cannot treat an unmeasured catalog as current.
export class SoftwareRemovalRefreshError extends UserFacingError {
  constructor(public readonly result: ComponentRemovalResult, public readonly refreshError: unknown) {
    super("panel.software.refreshAfterRemoveFailed");
  }
}

export class SoftwareRemovalIncompleteError extends UserFacingError {
  constructor(public readonly result: ComponentRemovalResult) {
    super("software.remove.partial");
  }
}

// Keep measured POST outcomes separate from the follow-up catalog measurement.
// Shared callers still receive an error instead of assuming an unconfirmed install succeeded.
export class SoftwareInstallationRefreshError extends UserFacingError {
  constructor(public readonly results: ComponentInstallResult[], public readonly refreshError: unknown) {
    super("panel.software.refreshAfterInstallFailed");
  }
}

export class SoftwareInstallationIncompleteError extends UserFacingError {
  constructor(public readonly results: ComponentInstallResult[]) {
    super(results.length === 1 && results[0].status.toLowerCase() === "failed"
      ? results[0].message || "software.install_failed" : "panel.software.resultUnconfirmed");
  }
}

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
  /** Üst sürüm canlı sorgulanabiliyor mu; false ise "güncel" değil "bilinmiyor". */
  versionCheckSupported: boolean;
  versionCheckFailed: boolean;
  installedAtUtc: string;
};

export type SoftwareCatalog = {
  manifest: ComponentManifestItem[];
  defaults: ComponentManifestItem[];
  installed: InstalledComponent[];
};

type BusyKey = "loading" | "checkingUpdates" | "installingComponentId" | "installingDefaults" | "removingComponentId";
type Requests = {
  epoch: number;
  sequence: number;
  catalogReadId: number;
  progressReadId: number;
  writeId: number;
  revision: number;
  busy: Map<BusyKey, number>;
};
type Request = { id: number; requests: Requests; revision: number };
const requestsByStore = new WeakMap<object, Requests>();

function beginRequest(store: object, busy?: BusyKey, write = false): Request {
  const epoch = getSessionEpoch();
  const key = toRaw(store);
  let requests = requestsByStore.get(key);
  if (!requests || requests.epoch !== epoch) {
    requests = { epoch, sequence: 0, catalogReadId: 0, progressReadId: 0, writeId: 0, revision: 0, busy: new Map() };
    requestsByStore.set(key, requests);
  }
  const id = ++requests.sequence;
  if (busy) requests.busy.set(busy, id);
  if (write) {
    requests.writeId = id;
    requests.revision++;
    requests.progressReadId = 0;
  }
  return { id, requests, revision: requests.revision };
}

function assertCurrentSession(request: Request): void {
  // Also guard the gap between the HTTP response interceptor and the store await.
  if (request.requests.epoch !== getSessionEpoch()) throw new CanceledError("Session changed");
}

function ownsBusy(request: Request, busy: BusyKey): boolean {
  return request.requests.epoch === getSessionEpoch() && request.requests.busy.get(busy) === request.id;
}

function finishCatalogWrite(request: Request): boolean {
  assertCurrentSession(request);
  const unchanged = request.requests.writeId === request.id && request.requests.revision === request.revision &&
    request.requests.catalogReadId < request.id;
  // Reads made before or during a mutation cannot overwrite its refreshed catalog.
  request.requests.revision++;
  return unchanged;
}

export const useSoftwareStore = defineStore("software", {
  state: () => ({
    catalog: null as SoftwareCatalog | null,
    loading: false,
    /*
     * Son katalog ölçümü başarılı mı. Eski katalog gösterim için tutulabilir ama başarısız
     * yenileme yeni yazmaları açmamalı. Bayrak yalnız güncel başarılı ölçümde true olur;
     * katalog dokuz ekranda paylaşıldığı için bu bilgi de mağazada kalır.
     */
    loaded: false,
    installingComponentId: "",
    removingComponentId: "",
    installingDefaults: false,
    checkingUpdates: false,
    liveProgress: null as OperationProgress | null
  }),
  getters: {
    incompleteCheckCount: (state) =>
      (state.catalog?.installed ?? []).filter((item) => !item.isSystemDetected && item.status.toLowerCase() === "installed"
        && (!item.versionCheckSupported || item.versionCheckFailed)).length,
    updatableCount: (state) =>
      (state.catalog?.installed ?? []).filter((item) => item.updateAvailable).length,
    failedCount: (state) =>
      (state.catalog?.installed ?? []).filter((item) => item.status.toLowerCase() === "failed").length
  },
  actions: {
    async fetchCatalog() {
      const request = beginRequest(this, "loading");
      request.requests.catalogReadId = request.id;
      this.loading = true;
      this.loaded = false;

      try {
        const response = await apiClient.get<ApiEnvelope<SoftwareCatalog>>("/software");
        assertCurrentSession(request);
        if (response.data?.success !== true || !response.data.data ||
          !Array.isArray(response.data.data.manifest) || !Array.isArray(response.data.data.defaults) ||
          !Array.isArray(response.data.data.installed)) throw new UserFacingError("panel.software.resultUnconfirmed");
        // Yalnız BAŞARIDA: hata fırlarsa bayrak false kalır, ekran "bileşen yok" demez.
        if (request.requests.catalogReadId === request.id && request.requests.revision === request.revision) {
          this.catalog = response.data.data;
          this.loaded = true;
        }
        return response.data.data;
      } finally {
        if (ownsBusy(request, "loading")) this.loading = false;
      }
    },
    async fetchOperationProgress() {
      const request = beginRequest(this);
      request.requests.progressReadId = request.id;
      const response = await apiClient.get<ApiEnvelope<OperationProgress>>("/software/operations/progress");
      assertCurrentSession(request);
      if (request.requests.progressReadId === request.id && request.requests.revision === request.revision) {
        this.liveProgress = response.data.data;
      }
      return response.data.data;
    },
    async checkForUpdates() {
      const request = beginRequest(this, "checkingUpdates", true);
      this.checkingUpdates = true;
      this.loaded = false;

      try {
        const response = await apiClient.post<ApiEnvelope<SoftwareCatalog>>("/software/updates/check", {});
        if (finishCatalogWrite(request)) {
          this.catalog = response.data.data;
          this.loaded = true;
        } else {
          // Another mutation overlapped this check; its response may predate that mutation.
          await this.fetchCatalog();
        }
        assertCurrentSession(request);
        return response.data.data;
      } catch (error) {
        assertCurrentSession(request);
        throw error;
      } finally {
        if (ownsBusy(request, "checkingUpdates")) this.checkingUpdates = false;
      }
    },
    // force: bileşen zaten kurulu ve sağlıklıysa sunucu kurulumu ATLAR. Sürüm yükseltmesi tam
    // olarak bu atlamayı aşmak ister — "Güncelle" düğmesi force=true gönderir, ilk kurulum göndermez.
    async installComponent(componentId: string, force = false) {
      const request = beginRequest(this, "installingComponentId", true);
      this.installingComponentId = componentId;

      try {
        const result = await installSoftwareComponent(componentId, force).catch(async error => {
          // Failed attempts can alter server records. Refresh once, only in their
          // originating session, while preserving the original install failure.
          finishCatalogWrite(request);
          await this.fetchCatalog().catch(() => undefined);
          assertCurrentSession(request);
          throw error;
        });
        finishCatalogWrite(request);
        try { await this.fetchCatalog(); }
        catch (error) {
          assertCurrentSession(request);
          throw new SoftwareInstallationRefreshError([result], error);
        }
        assertCurrentSession(request);
        if (result.status.toLowerCase() !== "installed") throw new SoftwareInstallationIncompleteError([result]);
        return result;
      } finally {
        if (ownsBusy(request, "installingComponentId")) this.installingComponentId = "";
      }
    },
    async installDefaultStack() {
      const request = beginRequest(this, "installingDefaults", true);
      const expectedIds = this.catalog?.defaults.map(item => item.id);
      this.installingDefaults = true;

      try {
        const results = await installSoftwareDefaults(expectedIds).catch(async error => {
          finishCatalogWrite(request);
          await this.fetchCatalog().catch(() => undefined);
          assertCurrentSession(request);
          throw error;
        });
        finishCatalogWrite(request);
        try { await this.fetchCatalog(); }
        catch (error) {
          assertCurrentSession(request);
          throw new SoftwareInstallationRefreshError(results, error);
        }
        assertCurrentSession(request);
        if (results.some(result => result.status.toLowerCase() !== "installed")) throw new SoftwareInstallationIncompleteError(results);
        return results;
      } finally {
        if (ownsBusy(request, "installingDefaults")) this.installingDefaults = false;
      }
    },
    async removeComponent(componentId: string) {
      const request = beginRequest(this, "removingComponentId", true);
      this.removingComponentId = componentId;

      try {
        const result = await removeSoftwareComponent(componentId);
        finishCatalogWrite(request);
        try {
          await this.fetchCatalog();
        } catch (error) {
          assertCurrentSession(request);
          throw new SoftwareRemovalRefreshError(result, error);
        }
        assertCurrentSession(request);
        if (result.warnings.length) throw new SoftwareRemovalIncompleteError(result);
        return result;
      } catch (error) {
        assertCurrentSession(request);
        throw error;
      } finally {
        if (ownsBusy(request, "removingComponentId")) this.removingComponentId = "";
      }
    }
  }
});
