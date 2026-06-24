import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type SiteBinding = {
  protocol: string;
  hostName: string;
  ipAddress: string;
  port: number;
  bindingInformation: string;
};

export type SiteRecord = {
  id: number;
  name: string;
  state: string;
  autoStart: boolean;
  physicalPath: string;
  applicationRootPath: string;
  applicationPoolName: string;
  bindings: SiteBinding[];
};

export type CreateSitePayload = {
  name: string;
  physicalPath: string;
  applicationPoolName: string;
  hostName: string;
  ipAddress: string;
  port: number;
  protocol: string;
  autoStart: boolean;
  runtimeProfile: string;
  runtimeComponentId: string;
  installRuntimeIfMissing: boolean;
  startupFile: string;
  provisionDefaultServices?: boolean;
  // profesyonel panel benzeri birleşik oluşturma (opsiyonel)
  createDatabase?: boolean;
  databaseEngine?: string;
  databaseName?: string;
  databaseUserName?: string;
  databasePassword?: string;
  createMailDomain?: boolean;
  createFtpUser?: boolean;
  ftpUserName?: string;
  ftpPassword?: string;
};

export type SiteCreationResult = {
  site: SiteRecord;
  created: string[];
  warnings: string[];
  generatedCredentials: {
    service: string;
    userName: string;
    password: string;
  }[];
};

export type SiteBindingPayload = {
  protocol: string;
  hostName: string;
  ipAddress: string;
  port: number;
};

export type SiteDefaultsResult = {
  siteName: string;
  readyFeatures: string[];
  failedFeatures: string[];
  isComplete: boolean;
};

export type SiteDeletionPlan = {
  siteName: string;
  physicalPath: string;
  applicationRootPath: string;
  hasDnsZone: boolean;
  mailDomains: string[];
  ftpUsers: string[];
  databases: string[];
  hasReverseProxy: boolean;
  hasDiskQuota: boolean;
  hasGitDeployment: boolean;
  customerAssignments: number;
};

export type SiteDeletionResult = {
  siteName: string;
  removed: string[];
  warnings: string[];
};

export async function getSites() {
  // Rol-duyarli uc: yonetici tum siteleri, musteri yalnizca atanmis siteleri alir.
  const response = await apiClient.get<ApiEnvelope<SiteRecord[]>>("/scope/sites");
  return response.data.data;
}

export async function getSite(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<SiteRecord>>(`/sites/${encodeURIComponent(siteName)}`);
  return response.data.data;
}

export async function createSite(payload: CreateSitePayload) {
  const response = await apiClient.post<ApiEnvelope<SiteCreationResult>>("/sites", payload);
  return response.data.data;
}

export async function importSites() {
  const response = await apiClient.post<ApiEnvelope<SiteRecord[]>>("/sites/import", {});
  return response.data.data;
}

export type ProtectedDirectory = {
  siteName: string;
  relativePath: string;
  userName: string;
  realm: string;
};

export async function getProtectedDirs(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<ProtectedDirectory[]>>(
    `/sites/${encodeURIComponent(siteName)}/protected-dirs`
  );
  return response.data.data;
}

export async function protectDir(siteName: string, payload: { relativePath: string; userName: string; password: string; realm?: string }) {
  const response = await apiClient.post<ApiEnvelope<ProtectedDirectory>>(
    `/sites/${encodeURIComponent(siteName)}/protected-dirs`,
    payload
  );
  return response.data.data;
}

export async function unprotectDir(siteName: string, relativePath: string) {
  await apiClient.delete(`/sites/${encodeURIComponent(siteName)}/protected-dirs`, {
    params: { relativePath }
  });
}

export async function ensureSiteDefaults(siteName: string) {
  const response = await apiClient.post<ApiEnvelope<SiteDefaultsResult>>(`/sites/${encodeURIComponent(siteName)}/defaults`, {});
  return response.data.data;
}

export async function startSite(siteName: string) {
  const response = await apiClient.post<ApiEnvelope<SiteRecord>>(`/sites/${encodeURIComponent(siteName)}/start`, {});
  return response.data.data;
}

export async function stopSite(siteName: string) {
  const response = await apiClient.post<ApiEnvelope<SiteRecord>>(`/sites/${encodeURIComponent(siteName)}/stop`, {});
  return response.data.data;
}

export async function getSiteDeletionPlan(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<SiteDeletionPlan>>(
    `/sites/${encodeURIComponent(siteName)}/deletion-plan`,
  );
  return response.data.data;
}

export async function deleteSite(
  siteName: string,
  options: { deleteFiles?: boolean; deleteDatabases?: boolean } = {},
) {
  const deleteFiles = options.deleteFiles ?? true;
  const deleteDatabases = options.deleteDatabases ?? true;
  const response = await apiClient.delete<ApiEnvelope<SiteDeletionResult>>(
    `/sites/${encodeURIComponent(siteName)}?deleteFiles=${deleteFiles}&deleteDatabases=${deleteDatabases}`,
  );
  return response.data.data;
}

export async function addBinding(siteName: string, payload: SiteBindingPayload) {
  const response = await apiClient.post<ApiEnvelope<SiteRecord>>(`/sites/${encodeURIComponent(siteName)}/bindings`, payload);
  return response.data.data;
}

export async function removeBinding(siteName: string, payload: SiteBindingPayload) {
  const response = await apiClient.delete<ApiEnvelope<SiteRecord>>(`/sites/${encodeURIComponent(siteName)}/bindings`, {
    params: payload
  });

  return response.data.data;
}
