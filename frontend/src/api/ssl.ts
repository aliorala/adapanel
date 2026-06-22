import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type SslCertificate = {
  siteName: string;
  hostName: string;
  thumbprint: string;
  friendlyName: string;
  subject: string;
  notAfterUtc: string;
  source: string;
  isHttpsBindingPresent: boolean;
  port: number;
};

export type SslWorkspace = {
  siteName: string;
  hostNames: string[];
  certificates: SslCertificate[];
  httpsRedirectEnabled: boolean;
};

export async function setHttpsRedirect(siteName: string, enabled: boolean) {
  const response = await apiClient.post<ApiEnvelope<SslWorkspace>>("/ssl/https-redirect", { siteName, enabled });
  return response.data.data;
}

export async function getSslWorkspace(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<SslWorkspace>>(`/ssl/${encodeURIComponent(siteName)}`);
  return response.data.data;
}

export async function issueLetsEncrypt(payload: { siteName: string; hostName: string; contactEmail: string; useStaging: boolean; }) {
  const response = await apiClient.post<ApiEnvelope<SslCertificate>>("/ssl/letsencrypt", payload);
  return response.data.data;
}

export async function importCertificate(payload: {
  siteName: string;
  hostName: string;
  fileName: string;
  base64Content: string;
  password: string;
  port: number;
}) {
  const response = await apiClient.post<ApiEnvelope<SslCertificate>>("/ssl/import", payload);
  return response.data.data;
}

export async function renewAllCertificates() {
  const response = await apiClient.post<ApiEnvelope<{ renewedCount: number; output: string }>>("/ssl/renew", {});
  return response.data.data;
}
