import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type BackupRecord = {
  id: string;
  name: string;
  type: string;
  source: string;
  path: string;
  format: string;
  createdAtUtc: string;
  size: number;
  status: string;
  targetType: string;
  remoteUri: string;
};

export type BackupTargetPayload = {
  targetType: string;
  remoteUrl: string;
  remoteUserName: string;
  remotePassword: string;
  remoteRegion: string;
};

export async function getBackups() {
  const response = await apiClient.get<ApiEnvelope<BackupRecord[]>>("/backup");
  return response.data.data;
}

export type BackupDatabaseRef = { engine: string; databaseName: string };

export type SiteBackupScope = {
  includeFiles: boolean;
  includeConfig: boolean;
  includeMail: boolean;
  databases: BackupDatabaseRef[];
};

export async function backupSite(siteName: string, scope?: Partial<SiteBackupScope>, target?: BackupTargetPayload) {
  const response = await apiClient.post<ApiEnvelope<BackupRecord>>("/backup/site", {
    siteName,
    includeFiles: scope?.includeFiles ?? true,
    includeConfig: scope?.includeConfig ?? true,
    includeMail: scope?.includeMail ?? false,
    databases: scope?.databases ?? [],
    ...(target ?? {})
  });

  return response.data.data;
}

export async function downloadBackup(backupId: string, fileName: string) {
  const response = await apiClient.get(`/backup/${encodeURIComponent(backupId)}/download`, {
    responseType: "blob"
  });
  const url = window.URL.createObjectURL(response.data as Blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  link.remove();
  window.URL.revokeObjectURL(url);
}

export async function backupMail(target?: BackupTargetPayload) {
  const response = await apiClient.post<ApiEnvelope<BackupRecord>>("/backup/mail", { ...(target ?? {}) });
  return response.data.data;
}

// Panelin kendi durumunu yedekle: adapanel.db + config (DataProtection anahtarları hariç).
export async function backupPanel(target?: BackupTargetPayload) {
  const response = await apiClient.post<ApiEnvelope<BackupRecord>>("/backup/panel", { ...(target ?? {}) });
  return response.data.data;
}

export async function backupDatabase(engine: string, databaseName = "", target?: BackupTargetPayload) {
  const response = await apiClient.post<ApiEnvelope<BackupRecord>>("/backup/db", {
    engine,
    databaseName,
    ...(target ?? {})
  });

  return response.data.data;
}

export type BackupEntry = {
  name: string;
  path: string;
  isDirectory: boolean;
  size: number;
};

export type BackupEntries = {
  backupId: string;
  prefix: string;
  entries: BackupEntry[];
  truncated: boolean;
};

export type RestoreItemsResult = {
  restoredCount: number;
  targetRoot: string;
};

export async function getBackupEntries(backupId: string, prefix = "") {
  const response = await apiClient.get<ApiEnvelope<BackupEntries>>(
    `/backup/${encodeURIComponent(backupId)}/entries`,
    { params: prefix ? { prefix } : {} });
  return response.data.data;
}

export async function restoreBackupItems(backupId: string, items: string[], targetPath = "") {
  const response = await apiClient.post<ApiEnvelope<RestoreItemsResult>>("/backup/restore-items", {
    backupId,
    items,
    targetPath
  });
  return response.data.data;
}

export async function restoreBackup(backupId: string, targetPath: string) {
  const response = await apiClient.post<ApiEnvelope<BackupRecord>>("/backup/restore", {
    backupId,
    targetPath
  });

  return response.data.data;
}
