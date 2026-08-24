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
  /**
   * Klonun `wp-config.php`'sine YENİ veritabanı bilgileri yazılabildi mi.
   *
   * `false` ise klon KAYNAĞIN veritabanına bağlanıyor olabilir: staging sanılan sitede yapılan her
   * değişiklik canlı veriyi değiştirir. Sunucu bu durumda `wp.clone_config_not_written` mesaj
   * anahtarını döndürür.
   */
  configRewritten: boolean;
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
  /**
   * Kaynağın `wp-config.php` ayarları hedefe taşınabildi mi.
   *
   * `false` ise taşınamadı ve hedefin KENDİ `wp-config.php`'si yedekten geri konuldu: bağlantı
   * doğru (hedef kendi veritabanında), yalnız kaynağa özel ayarlar taşınmadı. Sunucu bu durumda
   * `wp.copy_config_not_merged` mesaj anahtarını döndürür.
   *
   * Tehlikeli olan durum — geri yazımın hiç yapılamayacağı yapılandırma — kopyalama BAŞLAMADAN
   * `wp.copy_config_not_rewritable` hatasıyla reddedilir, yani buraya hiç ulaşmaz.
   */
  configRewritten: boolean;
};

export async function copyWordPressData(siteName: string, payload: { targetSiteName: string; files: boolean; database: boolean }) {
  const response = await apiClient.post<ApiEnvelope<WordPressCopyDataResult>>(
    `/wordpress/${encodeURIComponent(siteName)}/copy-data`,
    payload
  );
  return response.data.data;
}

/** Güncellenebilir öğe (çekirdek, eklenti ya da tema). */
export type WordPressUpdatable = {
  /** "core" | "plugin" | "theme" */
  kind: string;
  name: string;
  title: string;
  currentVersion: string;
  availableVersion: string;
  updateAvailable: boolean;
  status: string;
};

export type WordPressUpdates = {
  siteName: string;
  /**
   * Liste OKUNABİLDİ mi. false ise boş liste "güncelleme yok" DEMEK DEĞİLDİR — wp-cli
   * çalışamamış olabilir. Ölçememeyi "her şey güncel" saymak, açığı olan bir siteyi güvende
   * göstermek olurdu.
   */
  measured: boolean;
  error: string;
  items: WordPressUpdatable[];
};

export type WordPressUpdateItemResult = {
  kind: string;
  name: string;
  success: boolean;
  message: string;
};

export async function getWordPressUpdates(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<WordPressUpdates>>(
    `/wordpress/${encodeURIComponent(siteName)}/updates`
  );
  return response.data.data;
}

export async function applyWordPressUpdates(
  siteName: string,
  payload: { core: boolean; plugins: string[]; themes: string[] }
) {
  const response = await apiClient.post<ApiEnvelope<{ siteName: string; results: WordPressUpdateItemResult[] }>>(
    `/wordpress/${encodeURIComponent(siteName)}/updates`,
    payload
  );
  return response.data.data;
}
