import { apiClient } from "./client";

type ApiEnvelope<T> = { success: boolean; data: T; message: string; code: number };

export type SiteHostingSettings = {
  siteName: string;
  maximumBandwidthBytesPerSecond: number;
  maximumConnections: number;
  deniedIpAddresses: string[];
  redirectUrl: string;
  directoryBrowsingEnabled: boolean;
  defaultDocuments: string[];
  detailedErrorsEnabled: boolean;
  staticCompressionEnabled: boolean;
  dynamicCompressionEnabled: boolean;
  maximumUploadBytes: number;
  hotlinkProtectionEnabled: boolean;
  allowedReferrerHosts: string[];
  protectedExtensions: string[];
};

export async function getSiteHostingSettings(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<SiteHostingSettings>>(`/sites/${encodeURIComponent(siteName)}/hosting-settings`);
  return response.data.data;
}

export async function updateSiteHostingSettings(siteName: string, payload: Omit<SiteHostingSettings, "siteName">) {
  const response = await apiClient.put<ApiEnvelope<SiteHostingSettings>>(`/sites/${encodeURIComponent(siteName)}/hosting-settings`, payload);
  return response.data.data;
}

export async function runComposerInstall(siteName: string) {
  const response = await apiClient.post<ApiEnvelope<{ exitCode: number; output: string; error: string }>>(
    `/sites/${encodeURIComponent(siteName)}/hosting-settings/composer-install`,
    {}
  );
  return response.data.data;
}

// profesyonel paneller gibi keyfi Composer komutu (update, require monolog/monolog, dump-autoload, vb.).
export async function runComposerCommand(siteName: string, command: string) {
  const response = await apiClient.post<ApiEnvelope<{ exitCode: number; output: string; error: string }>>(
    `/sites/${encodeURIComponent(siteName)}/hosting-settings/composer-command`,
    { command }
  );
  return response.data.data;
}

// Laravel Artisan komutu (migrate, cache:clear, key:generate, config:cache, route:list, vb.).
export async function runArtisanCommand(siteName: string, command: string) {
  const response = await apiClient.post<ApiEnvelope<{ exitCode: number; output: string; error: string }>>(
    `/sites/${encodeURIComponent(siteName)}/hosting-settings/artisan-command`,
    { command }
  );
  return response.data.data;
}

// Node.js komutu (npm install / npm run <script> vb.).
export async function runNodeCommand(siteName: string, command: string) {
  const response = await apiClient.post<ApiEnvelope<{ exitCode: number; output: string; error: string }>>(
    `/sites/${encodeURIComponent(siteName)}/hosting-settings/node-command`,
    { command }
  );
  return response.data.data;
}

// WP-CLI komutu (plugin/tema/çekirdek yönetimi, search-replace, cache flush, vb.).
export async function runWpCliCommand(siteName: string, command: string) {
  const response = await apiClient.post<ApiEnvelope<{ exitCode: number; output: string; error: string }>>(
    `/sites/${encodeURIComponent(siteName)}/hosting-settings/wpcli-command`,
    { command }
  );
  return response.data.data;
}

// Site göçü: kaynak sitenin dosya arşivini (.zip URL) indirip bu siteye açar.
export async function importSiteArchive(siteName: string, archiveUrl: string, cleanFirst: boolean) {
  const response = await apiClient.post<ApiEnvelope<{ siteName: string; filesCopied: number }>>(
    `/sites/${encodeURIComponent(siteName)}/hosting-settings/import-archive`,
    { archiveUrl, cleanFirst }
  );
  return response.data.data;
}

export type RemoteFileMigrationPayload = {
  protocol: string; // ftp | ftps | sftp
  host: string;
  port: number;
  userName: string;
  password: string;
  remotePath: string;
};

export type RemoteFileMigrationResult = {
  siteName: string;
  protocol: string;
  filesDownloaded: number;
  bytesDownloaded: number;
  targetPath: string;
};

// profesyonel goc araci gibi (dosya tarafı): kaynak sunucudan FTP/FTPS/SFTP ile site dosyalarını çeker.
export async function migrateSiteFiles(siteName: string, payload: RemoteFileMigrationPayload) {
  const response = await apiClient.post<ApiEnvelope<RemoteFileMigrationResult>>(
    `/sites/${encodeURIComponent(siteName)}/hosting-settings/migrate-files`,
    payload
  );
  return response.data.data;
}

export type QueueWorkerStatus = { running: boolean; taskName: string };

export async function getQueueWorker(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<QueueWorkerStatus>>(
    `/sites/${encodeURIComponent(siteName)}/hosting-settings/queue`
  );
  return response.data.data;
}

export async function startQueueWorker(siteName: string) {
  const response = await apiClient.post<ApiEnvelope<QueueWorkerStatus>>(
    `/sites/${encodeURIComponent(siteName)}/hosting-settings/queue/start`, {}
  );
  return response.data.data;
}

export async function stopQueueWorker(siteName: string) {
  const response = await apiClient.post<ApiEnvelope<QueueWorkerStatus>>(
    `/sites/${encodeURIComponent(siteName)}/hosting-settings/queue/stop`, {}
  );
  return response.data.data;
}

// Laravel scheduler (dakikalık schedule:run) — durum / başlat / durdur.
export async function getLaravelScheduler(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<QueueWorkerStatus>>(
    `/sites/${encodeURIComponent(siteName)}/hosting-settings/scheduler`
  );
  return response.data.data;
}

export async function startLaravelScheduler(siteName: string) {
  const response = await apiClient.post<ApiEnvelope<QueueWorkerStatus>>(
    `/sites/${encodeURIComponent(siteName)}/hosting-settings/scheduler/start`, {}
  );
  return response.data.data;
}

export async function stopLaravelScheduler(siteName: string) {
  const response = await apiClient.post<ApiEnvelope<QueueWorkerStatus>>(
    `/sites/${encodeURIComponent(siteName)}/hosting-settings/scheduler/stop`, {}
  );
  return response.data.data;
}
