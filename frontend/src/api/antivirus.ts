import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
  details?: string;
};

export type AntivirusFinding = {
  path: string;
  signature: string;
};

export type AntivirusScanResult = {
  target: string;
  startedAtUtc: string;
  durationSeconds: number;
  scannedFiles: number;
  infectedCount: number;
  infected: AntivirusFinding[];
  status: string;
  detail: string;
};

export type AntivirusStatus = {
  isInstalled: boolean;
  version: string;
  databaseReady: boolean;
  databaseUpdatedAtUtc: string | null;
  lastScan: AntivirusScanResult | null;
};

export async function getAntivirusStatus() {
  const response = await apiClient.get<ApiEnvelope<AntivirusStatus>>("/antivirus/status");
  return response.data.data;
}

export async function installAntivirus() {
  const response = await apiClient.post<ApiEnvelope<AntivirusStatus>>("/antivirus/install", {});
  return response.data.data;
}

// İlk çağrı ~250 MB tanım indirir; dakikalar sürebilir.
export async function updateAntivirusDefinitions() {
  const response = await apiClient.post<ApiEnvelope<AntivirusStatus>>("/antivirus/definitions/update", {}, { timeout: 35 * 60_000 });
  return response.data.data;
}

export async function scanAntivirus(siteName: string, path = "") {
  const response = await apiClient.post<ApiEnvelope<AntivirusScanResult>>(
    "/antivirus/scan",
    { siteName, path },
    { timeout: 125 * 60_000 });
  return response.data.data;
}
