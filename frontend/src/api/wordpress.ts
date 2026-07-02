import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type WordPressComponent = { name: string; slug: string; version: string; active: boolean };

export type WordPressSecurity = {
  fileEditorDisabled: boolean;
  versionHidden: boolean;
  xmlRpcPingbackDisabled: boolean;
  authorScanBlocked: boolean;
};

export type WordPressSite = {
  siteName: string;
  isWordPress: boolean;
  version: string;
  path: string;
  maintenanceMode: boolean;
  adminUrl: string;
  plugins: WordPressComponent[];
  themes: WordPressComponent[];
  latestVersion: string;
  updateAvailable: boolean;
  security: WordPressSecurity;
};

export async function getWordPress(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<WordPressSite>>(`/wordpress/${encodeURIComponent(siteName)}`);
  return response.data.data;
}

export type WordPressHardenResult = { applied: string[] };

export async function hardenWordPress(siteName: string) {
  const response = await apiClient.post<ApiEnvelope<WordPressHardenResult>>(
    `/wordpress/${encodeURIComponent(siteName)}/harden`,
    {}
  );
  return response.data.data;
}

export async function setWordPressMaintenance(siteName: string, enabled: boolean) {
  const response = await apiClient.post<ApiEnvelope<WordPressSite>>(
    `/wordpress/${encodeURIComponent(siteName)}/maintenance`,
    { enabled }
  );
  return response.data.data;
}

export type WordPressLoginLink = { url: string; expiresInSeconds: number };

export async function createWordPressLoginLink(siteName: string) {
  const response = await apiClient.post<ApiEnvelope<WordPressLoginLink>>(
    `/wordpress/${encodeURIComponent(siteName)}/login-link`,
    {}
  );
  return response.data.data;
}

export type WordPressCloneResult = {
  targetSiteName: string;
  targetUrl: string;
  targetDatabase: string;
  databaseUser: string;
  databasePassword: string;
};

export async function cloneWordPress(siteName: string, payload: { targetSiteName: string; targetHostName: string }) {
  const response = await apiClient.post<ApiEnvelope<WordPressCloneResult>>(
    `/wordpress/${encodeURIComponent(siteName)}/clone`,
    payload
  );
  return response.data.data;
}

export type WordPressCopyDataResult = {
  targetSiteName: string;
  targetUrl: string;
  backupPath: string;
  copiedFiles: boolean;
  copiedDatabase: boolean;
};

export async function copyWordPressData(siteName: string, payload: { targetSiteName: string; files: boolean; database: boolean }) {
  const response = await apiClient.post<ApiEnvelope<WordPressCopyDataResult>>(
    `/wordpress/${encodeURIComponent(siteName)}/copy-data`,
    payload
  );
  return response.data.data;
}
