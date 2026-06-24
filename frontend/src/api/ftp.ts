import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type FtpUser = {
  userName: string;
  homePath: string;
  siteName: string;
  existsOnSystem: boolean;
  isEnabled: boolean;
  createdAtUtc: string;
};

export type FtpServerStatus = {
  serverAddress: string;
  controlPort: number;
  passivePortStart: number;
  passivePortEnd: number;
  isConfigured: boolean;
  statusMessage: string;
};

export async function ensureFtpServer() {
  const response = await apiClient.post<ApiEnvelope<FtpServerStatus>>("/ftp/ensure", {});
  return response.data.data;
}

export async function getFtpUsers() {
  const response = await apiClient.get<ApiEnvelope<FtpUser[]>>("/ftp/users");
  return response.data.data;
}

export async function createFtpUser(payload: { userName: string; password: string; siteName: string; homePath: string; }) {
  const response = await apiClient.post<ApiEnvelope<FtpUser>>("/ftp/users", payload);
  return response.data.data;
}

export async function setFtpPassword(userName: string, password: string) {
  await apiClient.post<ApiEnvelope<object>>("/ftp/users/password", { userName, password });
}

export type FtpTlsStatus = {
  requireSsl: boolean;
};

// M5 (panel "yalnız güvenli FTP"): admin-only server-level FTPS zorunluluğu.
export async function getFtpTls() {
  const response = await apiClient.get<ApiEnvelope<FtpTlsStatus>>("/ftp/tls");
  return response.data.data;
}

export async function setFtpTls(requireSsl: boolean) {
  const response = await apiClient.put<ApiEnvelope<FtpTlsStatus>>("/ftp/tls", { requireSsl });
  return response.data.data;
}

export async function deleteFtpUser(userName: string) {
  await apiClient.delete<ApiEnvelope<object>>(`/ftp/users/${encodeURIComponent(userName)}`);
}
