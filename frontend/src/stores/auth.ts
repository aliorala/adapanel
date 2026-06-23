import { defineStore } from "pinia";
import { AxiosError } from "axios";
import { apiClient, clearCsrfToken } from "../api/client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
  details?: string;
};

export type CurrentUser = {
  id: string;
  userName: string;
  email: string;
  role: string;
  preferredLanguage: string;
  isTwoFactorEnabled: boolean;
  isActive: boolean;
  lastLoginAtUtc: string | null;
};

type CurrentSession = {
  isAuthenticated: boolean;
  requiresSetupWizard: boolean;
  user: CurrentUser | null;
  isImpersonating?: boolean;
  impersonatorName?: string | null;
};

type LoginResult = {
  isAuthenticated: boolean;
  requiresTwoFactor: boolean;
  user: CurrentUser | null;
};

export type TwoFactorSetup = {
  isTwoFactorEnabled: boolean;
  hasPendingSetup: boolean;
  manualEntryKey: string;
  provisioningUri: string;
};

export type SecuritySettings = {
  secureEntryPath: string;
  allowedIpAddresses: string[];
  hasIpRestrictions: boolean;
};

export type ManagedUser = {
  id: string;
  userName: string;
  email: string;
  role: string;
  preferredLanguage: string;
  isActive: boolean;
  isTwoFactorEnabled: boolean;
  lastLoginAtUtc: string | null;
  createdAtUtc: string;
  assignedSites: string[];
  maxSites: number;
  maxDiskMb: number;
  maxDatabases: number;
  maxMailboxes: number;
  diskUsageMb: number;
  ownerId: string | null;
  ownerName: string;
};

export type AuditLogItem = {
  id: number;
  userId: string | null;
  userName: string;
  action: string;
  resource: string;
  details: string;
  ipAddress: string;
  createdAtUtc: string;
};

type LoginPayload = {
  userNameOrEmail: string;
  password: string;
  twoFactorCode?: string;
  rememberMe: boolean;
};

type CreateUserPayload = {
  userName: string;
  email: string;
  password: string;
  role: string;
  ownerId?: string | null;
  preferredLanguage: string;
  assignedSites: string[];
  maxSites: number;
  maxDiskMb: number;
  maxDatabases: number;
  maxMailboxes: number;
  sendWelcomeEmail: boolean;
};

type UpdateUserPayload = {
  email: string;
  role: string;
  ownerId?: string | null;
  preferredLanguage: string;
  isActive: boolean;
  assignedSites: string[];
  maxSites: number;
  maxDiskMb: number;
  maxDatabases: number;
  maxMailboxes: number;
};

