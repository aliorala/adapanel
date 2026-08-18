import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type AutomationSettings = {
  autoBackupEnabled: boolean;
  /**
   * interval | daily | weekly | monthly.
   * `interval` eski davranıştır (son çalışmadan N saat sonra) ve varsayılandır; takvim
   * modlarında yedek SABİT bir saatte alınır ve elle tetikleme o saati kaydırmaz.
   */
  backupScheduleMode: string;
  backupIntervalHours: number;
  /** Takvim modlarında çalışma saati — SUNUCUNUN yerel saati, tarayıcının değil. */
  backupScheduleHour: number;
  backupScheduleMinute: number;
  /** 0 = Pazar … 6 = Cumartesi. */
  backupScheduleDayOfWeek: number;
  /** 1-31; ay o günü çekmiyorsa sunucu ayın son gününü kullanır. */
  backupScheduleDayOfMonth: number;
  backupRetentionDays: number;
  backupSites: boolean;
  backupDatabases: boolean;
  backupMail: boolean;
  backupPanelConfig: boolean;
  backupDatabaseEngines: string[];
  autoSslRenewEnabled: boolean;
  sslRenewIntervalHours: number;
  alertsEnabled: boolean;
  diskUsageThresholdPercent: number;
  certExpiryWarningDays: number;
  memoryUsageThresholdPercent: number;
  serviceDownAlertsEnabled: boolean;
  backupTargetType: string;
  backupRemoteUrl: string;
  backupRemoteUserName: string;
  backupRemoteRegion: string;
  backupRemotePasswordSet: boolean;
  logCleanupEnabled: boolean;
  logRetentionDays: number;
  /** Kurulu bileşenler için günlük güncelleme kontrolü (varsayılan açık). */
  updateCheckEnabled: boolean;
  /** Bulunan güncellemeleri otomatik kur (riskli bileşenler hariç; varsayılan kapalı). */
  autoApplyComponentUpdates: boolean;
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
  lastLogCleanupRunUtc: string | null;
  lastLogCleanupStatus: string;
  lastLogCleanupDetails: string;
  lastUpdateCheckRunUtc: string | null;
  lastUpdateCheckStatus: string;
  lastUpdateCheckDetails: string;
  pendingComponentUpdates: number;
  /** Sunucunun saat dilimi kimliği; yedek saatinin hangi saate göre olduğunu ekran bununla söyler. */
  serverTimeZoneId: string;
  serverTimeZoneDisplayName: string;
  serverUtcOffsetMinutes: number;
  /** Sunucunun şu anki yerel saati (ISO, ofsetsiz). */
  serverLocalTime: string;
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

export type BackupRemoteTestPayload = {
  targetType: string;
  remoteUrl: string;
  remoteUserName: string;
  remotePassword: string;
  remoteRegion: string;
};
export type BackupRemoteTestResult = { success: boolean; message: string; details: string };

// Off-site yedek hedefini gerçek yedek almadan test et (parola boşsa sunucudaki kayıtlı kullanılır).
export async function testRemoteBackup(payload: BackupRemoteTestPayload) {
  const response = await apiClient.post<ApiEnvelope<BackupRemoteTestResult>>("/automation/backup/remote/test", payload);
  return response.data.data;
}
