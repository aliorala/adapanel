import { defineStore } from "pinia";
import { AxiosError } from "axios";
import { apiClient, clearCsrfToken } from "../api/client";
import { deleteUser as deleteUserRequest } from "../api/users";
import { resolveFailureDetail, resolveFailureKey, type TransportFailure } from "../utils/apiError";

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
  isSubUser?: boolean;
  allowedSections?: string[] | null;
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
  // Hatalı giriş denemeleri yüzünden kilitliyse kilidin bitişi; değilse null.
  lockedUntilUtc: string | null;
  createdAtUtc: string;
  assignedSites: string[];
  maxSites: number;
  maxDiskMb: number;
  maxDatabases: number;
  maxMailboxes: number;
  maxScheduledTasks: number;
  parentUserId: string | null;
  parentName: string;
  diskUsageMb: number;
  /** false ise diskUsageMb bir ALT SINIRDIR: agac tumuyle taranamadi. */
  diskMeasured: boolean;
  /**
   * Olcumun NEDEN kesildigi (`files.size_truncated_entries|_time|_denied`); olcum tamsa bos.
   * Bos olmasi "tam" demek DEGILDIR — bakilacak bayrak `diskMeasured`dir.
   *
   * Alan `UserSummaryDto`da vardi ama tipte yoktu, yani panel sebebi hic okuyamiyordu;
   * `SubscriptionUsageDto`nun ayni alani `api/subscriptions.ts`te ZATEN tipliydi.
   */
  diskUsageTruncatedReasonKey: string;
  databaseCount: number;
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
  maxScheduledTasks: number;
  parentUserId?: string | null;
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
  maxScheduledTasks: number;
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
          // Oturumu tazele: alt-kullanıcı/izin bilgisi (isSubUser, allowedSections) yalnız session ucunda döner.
          try {
            await this.fetchSession();
          } catch {
            // Session tazeleme başarısızsa login yine geçerli; menü kısıtları bir sonraki yüklemede oturur.
          }
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
    async changePassword(currentPassword: string, newPassword: string) {
      const response = await apiClient.post<ApiEnvelope<{ revokedApiTokens: number }>>("/auth/change-password", {
        currentPassword,
        newPassword
      });
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
    // Hatalı giriş kilidini beklemeden açar (varsayılan kilit süresi 15 dk).
    async unlockUser(userId: string) {
      const response = await apiClient.post<ApiEnvelope<ManagedUser>>(`/users/${userId}/unlock`, {});
      this.users = this.users.map((user) => user.id === userId ? response.data.data : user);
      return response.data.data;
    },
    // Siteler/veriler sunucuda kalır (yönetime devir); alt-kullanıcıları ve panel kayıtları backend'de temizlenir.
    //
    // SONUÇ ARTIK GERİ VERİLİYOR: uç "ne silindi, ne kaldı" raporu döner (denetim Y7). Eskiden
    // yanıt hiç okunmuyordu; ekran her koşulda "kullanıcı silindi" yazıyor, sunucuda kalan
    // sahipsiz site/veritabanı görünmüyordu.
    async deleteUser(userId: string, removeSites = false) {
      try {
        const sonuc = await deleteUserRequest(userId, removeSites);
        await this.fetchUsers();
        return sonuc;
      } catch (error) {
        // 409 = kullanıcı KAYDI silindi ama sunucuda kalıntı var. Liste yine tazelenmeli, yoksa
        // ekranda artık var olmayan bir kullanıcı görünmeye devam eder. Tazeleme hatası ASIL
        // hatayı gölgelemesin diye yutuluyor: kalıntı raporu her şeyden önemli.
        await this.fetchUsers().catch(() => undefined);
        throw error;
      }
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

/** Axios hatasını, saf çekirdeğin anladığı düz veriye indirger (tek dönüşüm noktası). */
function toTransportFailure(error: unknown): TransportFailure {
  const axiosError = error as AxiosError<ApiEnvelope<unknown>>;
  return {
    status: axiosError.response?.status,
    statusText: axiosError.response?.statusText,
    code: axiosError.code,
    message: axiosError.message,
    envelope: axiosError.response?.data ?? null,
    // `isAxiosError` yoksa hata ağdan değil, ekranın kendi kodundan gelmiştir.
    isAxios: axiosError.isAxiosError === true
  };
}

/**
 * Ekranda gösterilecek ÇEVİRİ ANAHTARI. Karar mantığı `utils/apiError.ts`'te (saf + test edilir).
 *
 * İki davranış değişti:
 * 1) Arka ucun serbest metne gömdüğü bileşik anahtar (`"ftp.user_exists — CreateUser failed…"`)
 *    ayrıştırılır; artık anahtar kısmı çevrilebilir. Ayrıntı kaybolmaz, `extractApiDetails`
 *    tarafından ayrı satırda döner.
 * 2) Ağ/zaman aşımı/durum kodu artık ham İngilizce dizge değil, anahtar döner
 *    (`common.network_error`, `common.timeout`, …) — Türkçe panelde İngilizce metin kalmasın.
 */
export function extractApiMessage(error: unknown): string {
  return resolveFailureKey(toTransportFailure(error));
}

/** Anahtarın altına yazılacak ayrıntı: zarfın `details`'i, gömülü ayrıntı ya da HTTP durumu. */
export function extractApiDetails(error: unknown): string {
  return resolveFailureDetail(toTransportFailure(error));
}
