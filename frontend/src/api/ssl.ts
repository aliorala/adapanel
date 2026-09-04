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
  // "letsencrypt" | "self-signed" | "manual" (başka bir sertifika otoritesi ya da elle yüklenmiş).
  source: string;
  // null = ÖLÇÜLEMEDİ. "Bağlama yok" ile "bakamadık" aynı şey değildir.
  isHttpsBindingPresent: boolean | null;
  // Bağlama BİZİM sertifikamızı taşıyor mu. false = ölçüldü, taşımıyor (bağlama sertifikasız ya da
  // başka sertifika) → site 443'te ölü olabilir. null = ölçülemedi.
  // "https bağlaması var" tek başına yetmez: bağlama önce kurulur, sertifika sonra iliştirilir.
  isCertificateBound: boolean | null;
  bindingDetail: string;
  port: number;
  // Let's Encrypt alınamadığı için kendinden imzalıya düşüldüyse sebebin çeviri anahtarı.
  // Boş değilse ekran "SSL kuruldu" gibi davranmamalı.
  fallbackReason: string;
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

export async function issueLetsEncrypt(payload: { siteName: string; hostName: string; contactEmail: string; useStaging: boolean; wildcard?: boolean; }) {
  // Wildcard DNS-01 turları uzun sürebilir (kayıt yazma + yetkili doğrulama) → geniş timeout.
  const response = await apiClient.post<ApiEnvelope<SslCertificate>>("/ssl/letsencrypt", payload, { timeout: 12 * 60_000 });
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
