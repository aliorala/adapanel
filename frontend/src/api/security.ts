import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type SiteSecurityConfiguration = {
  siteName: string;
  applicationPoolName: string;
  physicalPath: string;
  isIsolated: boolean;
  folderAclApplied: boolean;
  basicRulesEnabled: boolean;
  requestsPerMinute: number;
  principal: string;
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
