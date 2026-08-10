import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

/** Geri yüklemenin yarım kalan bir adımı (`messageKey` çevrilir, `detail` teşhis içindir). */
export type BackupRestoreWarning = {
  messageKey: string;
  target: string;
  detail: string;
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
  /** AES-256-GCM ile şifreli mi (dosya adı `.zip.enc`). Geri yükleme parola ister. */
  encrypted: boolean;
  warnings: BackupRestoreWarning[];
  preservedPath: string;
};

export type BackupTargetPayload = {
  targetType: string;
  remoteUrl: string;
  remoteUserName: string;
  remotePassword: string;
  remoteRegion: string;
  /**
   * Bu yedeğe özel şifreleme parolası. Boş bırakılırsa panelde KAYITLI parola kullanılır;
   * o da yoksa yedek ŞİFRESİZ alınır.
   *
   * PAROLA KAYBOLURSA YEDEK GERİ GETİRİLEMEZ: anahtar yalnız bu paroladan türetilir.
   */
  encryptionPassword: string;
};

/** Panelin varsayılan yedek şifreleme ayarı. Parola hiçbir zaman DÖNMEZ. */
export type BackupEncryptionSettings = {
  enabled: boolean;
  /**
   * Kayıtlı parola çözülebiliyor mu. `enabled` true ama bu false ise kayıt ÇÖZÜLEMİYOR
   * (ör. panel başka makineye taşındı) — zamanlanmış yedekler şifrelenemez.
   */
  passwordUsable: boolean;
  updatedAtUtc: string;
  algorithm: string;
};

export async function getBackupEncryption() {
  const response = await apiClient.get<ApiEnvelope<BackupEncryptionSettings>>("/backup/encryption");
  return response.data.data;
}

/** Boş parola = şifrelemeyi kapat ve kaydı sil. */
export async function setBackupEncryption(password: string) {
  const response = await apiClient.put<ApiEnvelope<BackupEncryptionSettings>>("/backup/encryption", { password });
  return response.data.data;
}

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

export async function backupSite(siteName: string, scope?: Partial<SiteBackupScope>, target?: Partial<BackupTargetPayload>) {
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

// Yedeği siler (yerel dosya + kayıt; off-site uzak kopya hedef depoda kalır).
export async function deleteBackup(backupId: string) {
  await apiClient.delete<ApiEnvelope<object>>(`/backup/${encodeURIComponent(backupId)}`);
}

export async function backupMail(target?: Partial<BackupTargetPayload>) {
  const response = await apiClient.post<ApiEnvelope<BackupRecord>>("/backup/mail", { ...(target ?? {}) });
  return response.data.data;
}

// Panelin kendi durumunu yedekle: adapanel.db + config (DataProtection anahtarları hariç).
export async function backupPanel(target?: Partial<BackupTargetPayload>) {
  const response = await apiClient.post<ApiEnvelope<BackupRecord>>("/backup/panel", { ...(target ?? {}) });
  return response.data.data;
}

export async function backupDatabase(engine: string, databaseName = "", target?: Partial<BackupTargetPayload>) {
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

export async function restoreBackupItems(backupId: string, items: string[], targetPath = "", encryptionPassword = "") {
  const response = await apiClient.post<ApiEnvelope<RestoreItemsResult>>("/backup/restore-items", {
    backupId,
    items,
    targetPath,
    encryptionPassword
  });
  return response.data.data;
}

export async function restoreBackup(backupId: string, targetPath: string, encryptionPassword = "") {
  const response = await apiClient.post<ApiEnvelope<BackupRecord>>("/backup/restore", {
    backupId,
    targetPath,
    encryptionPassword
  });

  return response.data.data;
}
