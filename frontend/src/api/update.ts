import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type UpdateStatus = {
  currentVersion: string;
  isUpdateAvailable: boolean;
  /** Sürüm bilgisi GitHub'dan alınamadı; aşağıdaki sürüm önbellekten. "Güncelsiniz" DENMEZ. */
  isCheckFailed: boolean;
  latestVersion: string;
  downloadUrl: string;
  packageSha256: string;
  packagePath: string;
  isPackageReady: boolean;
  isInstallScheduled: boolean;
  installScheduledAtUtc: string | null;
  notes: string;
  githubRepo: string;
  checkedAtUtc: string;
};

/**
 * force=true yalnız kullanıcı AÇIKÇA "kontrol et" dediğinde gönderilir: sunucu o zaman önbelleği
 * atlayıp GitHub'a gider. Otomatik çağrılarda (panel açılışı, rozet yenilemesi) önbellek kullanılır,
 * aksi hâlde GitHub'ın kimliksiz saatlik sınırı dolar ve yeni sürüm yayınlansa bile görünmez olur.
 */
export async function checkForUpdate(force = false) {
  const response = await apiClient.get<ApiEnvelope<UpdateStatus>>(
    force ? "/update/check?force=true" : "/update/check",
  );
  return response.data.data;
}

export async function applyUpdate(downloadUrl: string, packageSha256 = "") {
  const response = await apiClient.post<ApiEnvelope<UpdateStatus>>("/update/apply", {
    downloadUrl,
    packageSha256
  });
  return response.data.data;
}

export async function installPreparedUpdate() {
  const response = await apiClient.post<ApiEnvelope<UpdateStatus>>("/update/install");
  return response.data.data;
}

// Güncellemelerin çekileceği GitHub deposunu (owner/repo) ayarla.
export async function setUpdateGithubRepo(repo: string) {
  const response = await apiClient.post<ApiEnvelope<UpdateStatus>>("/update/github", { repo });
  return response.data.data;
}
