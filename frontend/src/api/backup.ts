import { apiClient } from "./client";
// Çalışan-iş sözleşmesi TEK YERDE tanımlı (composables/useRunningTask.ts): dört modül aynı
// gövdeyi döndürüyor ve tipi her sarmalayıcıda yeniden yazmak, bir alan eklendiğinde üçünün
// bayatlaması demekti.
import type { ServerRunningJobs } from "../composables/useRunningTask";

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

  // --- ARTIMSAL ZİNCİR (Plesk modeli) ---
  // Eski kayıtlarda bu alanlar BOŞ gelir ve boş değerler "zincirsiz tekil tam yedek" demektir;
  // ekran bu yüzden `chainId` doluluğuna bakar, `backupMode`a değil.

  /** Zincir kimliği; boş = zincire ait değil (kendi başına yeterli tam yedek). */
  chainId: string;
  /** 0 = TAM yedek (zincir başı), 1..n = artımsal halkalar. */
  chainIndex: number;
  /** "full" | "incremental". */
  backupMode: string;
  /** Artımsal halkanın dayandığı önceki halka; tam yedekte boş. */
  baseBackupId: string;
  /** Abonelik yedeğinde sahibinin kullanıcı kimliği ("N" biçimi, tiresiz). */
  subscriptionUserId: string;
  /** Arşivdeki mantıksal öğe sayısı (site + veritabanı + posta alanı + FTP); 0 = ölçülmedi. */
  itemCount: number;

  // --- UZLAŞTIRMA: yedek GERÇEKTEN duruyor mu? ---
  // Kayıt listede görünüp dosyası olmayan bir yedek, panelin verebileceği en pahalı yalandır;
  // kullanıcı bunu normalde ancak geri yüklemeye çalışırken öğrenirdi.

  /** Yerel dosya listeleme ANINDA yok (sunucu her listelemede ölçer; kayda yazılmaz). */
  localMissing: boolean;
  /**
   * Uzak kopya SON DOĞRULAMADA yoktu. `false` "yok değil" değil, "ölçülmedi" de olabilir —
   * ölçülüp ölçülmediği `remoteVerifiedUtc` ile anlaşılır.
   */
  remoteMissing: boolean;
  /** Uzak kopyanın en son ne zaman ölçüldüğü; null = hiç doğrulanmadı. */
  remoteVerifiedUtc: string | null;
};

/** "Yedekleri doğrula" sonucunda sorunlu bulunan tek bir kayıt. */
export type BackupVerificationIssue = {
  backupId: string;
  name: string;
  source: string;
  targetType: string;
  /** i18n anahtarı: `backup.verify.missing_local` | `..missing_remote` | `..unreachable`. */
  messageKey: string;
  detail: string;
};

/** Hedefte duran ama panelde kaydı olmayan dosya (ters yetim). */
export type BackupOrphan = {
  remotePath: string;
  targetType: string;
};

export type BackupVerificationResult = {
  checked: number;
  missingLocal: number;
  missingRemote: number;
  /**
   * Hedefe ULAŞILAMADIĞI için karar verilemeyen kayıt sayısı. Bunlar KAYIP DEĞİLDİR —
   * geçici bir ağ arızasını "yedek silinmiş" saymak bütün geçmişi kayıp gösterirdi.
   */
  unreachable: number;
  issues: BackupVerificationIssue[];
  orphans: BackupOrphan[];
  /** Yetim taraması YAPILAMAYAN hedefler; boş yetim listesi "yetim yok" demek değildir. */
  orphanScanSkipped: string[];
};

/** Zincirdeki tek bir halka. */
export type BackupChainLink = {
  backupId: string;
  name: string;
  chainIndex: number;
  backupMode: "full" | "incremental";
  createdAtUtc: string;
  size: number;
  /** Halkanın dosyası GERÇEKTEN yerinde mi (kayıt duruyor ama dosya silinmiş olabilir). */
  available: boolean;
};

/**
 * Bir yedeğin geri yüklenebilmesi için gereken zincirin çözümü.
 *
 * `broken` ise geri yükleme BAŞLATILMAMALI: sunucu da `backup.chain_broken` ile reddeder, ama
 * kullanıcı bunu düğmeye basmadan ÖNCE görmelidir.
 */
export type BackupChain = {
  chainId: string;
  /** TAM yedekten hedefe SIRALI halkalar. */
  links: BackupChainLink[];
  broken: boolean;
  /** Kopuksa eksik halkaların adı ya da "#2" gibi sıra numarası. */
  missingLinks: string[];
};

