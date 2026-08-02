import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

/**
 * Kişisel erişim anahtarı (yapay zeka bağlantısı). Anahtarın DEĞERİ burada asla dönmez;
 * yalnız son 4 karakter ("suffix") ile ayırt edilir.
 */
export type ApiToken = {
  id: string;
  name: string;
  suffix: string;
  allowDestructive: boolean;
  allowTerminal: boolean;
  createdAtUtc: string;
  expiresAtUtc: string | null;
  lastUsedAtUtc: string | null;
  isRevoked: boolean;
  /** Süresi dolmamış ve iptal edilmemişse true — arayüz "etkin/pasif" için bunu kullanır. */
  isUsable: boolean;
};

export type CreateApiTokenPayload = {
  name: string;
  allowDestructive: boolean;
  allowTerminal: boolean;
  /** Gün cinsinden ömür; 0 = süresiz. */
  expiresInDays: number;
};

/**
 * Oluşturma yanıtı. <c>value</c> YALNIZCA bu yanıtta döner; sunucu yalnız karmasını saklar,
 * bir daha hiçbir yerden okunamaz.
 */
export type CreatedApiToken = {
  token: ApiToken;
  value: string;
};

export async function getApiTokens() {
  const response = await apiClient.get<ApiEnvelope<ApiToken[]>>("/api-tokens");
  return response.data.data;
}

export async function createApiToken(payload: CreateApiTokenPayload) {
  const response = await apiClient.post<ApiEnvelope<CreatedApiToken>>("/api-tokens", payload);
  return response.data.data;
}

export async function revokeApiToken(tokenId: string) {
  await apiClient.delete<ApiEnvelope<object>>(`/api-tokens/${encodeURIComponent(tokenId)}`);
}
