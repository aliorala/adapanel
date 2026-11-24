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
  /** Ozel anahtar ve jeton DEGERLERI ASLA gelmez: yalniz var/yok ve genel anahtar. */
  hasDeployKey: boolean;
  deployPublicKey: string;
  deployKeyFingerprint: string;
  deployKeyCreatedUtc: string | null;
  hasHttpsToken: boolean;
  httpsUsername: string;
  postDeployCommands: string[];
  postDeployTimeoutSeconds: number;
  /** Secilebilecek beyaz liste kimlikleri -- SUNUCUDAN gelir. */
  allowedPostDeployCommands: string[];
  recentDeployments: GitDeployHistory[];
};

export type GitDeployHistory = {
  id: string;
  deployedAtUtc: string;
  trigger: string;
  previousCommit: string;
  newCommit: string;
  success: boolean;
  localChangeCount: number;
  localChanges: string[];
  savedChangesRef: string;
  postDeploy: string;
};

export type GitRemovalResult = { siteName: string; removed: string[]; leftovers: string[]; kept: string[] };

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

const base = (siteName: string) => `/git/${encodeURIComponent(siteName)}`;

export async function generateGitDeployKey(siteName: string) {
  const response = await apiClient.post<ApiEnvelope<GitDeployment>>(`${base(siteName)}/deploy-key`, {});
  return response.data.data;
}

export async function removeGitDeployKey(siteName: string) {
  const response = await apiClient.delete<ApiEnvelope<GitDeployment>>(`${base(siteName)}/deploy-key`);
  return response.data.data;
}

export async function setGitHttpsToken(siteName: string, username: string, token: string) {
  const response = await apiClient.put<ApiEnvelope<GitDeployment>>(`${base(siteName)}/https-token`, { username, token });
  return response.data.data;
}

export async function removeGitHttpsToken(siteName: string) {
  const response = await apiClient.delete<ApiEnvelope<GitDeployment>>(`${base(siteName)}/https-token`);
  return response.data.data;
}

export async function setGitPostDeploy(siteName: string, commands: string[], timeoutSeconds: number) {
  const response = await apiClient.put<ApiEnvelope<GitDeployment>>(`${base(siteName)}/post-deploy`, { commands, timeoutSeconds });
  return response.data.data;
}

export async function rollbackGit(siteName: string, deploymentId: string) {
  const response = await apiClient.post<ApiEnvelope<GitDeployResult>>(`${base(siteName)}/rollback`, { deploymentId });
  return response.data.data;
}

export async function removeGitDeployment(siteName: string, confirmSiteName: string) {
  const response = await apiClient.post<ApiEnvelope<GitRemovalResult>>(`${base(siteName)}/remove`, { confirmSiteName });
  return response.data.data;
}
