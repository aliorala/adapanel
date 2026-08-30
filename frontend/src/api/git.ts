import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type GitDeployment = {
  siteName: string;
  repositoryUrl: string;
  branch: string;
  configured: boolean;
  deployPath: string;
  lastCommit: string;
  lastDeployUtc: string | null;
  lastDeployStatus: string;
  webhookPath: string;
  webhookSecret: string;
  /**
   * Jetonun konacagi istek basliginin adi -- SUNUCUDAN gelir, burada sabit YAZILMAZ.
   * Sabit yazilsaydi sunucu adi degistirdiginde panel, artik okunmayan bir basligi talimat
   * olarak gosterirdi ve kullanici sebebi gorunmeyen bir 401 alirdi.
   */
  webhookHeaderName: string;
};

export type GitDeployResult = {
  success: boolean;
  commit: string;
  output: string;
  deployedAtUtc: string;
  deployment: GitDeployment;
};

export async function getGitDeployment(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<GitDeployment>>(`/git/${encodeURIComponent(siteName)}`);
  return response.data.data;
}

export async function configureGitDeployment(
  siteName: string,
  repositoryUrl: string,
  branch: string,
  regenerateSecret = false
) {
  const response = await apiClient.put<ApiEnvelope<GitDeployment>>(`/git/${encodeURIComponent(siteName)}`, {
    repositoryUrl,
    branch,
    regenerateSecret
  });
  return response.data.data;
}

export async function deployGit(siteName: string) {
  const response = await apiClient.post<ApiEnvelope<GitDeployResult>>(`/git/${encodeURIComponent(siteName)}/deploy`, {});
  return response.data.data;
}
