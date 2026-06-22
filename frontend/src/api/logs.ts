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