export const useAuthStore = defineStore("auth", {
  state: () => ({
    session: null as CurrentSession | null,
    users: [] as ManagedUser[],
    auditLogs: [] as AuditLogItem[],
    securitySettings: null as SecuritySettings | null,
    twoFactorSetup: null as TwoFactorSetup | null,
    loading: false
  }),
  actions: {
    async fetchSession() {
      const response = await apiClient.get<ApiEnvelope<CurrentSession>>("/auth/session");
      this.session = response.data.data;
      return this.session;
    },
    async login(payload: LoginPayload) {
      this.loading = true;

      try {
        const response = await apiClient.post<ApiEnvelope<LoginResult>>("/auth/login", payload);
        const result = response.data.data;

        if (result.isAuthenticated) {
          clearCsrfToken();
          this.session = {
            isAuthenticated: true,
            requiresSetupWizard: false,
            user: result.user
          };
        }

        return result;
      } finally {
        this.loading = false;
      }
    },
    async impersonate(userId: string) {
      await apiClient.post<ApiEnvelope<CurrentUser>>(`/auth/impersonate/${encodeURIComponent(userId)}`, {});
      clearCsrfToken();
      await this.fetchSession();
    },
    async stopImpersonate() {
      await apiClient.post<ApiEnvelope<CurrentUser>>("/auth/impersonate/stop", {});
      clearCsrfToken();
      await this.fetchSession();
    },
    async logout() {
      await apiClient.post<ApiEnvelope<object>>("/auth/logout", {});
      clearCsrfToken();
      this.session = {
        isAuthenticated: false,
        requiresSetupWizard: false,
        user: null
      };
      this.users = [];
      this.auditLogs = [];
      this.securitySettings = null;
      this.twoFactorSetup = null;
    },
    async beginTwoFactorSetup() {
      const response = await apiClient.post<ApiEnvelope<TwoFactorSetup>>("/auth/2fa/begin", {});
      this.twoFactorSetup = response.data.data;
      return this.twoFactorSetup;
    },
    async confirmTwoFactorSetup(code: string) {
      const response = await apiClient.post<ApiEnvelope<{ user: CurrentUser; recoveryCodes: string[] }>>("/auth/2fa/confirm", { code });
      if (this.session) {
        this.session.user = response.data.data.user;
      }
      this.twoFactorSetup = null;
      return response.data.data.recoveryCodes ?? [];
    },
    async regenerateRecoveryCodes() {
      const response = await apiClient.post<ApiEnvelope<{ codes: string[] }>>("/auth/2fa/recovery-codes", {});
      return response.data.data.codes ?? [];
    },
    async disableTwoFactor(password: string) {
      const response = await apiClient.post<ApiEnvelope<CurrentUser>>("/auth/2fa/disable", { password });
      if (this.session) {
        this.session.user = response.data.data;
      }
      return response.data.data;
    },
    async fetchUsers() {
      const response = await apiClient.get<ApiEnvelope<ManagedUser[]>>("/users");
      this.users = response.data.data;
      return this.users;
    },
    async createUser(payload: CreateUserPayload) {
      const response = await apiClient.post<ApiEnvelope<ManagedUser>>("/users", payload);
      this.users = [...this.users, response.data.data].sort((left, right) => left.userName.localeCompare(right.userName));
      return response.data.data;
    },
    async updateUser(userId: string, payload: UpdateUserPayload) {
      const response = await apiClient.put<ApiEnvelope<ManagedUser>>(`/users/${userId}`, payload);
      this.users = this.users.map((user) => user.id === userId ? response.data.data : user);
      return response.data.data;
    },
    async fetchSecuritySettings() {
      const response = await apiClient.get<ApiEnvelope<SecuritySettings>>("/security/settings");
      this.securitySettings = response.data.data;
      return this.securitySettings;
    },
    async updateSecuritySettings(payload: SecuritySettings) {
      const response = await apiClient.put<ApiEnvelope<SecuritySettings>>("/security/settings", payload);
      this.securitySettings = response.data.data;
      return response.data.data;
    },
    async fetchAuditLogs() {
      const response = await apiClient.get<ApiEnvelope<AuditLogItem[]>>("/audit-logs?take=25");
      this.auditLogs = response.data.data;
      return this.auditLogs;
    },
    async forgotPassword(email: string) {
      await apiClient.post<ApiEnvelope<object>>("/auth/password/forgot", { email });
    },
    async resetPassword(token: string, newPassword: string) {
      await apiClient.post<ApiEnvelope<object>>("/auth/password/reset", { token, newPassword });
    }
  }
});

export function extractApiMessage(error: unknown): string {
  const axiosError = error as AxiosError<ApiEnvelope<unknown>>;
  const message = axiosError.response?.data?.message;
  if (message) {
    return message;
  }

  if (axiosError.response?.status) {
    return `HTTP ${axiosError.response.status}: ${axiosError.response.statusText || "Request failed"}`;
  }

  return axiosError.message || "common.request_failed";
}

export function extractApiDetails(error: unknown): string {
  const axiosError = error as AxiosError<ApiEnvelope<unknown>>;
  return axiosError.response?.data?.details?.trim() ?? "";
}
