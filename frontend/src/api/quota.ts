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

/**
 * Disk kotası için gereken Windows rolünü (FSRM) kurar.
 *
 * Arayüz "önce FSRM özelliğini ekleyin" diyordu ama panelde ekleme yolu yoktu — çıkmaz sokaktı.
 * Panel diğer Windows rollerini (DNS, FTP, ASP.NET) zaten kendisi kuruyor.
 */
export async function installFsrm(siteName: string) {
  const response = await apiClient.post<ApiEnvelope<SiteDiskQuota>>(
    `/sites/${encodeURIComponent(siteName)}/quota/install`,
    {}
  );
  return response.data.data;
}
