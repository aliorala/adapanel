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

export async function checkForUpdate() {
  const response = await apiClient.get<ApiEnvelope<UpdateStatus>>("/update/check");
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
