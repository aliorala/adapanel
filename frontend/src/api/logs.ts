import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type LogTail = {
  name: string;
  path: string;
  lines: string[];
  lastModifiedUtc: string | null;
};

export async function getPanelLog(lines = 120) {
  const response = await apiClient.get<ApiEnvelope<LogTail>>("/logs/panel", {
    params: { lines }
  });

  return response.data.data;
}

export async function getSiteLog(siteName: string, lines = 120) {
  const response = await apiClient.get<ApiEnvelope<LogTail>>(`/logs/site/${encodeURIComponent(siteName)}`, {
    params: { lines }
  });

  return response.data.data;
}

/** Log döndürme politikası. 0 değerleri "kapalı / sınırsız" demektir (panelin diğer saklama ayarlarıyla aynı anlam). */
export type LogRotationSettings = {
  enabled: boolean;
  compress: boolean;
  siteLogMaxAgeDays: number;
  panelLogMaxAgeDays: number;
  maxFileSizeMB: number;
  siteKeepCount: number;
  panelKeepCount: number;
  maxTotalSizeMB: number;
  intervalHours: number;
};

/** Tek bir günlük klasörünün durumu (panel ya da bir sitenin IIS klasörü). */
export type LogRotationTarget = {
  key: string;
  name: string;
  siteName: string;
  directory: string;
  directoryExists: boolean;
  lastRotationUtc: string | null;
  rotatedFiles: number;
  deletedArchives: number;
  freedBytes: number;
  skippedLockedFiles: number;
  skippedUnsafeFiles: number;
  currentLogBytes: number;
  currentArchiveBytes: number;
  status: string;
  error: string;
};

export type LogRotationStatus = {
  settings: LogRotationSettings;
  lastRunUtc: string | null;
  nextRunUtc: string | null;
  targets: LogRotationTarget[];
  totalFreedBytes: number;
};

export async function getLogRotation() {
  const response = await apiClient.get<ApiEnvelope<LogRotationStatus>>("/logs/rotation");
  return response.data.data;
}

export async function updateLogRotation(settings: LogRotationSettings) {
  const response = await apiClient.put<ApiEnvelope<LogRotationStatus>>("/logs/rotation", settings);
  return response.data.data;
}

export async function runLogRotation() {
  const response = await apiClient.post<ApiEnvelope<LogRotationStatus>>("/logs/rotation/run");
  return response.data.data;
}

export async function getSiteLogRotation(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<LogRotationTarget>>(
    `/logs/rotation/site/${encodeURIComponent(siteName)}`);
  return response.data.data;
}

export async function runSiteLogRotation(siteName: string) {
  const response = await apiClient.post<ApiEnvelope<LogRotationTarget>>(
    `/logs/rotation/site/${encodeURIComponent(siteName)}/run`);
  return response.data.data;
}
