import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type SiteBinding = {
  protocol: string;
  hostName: string;
  ipAddress: string;
  port: number;
  bindingInformation: string;
};

export type SiteRecord = {
  id: number;
  name: string;
  state: string;
  autoStart: boolean;
  physicalPath: string;
  applicationRootPath: string;
  applicationPoolName: string;
  bindings: SiteBinding[];
};

// #1 Belge kökü / web root: IIS fiziksel yolunu değiştir (Laravel/Symfony için ...\public). IIS'te kalıcı.
export async function updateDocumentRoot(siteName: string, physicalPath: string) {
  const response = await apiClient.put<ApiEnvelope<SiteRecord>>(
    `/sites/${encodeURIComponent(siteName)}/document-root`,
    { physicalPath }
  );
  return response.data.data;
}

// #2 Laravel/WordPress pretty-URL yönlendirmesi (index.php front controller). URL Rewrite modülü gerekir.
export async function enableFrameworkRewrite(siteName: string) {
  const response = await apiClient.post<ApiEnvelope<SiteRecord>>(`/sites/${encodeURIComponent(siteName)}/framework-rewrite`, {});
  return response.data.data;
}

// #5 onar: mevcut siteye yazma iznini (app pool kök Modify) yeniden uygula.
export async function repairSitePermissions(siteName: string) {
  const response = await apiClient.post<ApiEnvelope<SiteRecord>>(`/sites/${encodeURIComponent(siteName)}/repair-permissions`, {});
  return response.data.data;
}

export type CreateSitePayload = {
  name: string;
  physicalPath: string;
  applicationPoolName: string;
  hostName: string;
  ipAddress: string;
  port: number;
  protocol: string;
  autoStart: boolean;
  runtimeProfile: string;
  runtimeComponentId: string;
  installRuntimeIfMissing: boolean;
  startupFile: string;
  provisionDefaultServices?: boolean;
  // profesyonel panel benzeri birleşik oluşturma (opsiyonel)
  createDatabase?: boolean;
  databaseEngine?: string;
  databaseName?: string;
  databaseUserName?: string;
  databasePassword?: string;
  createMailDomain?: boolean;
  createFtpUser?: boolean;
  ftpUserName?: string;
  ftpPassword?: string;
};

export type SiteCreationResult = {
  site: SiteRecord;
  created: string[];
  warnings: string[];
  generatedCredentials: {
    service: string;
    userName: string;
    password: string;
  }[];
};

export type SiteBindingPayload = {
  protocol: string;
  hostName: string;
  ipAddress: string;
  port: number;
};

export type SiteDefaultsResult = {
  siteName: string;
  readyFeatures: string[];
  failedFeatures: string[];
  isComplete: boolean;
};

export type SiteDeletionPlan = {
  siteName: string;
  physicalPath: string;
  applicationRootPath: string;
  hasDnsZone: boolean;
  mailDomains: string[];
  ftpUsers: string[];
  databases: string[];
  hasReverseProxy: boolean;
  hasDiskQuota: boolean;
  hasGitDeployment: boolean;
  customerAssignments: number;
  // Sitenin klasöründen çalışan zamanlanmış görev sayısı; site silinse de tetiklenmeye
  // devam edecekleri için kaldırılırlar.
  scheduledTasks: number;
};

export type SiteDeletionResult = {
  siteName: string;
  removed: string[];
  warnings: string[];
  // Dosya klasörünün NE KADARININ silindiği ölçülebildi mi. Sunucudaki sayım bütçelidir; iki
  // ölçüm de tavana dayanırsa panel "silindi" de "silinemedi" de diyemez ve bunu SÖYLEMEK
  // zorundadır (bkz. SiteDeletionResultDto.FilesDeletionMeasured).
  filesDeletionMeasured: boolean;
  // Ölçümün neden kesildiği (files.size_truncated_*); ölçüm tamsa boş. Boş olması "tam" demek
  // değildir — bakılacak alan yukarıdakidir.
  filesDeletionTruncatedReasonKey: string;
};

export async function getSites() {
  // Rol-duyarli uc: yonetici tum siteleri, musteri yalnizca atanmis siteleri alir.
  const response = await apiClient.get<ApiEnvelope<SiteRecord[]>>("/scope/sites");
  return response.data.data;
}

export async function getSite(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<SiteRecord>>(`/sites/${encodeURIComponent(siteName)}`);
  return response.data.data;
}

export type SitePreview = {
  url: string;
  previewHost: string;
  method: string;
  realHost: string;
  serverAddress: string;
  hostsLine: string;
  bindingAdded: boolean;
};

export async function getSitePreview(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<SitePreview>>(`/sites/${encodeURIComponent(siteName)}/preview`);
  return response.data.data;
}

export async function createSite(payload: CreateSitePayload) {
  const response = await apiClient.post<ApiEnvelope<SiteCreationResult>>("/sites", payload);
  return response.data.data;
}

export async function importSites() {
  const response = await apiClient.post<ApiEnvelope<SiteRecord[]>>("/sites/import", {});
  return response.data.data;
}

