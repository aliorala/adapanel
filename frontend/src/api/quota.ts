import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type SiteDiskQuota = {
  siteName: string;
  path: string;
  fsrmAvailable: boolean;
  quotaEnabled: boolean;
  limitBytes: number;
  usedBytes: number;
  softLimit: boolean;
};

export async function getQuota(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<SiteDiskQuota>>(`/sites/${encodeURIComponent(siteName)}/quota`);
  return response.data.data;
}

export async function setQuota(siteName: string, limitMegabytes: number, softLimit: boolean) {
  const response = await apiClient.put<ApiEnvelope<SiteDiskQuota>>(`/sites/${encodeURIComponent(siteName)}/quota`, {
    limitMegabytes,
    softLimit
  });
  return response.data.data;
}

export async function removeQuota(siteName: string) {
  const response = await apiClient.delete<ApiEnvelope<SiteDiskQuota>>(`/sites/${encodeURIComponent(siteName)}/quota`);
  return response.data.data;
}
