import { apiClient } from "./client";

type ApiEnvelope<T> = { success: boolean; data: T; message: string; code: number };

export type PanelPreferences = {
  panelName: string;
  defaultLanguage: string;
  themeMode: string;
  uiDensity: string;
  defaultSiteRoot: string;
  defaultBackupRoot: string;
  serverAddress: string;
  preferredHttpsPort: number;
  allowHttpFallback: boolean;
  loginLockoutMinutes: number;
  defaultRuntimeProfile: string;
  installRuntimeIfMissing: boolean;
  restartRequired: boolean;
};

export async function getPanelPreferences() {
  const response = await apiClient.get<ApiEnvelope<PanelPreferences>>("/panel-settings");
  return response.data.data;
}

export async function updatePanelPreferences(payload: PanelPreferences) {
  const response = await apiClient.put<ApiEnvelope<PanelPreferences>>("/panel-settings", payload);
  return response.data.data;
}
