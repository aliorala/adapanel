import { apiClient } from "./client";

type ApiEnvelope<T> = { success: boolean; data: T; message: string; code: number };

export type ServiceHealth = {
  name: string;
  label: string;
  status: string; // Running | Stopped | NotInstalled | Unknown
};

export async function getServerHealth() {
  const response = await apiClient.get<ApiEnvelope<ServiceHealth[]>>("/server-health");
  return response.data.data;
}

export async function restartService(name: string) {
  const response = await apiClient.post<ApiEnvelope<ServiceHealth>>("/server-health/restart", { name });
  return response.data.data;
}

export type DomainRefreshResult = {
  siteName: string;
  zoneName: string;
  addedDnsRecords: string[];
  appliedMailRecords: number;
  error: string;
  alreadyCurrent: boolean;
};

/** Tek bir alan adına değil SUNUCUYA ait bulgu (posta sunucusu adı, kapalı port). */
export type ServerFinding = {
  key: string;
  severity: "warn" | "fail";
  repaired: boolean;
  needsConfirmation: boolean;
  detail: string;
};

export type DomainRefreshSummary = {
  results: DomainRefreshResult[];
  totalSites: number;
  updatedSites: number;
  failedSites: number;
  /**
   * Sunucu geneli bulgular. Bunlar hiçbir alan adına ait olmadığı için updatedSites'a
   * yansımaz; ayrıca sayılmazlarsa onarma düğmesi hiç görünmez ve sorun kalıcı olur.
   */
  serverFindings: ServerFinding[];
};

/** Değişiklik YAPMADAN kaç alan adının eski kaldığını sorar (uyarı göstermek için). */
export async function inspectDomains() {
  const response = await apiClient.get<ApiEnvelope<DomainRefreshSummary>>("/server-health/refresh-domains");
  return response.data.data;
}

/** Eksik varsayılanları tamamlar; var olan kayıtlara dokunmaz. */
export async function refreshDomains() {
  const response = await apiClient.post<ApiEnvelope<DomainRefreshSummary>>("/server-health/refresh-domains");
  return response.data.data;
}
