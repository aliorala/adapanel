import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type PhpVersionRecord = {
  componentId: string;
  displayName: string;
  version: string;
  installDir: string;
  executablePath: string;
  status: string;
  isFastCgiRegistered: boolean;
  installedAtUtc: string;
};

export type SitePhpAssignment = {
  siteName: string;
  componentId: string;
  displayName: string;
  version: string;
  executablePath: string;
  handlerName: string;
  isFastCgiRegistered: boolean;
};

export async function getPhpVersions() {
  const response = await apiClient.get<ApiEnvelope<PhpVersionRecord[]>>("/php/versions");
  return response.data.data;
}

export async function installPhpVersion(componentId: string) {
  const response = await apiClient.post<ApiEnvelope<object>>("/php/install", { componentId });
  return response.data.message;
}

export async function setSitePhpVersion(siteName: string, componentId: string) {
  const response = await apiClient.post<ApiEnvelope<SitePhpAssignment>>(`/sites/${encodeURIComponent(siteName)}/php`, {
    componentId
  });

  return response.data.data;
}

export type SitePhpSettings = {
  siteName: string;
  memoryLimit: string;
  maxExecutionTime: number;
  uploadMaxFilesize: string;
  postMaxSize: string;
  displayErrors: boolean;
  openBasedirEnabled?: boolean;
  configured: boolean;
  redisEnabled?: boolean;
};

export async function getSitePhpSettings(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<SitePhpSettings>>(`/sites/${encodeURIComponent(siteName)}/php/settings`);
  return response.data.data;
}

export async function updateSitePhpSettings(siteName: string, payload: Omit<SitePhpSettings, "siteName" | "configured">) {
  const response = await apiClient.post<ApiEnvelope<SitePhpSettings>>(`/sites/${encodeURIComponent(siteName)}/php/settings`, payload);
  return response.data.data;
}

// Native phpredis (redis) PHP uzantısını etkinleştir (Laravel "Class Redis not found" hatasını çözer).
export async function enableRedisExtension() {
  const response = await apiClient.post<ApiEnvelope<{ enabled: boolean; message: string; fileName: string }>>("/php/enable-redis", {});
  return response.data.data;
}

// GENEL php.ini ayarları (phpMyAdmin/Adminer dahil tüm PHP araçlarının yükleme/süre limitleri).
export async function getGlobalPhpSettings() {
  const response = await apiClient.get<ApiEnvelope<SitePhpSettings>>("/php/global-settings");
  return response.data.data;
}

export async function updateGlobalPhpSettings(payload: Omit<SitePhpSettings, "siteName" | "configured">) {
  const response = await apiClient.post<ApiEnvelope<SitePhpSettings>>("/php/global-settings", payload);
  return response.data.data;
}