export type BackupDeleteResult = {
  deletedIds: string[];
  deletedNames: string[];
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

/**
 * Kayıtların GERÇEKTEN var olup olmadığını ölçer ve hedefte duran ama kaydı olmayan (yetim)
 * dosyaları listeler. HİÇBİR ŞEYİ SİLMEZ.
 *
 * Ayrı bir düğmeye bağlıdır çünkü uzak ölçüm kayıt başına bir AĞ isteğidir; listelemeye
 * bağlansaydı panel her açılışta yavaşlar ve hedefin hız sınırına takılırdı.
 */
export async function verifyBackups() {
  const response = await apiClient.post<ApiEnvelope<BackupVerificationResult>>("/backup/verify", {});
  return response.data.data;
}

export type BackupDatabaseRef = { engine: string; databaseName: string };

export type SiteBackupScope = {
  includeFiles: boolean;
  includeConfig: boolean;
  includeMail: boolean;
  databases: BackupDatabaseRef[];
  /** "Mümkünse artımsal": ilk yedek ve tam yedek periyodu dolduğunda yine TAM alınır. */
  incremental: boolean;
  /** Artımsal zincirin kaç günde bir TAM yedekle yenileneceği (1..365). */
  fullBackupEveryDays: number;
};

export async function backupSite(siteName: string, scope?: Partial<SiteBackupScope>, target?: Partial<BackupTargetPayload>) {
  const response = await apiClient.post<ApiEnvelope<BackupRecord>>("/backup/site", {
    siteName,
    includeFiles: scope?.includeFiles ?? true,
    includeConfig: scope?.includeConfig ?? true,
    includeMail: scope?.includeMail ?? false,
    databases: scope?.databases ?? [],
    incremental: scope?.incremental ?? false,
    fullBackupEveryDays: scope?.fullBackupEveryDays ?? 7,
    ...(target ?? {})
  });

  return response.data.data;
}

/** Abonelik yedeğinin kapsamı; hepsi kapatılırsa arşivde manifestten başka bir şey kalmaz. */
export type SubscriptionBackupScope = {
  includeFiles: boolean;
  includeDatabases: boolean;
  includeMail: boolean;
  includeFtp: boolean;
  incremental: boolean;
  fullBackupEveryDays: number;
};

/**
 * ABONELİK yedeği: müşterinin bütün siteleri + veritabanları + posta alan adları + FTP hesapları
 * TEK arşivde.
 *
 * `userId` ZORUNLU: kapsamlı kullanıcı (müşteri/bayi) başka bir abonelik kimliği yazamaz —
 * sunucu `403 auth.subscription_forbidden` döner.
 */
export async function backupSubscription(
  userId: string,
  scope?: Partial<SubscriptionBackupScope>,
  target?: Partial<BackupTargetPayload>
) {
  const response = await apiClient.post<ApiEnvelope<BackupRecord>>("/backup/subscription", {
    userId,
    includeFiles: scope?.includeFiles ?? true,
    includeDatabases: scope?.includeDatabases ?? true,
    includeMail: scope?.includeMail ?? true,
    includeFtp: scope?.includeFtp ?? true,
    incremental: scope?.incremental ?? false,
    fullBackupEveryDays: scope?.fullBackupEveryDays ?? 7,
    ...(target ?? {})
  });

  return response.data.data;
}

/** Yedeğin zinciri: TAM yedekten bu yedeğe kadar halkalar + kopuk mu. */
export async function getBackupChain(backupId: string) {
  const response = await apiClient.get<ApiEnvelope<BackupChain>>(`/backup/${encodeURIComponent(backupId)}/chain`);
  return response.data.data;
}

/**
 * Yedeği VE ona bağımlı bütün artımsal halkaları siler.
 *
 * Yalnız kullanıcı neyi kaybedeceğini GÖRDÜKTEN sonra çağrılır: tek yedek silme
 * (`deleteBackup`) bağımlı halka varsa `409 backup.chain_has_dependents` ile reddedilir.
 */
export async function deleteBackupChain(backupId: string) {
  const response = await apiClient.delete<ApiEnvelope<BackupDeleteResult>>(
    `/backup/${encodeURIComponent(backupId)}/chain`);
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

// =====================================================================================
// ABONELİK BAŞINA YEDEK PROGRAMI
// =====================================================================================

/**
 * Tek bir aboneliğin kendi yedek programı.
 *
 * `intervalHours` sunucudan KOTAYA ÇEKİLMİŞ hâliyle döner (istenen değerle değil); ekran formu
 * her zaman DÖNEN değerle tazelemelidir, yoksa kullanıcı kabul edilmemiş bir sıklığı kayıtlı sanır.
 */
export type SubscriptionBackupSchedule = {
  userId: string;
  /** Boş olabilir (ad çözülemedi); ekran kimliğe düşmelidir. */
  userName: string;
  enabled: boolean;
  /**
   * interval | daily | weekly | monthly. Sunucu geneli programla AYNI kavram; `interval`
   * varsayılandır, yani mevcut programlar hiç değişmeden sürer.
   */
  scheduleMode: string;
  intervalHours: number;
  /** Takvim modlarında çalışma saati — SUNUCUNUN yerel saati. */
  scheduleHour: number;
  scheduleMinute: number;
  /** 0 = Pazar … 6 = Cumartesi. */
  scheduleDayOfWeek: number;
  /** 1-31; ay o günü çekmiyorsa sunucu ayın son gününü kullanır. */
  scheduleDayOfMonth: number;
  /** 0 = süreye göre silme yok. */
  retentionDays: number;
  /** 0 = sayıya göre silme yok. */
  keepCount: number;
  incremental: boolean;
  fullBackupEveryDays: number;
  includeFiles: boolean;
  includeDatabases: boolean;
  includeMail: boolean;
  includeFtp: boolean;
  targetType: string;
  remoteUrl: string;
  remoteUserName: string;
  remoteRegion: string;
  /** Parolanın KENDİSİ asla dönmez; yalnız kayıtlı olup olmadığı bilinir. */
  remotePasswordSet: boolean;
  lastRunUtc: string | null;
  /** "" | "completed" | "completed_with_errors" | "failed". */
  lastStatus: string;
  /** İnsan okur özet; kısmi başarısızlıkta hangi öğenin neden alınamadığı burada. */
  lastDetails: string;
  nextRunUtc: string | null;
  planId: string;
  planName: string;
  /** Planın günlük yedek sınırı (0 = sınırsız). */
  maxBackupsPerDay: number;
  /** İstenen sıklık plan kotasına ÇEKİLDİ mi. */
  throttledByQuota: boolean;
};

export type SubscriptionBackupSchedulePayload = {
  enabled: boolean;
  scheduleMode: string;
  intervalHours: number;
  scheduleHour: number;
  scheduleMinute: number;
  scheduleDayOfWeek: number;
  scheduleDayOfMonth: number;
  retentionDays: number;
  keepCount: number;
  incremental: boolean;
  fullBackupEveryDays: number;
  includeFiles: boolean;
  includeDatabases: boolean;
  includeMail: boolean;
  includeFtp: boolean;
  targetType: string;
  remoteUrl: string;
  remoteUserName: string;
  remoteRegion: string;
  /** BOŞ gönderilirse KAYITLI parola korunur (silinmez); ekran parolayı geri okuyamaz. */
  remotePassword: string;
};

export async function getBackupSchedules() {
  const response = await apiClient.get<ApiEnvelope<SubscriptionBackupSchedule[]>>("/backup/schedules");
  return response.data.data;
}

/** Programı olmayan abonelikte VARSAYILANLARLA dolu bir kayıt döner (yeni program formu için). */
export async function getBackupSchedule(userId: string) {
  const response = await apiClient.get<ApiEnvelope<SubscriptionBackupSchedule>>(
    `/backup/schedules/${encodeURIComponent(userId)}`);
  return response.data.data;
}

export async function updateBackupSchedule(userId: string, payload: SubscriptionBackupSchedulePayload) {
  const response = await apiClient.put<ApiEnvelope<SubscriptionBackupSchedule>>(
    `/backup/schedules/${encodeURIComponent(userId)}`, payload);
  return response.data.data;
}

export async function deleteBackupSchedule(userId: string) {
  await apiClient.delete<ApiEnvelope<object>>(`/backup/schedules/${encodeURIComponent(userId)}`);
}

/** Programı beklemeden şimdi çalıştırır (günlük kota bu yolu engellemez). */
export async function runBackupScheduleNow(userId: string) {
  const response = await apiClient.post<ApiEnvelope<SubscriptionBackupSchedule>>(
    `/backup/schedules/${encodeURIComponent(userId)}/run`, {});
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

/**
 * Bu sunucuda ŞU AN süren yedekleme/geri yükleme işleri + yarıda kalmış olanlar.
 *
 * NEDEN VAR: yedek uçları işi arka plana almaz — istek iş bitene kadar açık kalır. Sayfa
 * yenilendiğinde iş sunucuda DEVAM eder ama panelde "sürüyor" bilgisi kayboluyordu; kullanıcı
 * ekranı donmuş sanıp yedeği ikinci kez tetikliyordu. Bu uç o bilgiyi geri okutur.
 *
 * YÜZDE ALANI YOK ve olmayacak: yedekleme toplam dosya sayısını sıkıştırmaya başlamadan bilmez.
 * Yüzde göstermenin tek yolu onu UYDURMAK olurdu. Ölçülebilen iki şey veriliyor: işin konusu ve
 * sunucudaki başlangıç anı (bir de sunucunun kendi saati, saat sapması düşsün diye).
 */
export async function getBackupRunningJobs() {
  const response = await apiClient.get<ApiEnvelope<ServerRunningJobs>>("/backup/running");
  return response.data.data;
}
