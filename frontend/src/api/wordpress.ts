import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type WordPressComponent = { name: string; slug: string; version: string };

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
};

export async function getWordPress(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<WordPressSite>>(`/wordpress/${encodeURIComponent(siteName)}`);
  return response.data.data;
}

export async function setWordPressMaintenance(siteName: string, enabled: boolean) {
  const response = await apiClient.post<ApiEnvelope<WordPressSite>>(
    `/wordpress/${encodeURIComponent(siteName)}/maintenance`,
    { enabled }
  );
  return response.data.data;
}
