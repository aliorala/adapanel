import { apiClient } from "./client";

type ApiEnvelope<T> = { success: boolean; data: T; message: string; code: number };

/** Bir aboneliğin o anki kullanımı ve durumu. */
export type SubscriptionUsage = {
  userId: string;
  userName: string;
  planId: string;
  planName: string;

  trafficBytesThisMonth: number;
  trafficLimitGb: number;
  /**
   * Trafik ÖLÇÜLEBİLDİ mi. false ise sayı gerçek kullanımı göstermez ve kota uygulanmaz —
   * ekranda "ölçülemedi" denir; sıfır göstermek "trafik yok" sanılmasına yol açardı.
   */
  trafficMeasured: boolean;

  expiresAtUtc: string | null;
  isExpired: boolean;

  isSuspended: boolean;
  suspendedAtUtc: string | null;
  /** Sabit anahtar: "traffic" | "expired" | "manual" — arayüz çevirir. */
  suspendReason: string;

  siteCount: number;
  maxSites: number;
  diskUsageMb: number;
  maxDiskMb: number;
};

export type SubscriptionEnforcementChange = {
  userId: string;
  userName: string;
  /** "suspended" | "resumed" */
  action: string;
  reason: string;
};

/** Giriş yapmış kullanıcının kendi kullanımı (müşteri de görebilir). */
export async function getMySubscription() {
  const response = await apiClient.get<ApiEnvelope<SubscriptionUsage>>("/subscriptions/me");
  return response.data.data;
}

export async function getSubscription(userId: string) {
  const response = await apiClient.get<ApiEnvelope<SubscriptionUsage>>(`/subscriptions/${encodeURIComponent(userId)}`);
  return response.data.data;
}

/** Aboneliği bir plana bağlar; boş planId bağı kaldırır. */
export async function setSubscriptionPlan(userId: string, planId: string) {
  const response = await apiClient.put<ApiEnvelope<SubscriptionUsage>>(
    `/subscriptions/${encodeURIComponent(userId)}/plan`,
    { planId }
  );
  return response.data.data;
}

export async function setSubscriptionExpiry(userId: string, expiresAtUtc: string | null) {
  const response = await apiClient.put<ApiEnvelope<SubscriptionUsage>>(
    `/subscriptions/${encodeURIComponent(userId)}/expiry`,
    { expiresAtUtc }
  );
  return response.data.data;
}

export async function suspendSubscription(userId: string) {
  const response = await apiClient.post<ApiEnvelope<SubscriptionUsage>>(
    `/subscriptions/${encodeURIComponent(userId)}/suspend`,
    {}
  );
  return response.data.data;
}

export async function resumeSubscription(userId: string) {
  const response = await apiClient.post<ApiEnvelope<SubscriptionUsage>>(
    `/subscriptions/${encodeURIComponent(userId)}/resume`,
    {}
  );
  return response.data.data;
}

/** Bütün abonelikleri hemen denetler (arka plan turunu beklemeden). */
export async function enforceSubscriptions() {
  const response = await apiClient.post<ApiEnvelope<SubscriptionEnforcementChange[]>>("/subscriptions/enforce", {});
  return response.data.data;
}
