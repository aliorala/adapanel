import { apiClient } from "./client";

type ApiEnvelope<T> = { success: boolean; data: T; message: string; code: number };

/**
 * Lisansın panele gösterilen tam durumu (`LicenseStatusDto`).
 *
 * Anahtarın KENDİSİ hiçbir yanıtta dönmez; yalnız son dört karakteri (`keySuffix`) gelir.
 */
export type LicenseStatus = {
  /**
   * PascalCase DİZGİ olarak gelir ("Active", "GraceExpired" …): dönüştürücü `LicenseState`
   * tipinin üzerinde tanımlı, panelin genel JSON yapılandırması değiştirilmedi.
   *
   * Ekran buna sıkı bağlanmaz: renk kararı `isConfigured` + `restrictsNewResources`'tan,
   * metin `messageKey`'ten gelir; `state` yalnız rozet etiketinde kullanılır.
   */
  state: "NotConfigured" | "Active" | "Grace" | "GraceExpired" | "Expired" | "Invalid" | "Unlicensed";
  /** Arayüzün çevireceği mesaj anahtarı (`license.*`). */
  messageKey: string;
  /** Lisanslama bu kurulumda AÇIK mı (doğrulama ucu yapılandırıldı mı). */
  isConfigured: boolean;
  /** YENİ kaynak oluşturma kısıtlı mı. Mevcut kaynaklar her hâlükârda çalışır. */
  restrictsNewResources: boolean;
  hasKey: boolean;
  /** Anahtarın yalnız son dört karakteri. */
  keySuffix: string;
  sku: string;
  /** 0 = sınırsız. */
  maxSubscriptions: number;
  expiresAtUtc: string | null;
  lastVerifiedAtUtc: string | null;
  offlineGraceEndsAtUtc: string | null;
  /** SHA-256, 64 karakter onaltılık. */
  fingerprint: string;
  fingerprintSource: string;
  /** Teşhis metni; çeviri anahtarı DEĞİL. */
  lastError: string;
};

export async function getLicense() {
  const response = await apiClient.get<ApiEnvelope<LicenseStatus>>("/license");
  return response.data.data;
}

export async function activateLicense(licenseKey: string) {
  const response = await apiClient.post<ApiEnvelope<LicenseStatus>>("/license/activate", { licenseKey });
  return response.data.data;
}

export async function refreshLicense() {
  const response = await apiClient.post<ApiEnvelope<LicenseStatus>>("/license/refresh", {});
  return response.data.data;
}

export async function removeLicense() {
  const response = await apiClient.delete<ApiEnvelope<LicenseStatus>>("/license");
  return response.data.data;
}
