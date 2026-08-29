import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

/*
 * ÖLÇÜM BAYRAKLARI TİPE GİRDİ — API ONLARI ZATEN GÖNDERİYORDU, ARAYÜZ GÖRMÜYORDU.
 *
 * ÖLÇÜLDÜ (kurulu panel, `GET /sites/{ad}/security` yanıtı): dönen gövdede
 * `isIsolatedMeasured`, `folderAclMeasured`, `basicRulesMeasured`, `rateLimitMeasured`
 * alanlarının DÖRDÜ DE var. Bu tip onları hiç bildirmediği için ekran da hiç okumuyordu:
 * ölçüm başarısız olduğunda sunucu panelin son bildiği KAYDI döndürüyor, ekran ise onu
 * ölçülmüş gibi yeşil/kehribar bir rozetle çiziyordu. Yani korunmayan bir site
 * "korunuyor" görünebiliyordu (bkz. SiteSecurityConfigurationDto'nun kendi açıklaması:
 * bayrak tam olarak bunu söylemek için eklenmiş).
 *
 * OPSİYONEL (`?`) BİLEREK: bu alanları göndermeyen ESKİ bir sunucuya bağlanıldığında
 * `undefined` gelir. Onu "ölçülemedi" saymak, ölçülmüş bir değeri şüpheli göstermek olurdu —
 * yani ters yönde bir yalan. Ekran yalnız `=== false` durumunda "Ölçülemedi" der.
 */
export type SiteSecurityConfiguration = {
  siteName: string;
  applicationPoolName: string;
  physicalPath: string;
  isIsolated: boolean;
  folderAclApplied: boolean;
  basicRulesEnabled: boolean;
  requestsPerMinute: number;
  principal: string;
  isIsolatedMeasured?: boolean;
  folderAclMeasured?: boolean;
  basicRulesMeasured?: boolean;
  rateLimitMeasured?: boolean;
};

export async function getSiteSecurity(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<SiteSecurityConfiguration>>(`/sites/${encodeURIComponent(siteName)}/security`);
  return response.data.data;
}

export async function isolateSite(siteName: string) {
  const response = await apiClient.post<ApiEnvelope<SiteSecurityConfiguration>>(`/sites/${encodeURIComponent(siteName)}/isolate`, {});
  return response.data.data;
}

export async function applyAcl(siteName: string, principal: string, rights: string) {
  const response = await apiClient.post<ApiEnvelope<SiteSecurityConfiguration>>(`/sites/${encodeURIComponent(siteName)}/acl`, {
    principal,
    rights
  });
  return response.data.data;
}

export async function setRateLimit(siteName: string, requestsPerMinute: number, burst: number) {
  const response = await apiClient.post<ApiEnvelope<SiteSecurityConfiguration>>(`/sites/${encodeURIComponent(siteName)}/ratelimit`, {
    requestsPerMinute,
    burst
  });
  return response.data.data;
}

export async function enableBasicRules(siteName: string) {
  const response = await apiClient.post<ApiEnvelope<SiteSecurityConfiguration>>(`/sites/${encodeURIComponent(siteName)}/basic-rules`, {});
  return response.data.data;
}

export type BruteForceSettings = {
  enabled: boolean;
  maxFailedAttempts: number;
  windowMinutes: number;
  banMinutes: number;
  whitelistedIps: string[];
};

export type BannedIp = {
  ipAddress: string;
  bannedAtUtc: string;
  expiresAtUtc: string | null;
  failedAttempts: number;
  firewallRuleName: string;
};

export type BruteForceStatus = {
  settings: BruteForceSettings;
  bannedIps: BannedIp[];
};

export async function getBruteForce() {
  const response = await apiClient.get<ApiEnvelope<BruteForceStatus>>("/security/bruteforce");
  return response.data.data;
}

export async function updateBruteForce(settings: BruteForceSettings) {
  const response = await apiClient.put<ApiEnvelope<BruteForceStatus>>("/security/bruteforce", settings);
  return response.data.data;
}

export async function unbanIp(ipAddress: string) {
  const response = await apiClient.post<ApiEnvelope<BruteForceStatus>>("/security/bruteforce/unban", { ipAddress });
  return response.data.data;
}
