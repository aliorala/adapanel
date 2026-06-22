import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type AutomationSettings = {
  autoBackupEnabled: boolean;
  backupIntervalHours: number;
  backupRetentionDays: number;
  backupSites: boolean;
  backupDatabases: boolean;
  backupMail: boolean;
  backupDatabaseEngines: string[];
  autoSslRenewEnabled: boolean;
  sslRenewIntervalHours: number;
  alertsEnabled: boolean;
  diskUsageThresholdPercent: number;
  certExpiryWarningDays: number;
  backupTargetType: string;
  backupRemoteUrl: string;
  backupRemoteUserName: string;
  backupRemoteRegion: string;
  backupRemotePasswordSet: boolean;
};

export type UpdateAutomationPayload = AutomationSettings & {
  /** Boş bırakılırsa sunucudaki kayıtlı parola korunur. */
  backupRemotePassword: string;
};

export type AutomationStatus = {
  settings: AutomationSettings;
  lastBackupRunUtc: string | null;
  lastBackupStatus: string;
  lastBackupDetails: string;
  nextBackupRunUtc: string | null;
  lastSslRunUtc: string | null;
  lastSslStatus: string;
  lastSslDetails: string;
  nextSslRunUtc: string | null;
};

export async function getAutomation() {
  const response = await apiClient.get<ApiEnvelope<AutomationStatus>>("/automation");
  return response.data.data;
}

export async function updateAutomation(settings: UpdateAutomationPayload) {
  const response = await apiClient.put<ApiEnvelope<AutomationStatus>>("/automation/settings", settings);
  return response.data.data;
}

export async function runAutomationBackup() {
  const response = await apiClient.post<ApiEnvelope<AutomationStatus>>("/automation/backup/run");
  return response.data.data;
}

export async function runAutomationSsl() {
  const response = await apiClient.post<ApiEnvelope<AutomationStatus>>("/automation/ssl/run");
  return response.data.data;
}