export type ProtectedDirectory = {
  siteName: string;
  relativePath: string;
  userName: string;
  realm: string;
  /**
   * Koruma İSTEKLERE UYGULANIYOR mu — ölçülmüş değer.
   *
   * Kayıt var olduğu hâlde `false` olabilir: doğrulamayı AdaPanel'in IIS modülü yapıyor ve
   * modül kayıtlı değilse koruma kurulur ama hiçbir isteği durdurmaz. Bunu göstermemek,
   * kullanıcıya korunmayan bir klasörü korunuyor diye sunmak olurdu.
   */
  isActive: boolean;
  /**
   * Parola hâlâ ESKİ biçimde, klasörün içinde DÜZ METİN duruyor.
   *
   * Eski sürümler parolayı sitenin kendi `web.config`'ine base64 ile yazıyordu — base64
   * gizleme değil kodlamadır, yani müşterinin FTP'si, PHP betiği ya da site yedeği parolayı
   * okuyabiliyordu. Dönüşüm otomatik ama modül kayıtlı olmadan yapılamaz (çalışan korumayı
   * kaldırıp yerine hiçbir şey koymamak olurdu), o yüzden bu durum GÖRÜNÜR kalır.
   */
  isLegacyPlaintext: boolean;
};

export async function getProtectedDirs(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<ProtectedDirectory[]>>(
    `/sites/${encodeURIComponent(siteName)}/protected-dirs`
  );
  return response.data.data;
}

export async function protectDir(siteName: string, payload: { relativePath: string; userName: string; password: string; realm?: string }) {
  const response = await apiClient.post<ApiEnvelope<ProtectedDirectory>>(
    `/sites/${encodeURIComponent(siteName)}/protected-dirs`,
    payload
  );
  return response.data.data;
}

export async function unprotectDir(siteName: string, relativePath: string) {
  await apiClient.delete(`/sites/${encodeURIComponent(siteName)}/protected-dirs`, {
    params: { relativePath }
  });
}

export async function ensureSiteDefaults(siteName: string) {
  const response = await apiClient.post<ApiEnvelope<SiteDefaultsResult>>(`/sites/${encodeURIComponent(siteName)}/defaults`, {});
  return response.data.data;
}

export async function startSite(siteName: string) {
  const response = await apiClient.post<ApiEnvelope<SiteRecord>>(`/sites/${encodeURIComponent(siteName)}/start`, {});
  return response.data.data;
}

export async function stopSite(siteName: string) {
  const response = await apiClient.post<ApiEnvelope<SiteRecord>>(`/sites/${encodeURIComponent(siteName)}/stop`, {});
  return response.data.data;
}

export async function getSiteDeletionPlan(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<SiteDeletionPlan>>(
    `/sites/${encodeURIComponent(siteName)}/deletion-plan`,
  );
  return response.data.data;
}

export type SiteDatabaseRef = { engine: string; name: string };
export type SiteDatabases = { linked: SiteDatabaseRef[]; candidates: SiteDatabaseRef[] };

// Sitenin veritabanı görünümü: bağlı olanlar + bağlanabilecek adaylar (seçerek bağlamak için).
export async function getSiteDatabases(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<SiteDatabases>>(
    `/sites/${encodeURIComponent(siteName)}/databases`,
  );
  return response.data.data;
}

// Mevcut bir veritabanını siteye bağla (seçerek; ad eşleşmesi gerekmez).
export async function linkSiteDatabase(siteName: string, engine: string, name: string) {
  await apiClient.post<ApiEnvelope<object>>(
    `/sites/${encodeURIComponent(siteName)}/databases/link`,
    { engine, name },
  );
}

// Veritabanının siteyle bağını kaldır (veritabanını SİLMEZ).
export async function unlinkSiteDatabase(siteName: string, engine: string, name: string) {
  await apiClient.delete<ApiEnvelope<object>>(
    `/sites/${encodeURIComponent(siteName)}/databases/link`,
    { params: { engine, name } },
  );
}


export async function deleteSite(
  siteName: string,
  options: { deleteFiles?: boolean; deleteDatabases?: boolean } = {},
) {
  const deleteFiles = options.deleteFiles ?? true;
  const deleteDatabases = options.deleteDatabases ?? true;
  const response = await apiClient.delete<ApiEnvelope<SiteDeletionResult>>(
    `/sites/${encodeURIComponent(siteName)}?deleteFiles=${deleteFiles}&deleteDatabases=${deleteDatabases}`,
  );
  return response.data.data;
}

export async function addBinding(siteName: string, payload: SiteBindingPayload) {
  const response = await apiClient.post<ApiEnvelope<SiteRecord>>(`/sites/${encodeURIComponent(siteName)}/bindings`, payload);
  return response.data.data;
}

export async function removeBinding(siteName: string, payload: SiteBindingPayload) {
  const response = await apiClient.delete<ApiEnvelope<SiteRecord>>(`/sites/${encodeURIComponent(siteName)}/bindings`, {
    params: payload
  });

  return response.data.data;
}
