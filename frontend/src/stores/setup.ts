import { defineStore } from "pinia";
import { apiClient } from "../api/client";

type SetupStatus = {
  isInitialized: boolean;
  requiresSetupWizard: boolean;
  secureEntryPath: string;
  defaultLanguage: string;
  preferredHttpsPort: number;
  applicationVersion: string;
};

type SetupResult = {
  isInitialized: boolean;
  secureEntryPath: string;
  adminUserName: string;
  preferredLanguage: string;
  preferredHttpsPort: number;
  recommendedComponentsRequested: boolean;
  recommendedComponentsInstalled: number;
  installationInProgress: boolean;
  warnings: string[];
};

export type OperationProgressStep = {
  name: string;
  status: "running" | "done" | "failed";
  detail: string;
};

export type OperationProgress = {
  title: string;
  running: boolean;
  completed: boolean;
  failed: boolean;
  error: string;
  steps: OperationProgressStep[];
  startedAtUtc: string | null;
};

type SetupForm = {
  userName: string;
  email: string;
  password: string;
  confirmPassword: string;
  secureEntryPath: string;
  preferredLanguage: string;
  preferredHttpsPort: number;
  installRecommendedComponents: boolean;
};

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export const useSetupStore = defineStore("setup", {
  state: () => ({
    loading: false,
    initialized: false,
    status: null as SetupStatus | null
  }),
  actions: {
    async fetchStatus() {
      const response = await apiClient.get<ApiEnvelope<SetupStatus>>("/setup/status");
      this.status = response.data.data;
      this.initialized = response.data.data.isInitialized;
      return response.data.data;
    },
    async initialize(form: SetupForm) {
      this.loading = true;

      try {
        const response = await apiClient.post<ApiEnvelope<SetupResult>>("/setup/initialize", form);
        this.initialized = response.data.data.isInitialized;
        return response.data.data;
      } finally {
        this.loading = false;
      }
    },
    async fetchProgress(signal?: AbortSignal) {
      const response = await apiClient.get<ApiEnvelope<OperationProgress>>("/setup/progress", { signal, timeout: 10_000 });
      return response.data.data;
    }
  }
});
