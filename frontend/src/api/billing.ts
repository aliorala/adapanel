import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

/** Tek bir faturalama çağrısının izi (ayar ekranındaki "son çağrılar" listesi). */
export type BillingCallLogEntry = {
  atUtc: string;
  /** "usage" | "terminate_check" | "settings" */
  action: string;
  /** "ok" | "denied" | "error" */
  outcome: string;
  detail: string;
};

export type BillingSettings = {
  enabled: boolean;
  lastCallAtUtc: string | null;
  lastAccountCount: number;
  recentCalls: BillingCallLogEntry[];
};

/**
 * Bir aboneliğin WHMCS'e verilen kullanımı. Bütün büyüklükler MB'dir — çeviri panelde yapılır,
 * çünkü PHP tarafında bir kez yanlış bölünen sayı müşteriye yanlış fatura demektir.
 */
export type BillingUsageAccount = {
  userId: string;
  userName: string;
  domains: string[];
  planId: string;
  planName: string;
  diskUsageMb: number;
  /** 0 = sınırsız. */
  diskLimitMb: number;
  bandwidthUsageMb: number;
  /** 0 = sınırsız. */
  bandwidthLimitMb: number;
  /**
   * Trafik ölçülebildi mi. false ise bandwidthUsageMb GERÇEK kullanımı göstermez ve modül o
   * satırın bant genişliğini WHMCS'e yazmaz.
   */
  bandwidthMeasured: boolean;
  isSuspended: boolean;
};

export type BillingUsageReport = {
  generatedAtUtc: string;
  accounts: BillingUsageAccount[];
  unmeasuredTrafficCount: number;
};

export async function getBillingSettings() {
  const response = await apiClient.get<ApiEnvelope<BillingSettings>>("/billing/settings");
  return response.data.data;
}

export async function updateBillingSettings(enabled: boolean) {
  const response = await apiClient.put<ApiEnvelope<BillingSettings>>("/billing/settings", { enabled });
  return response.data.data;
}

export async function getBillingUsage() {
  const response = await apiClient.get<ApiEnvelope<BillingUsageReport>>("/billing/usage");
  return response.data.data;
}
