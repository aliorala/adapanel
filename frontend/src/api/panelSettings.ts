import { apiClient } from "./client";

type ApiEnvelope<T> = { success: boolean; data: T; message: string; code: number };

export type ServerAddressDnsSync = {
  oldAddress: string;
  newAddress: string;
  pending: boolean;
  skipped?: boolean;
  zones: { siteName: string; zoneName: string; changedRecords: number; succeeded: boolean; skipped?: boolean; error: string }[];
  error: string;
  ftp?: { succeeded: boolean; skipped: boolean; changed: boolean; error: string; currentAddress?: string | null } | null;
};

export type PanelPreferences = {
  panelName: string;
  defaultLanguage: string;
  themeMode: string;
  uiDensity: string;
  defaultSiteRoot: string;
  defaultBackupRoot: string;
  serverAddress: string;
  serverAddressDnsSync?: ServerAddressDnsSync | null;
  /**
   * `serverAddress` internetten açılabilecek bir ad/adres mi. SUNUCUNUN TÜRETTİĞİ salt okunur
   * bilgi; PUT gövdesinde gönderilse de sunucu kendi hesabıyla ezer.
   *
   * Neden var: adres ayarlanmadığında sunucu makinenin iç IP'sini (192.168.x.x gibi) yazıyor ve
   * müşteriye e-posta ile giden bağlantılar o adresi taşıyor — açılmıyorlar. Bayrak yalnız uyarı
   * gösterir, hiçbir gönderimi engellemez (iç ağ kurulumunda o adres doğrudur).
   *
   * OPSİYONEL: alanı taşımayan mevcut varsayılan tercih nesnelerini bozmamak için.
   */
  serverAddressExternallyReachable?: boolean;
  /**
   * Panelin dışarıya açıldığı alan adı (boş = pasif). SALT OKUNUR bir alandır: sahibi
   * "Panel erişimi" bölümüdür (api/panelAccess.ts). Genel ayarlar PUT'u bu alanı GÖNDERMEZ,
   * sunucu da önceki değeri korur — buradan yanlışlıkla sıfırlanamaz.
   *
   * OPSİYONEL: mevcut varsayılan tercih nesneleri (ör. SetupWizardView'daki reactive literal)
   * bu alanı eklemek ZORUNDA kalmasın diye. Sunucu her durumda gerçek değeri döner.
   */
  panelHostName?: string;
  preferredHttpsPort: number;
  allowHttpFallback: boolean;
  loginLockoutMinutes: number;
  defaultRuntimeProfile: string;
  installRuntimeIfMissing: boolean;
  restartRequired: boolean;
};

export async function getPanelPreferences() {
  const response = await apiClient.get<ApiEnvelope<PanelPreferences>>("/panel-settings");
  return response.data.data;
}

export async function updatePanelPreferences(payload: PanelPreferences, reconcileServerAddress = false) {
  const response = await apiClient.put<ApiEnvelope<PanelPreferences>>("/panel-settings", { ...payload, reconcileServerAddress });
  return response.data.data;
}

export async function syncServerAddressDns(oldAddress: string) {
  const response = await apiClient.post<ApiEnvelope<PanelPreferences>>("/panel-settings/dns-address-sync", { oldAddress });
  return response.data.data;
}

/*
 * ÖZEL MARKA (white-label).
 *
 * Aynı dosyada duruyor çünkü bunlar da panel ayarıdır ve aynı ekrandan yönetilir; ayrı bir modül
 * yalnızca iki dosya arasında gidip gelmek olurdu.
 *
 * GÖRSELLER YANITTA data URI OLARAK GELİR, ayrı bir dosya adresi olarak değil. Sebebi mimari:
 * panelin gizli giriş yolu ara katmanı `/api` altındaki her isteğe `X-AdaPanel-Entry-Path`
 * başlığını zorunlu kılıyor, bir `<img src>` isteği ise başlık gönderemez — ayrı bir görsel ucu
 * yazılsaydı logo her seferinde 404 dönerdi.
 */
export type Branding = {
  panelName: string;
  /** "#rrggbb" ya da boş. Boş = panelin kendi rengi. */
  accentColor: string;
  logoDataUrl: string;
  faviconDataUrl: string;
  logoLinkUrl: string;
  /** "server" (yöneticinin markası) ya da "reseller" (kullanıcıyı barındıran bayinin markası). */
  scope: string;
};

export type BrandingImageKind = "logo" | "favicon";

export const emptyBranding = (): Branding => ({
  panelName: "",
  accentColor: "",
  logoDataUrl: "",
  faviconDataUrl: "",
  logoLinkUrl: "",
  scope: "server"
});

/** Oturum açan kişinin GÖRECEĞİ marka. Kimliksiz çağrıda (giriş ekranı) sunucu markası döner. */
export async function getBranding() {
  const response = await apiClient.get<ApiEnvelope<Branding>>("/branding");
  return response.data.data;
}

/** Düzenleme formunun okuduğu HAM kayıt: yöneticide sunucu, bayide bayinin KENDİ markası. */
export async function getBrandingSettings() {
  const response = await apiClient.get<ApiEnvelope<Branding>>("/branding/settings");
  return response.data.data;
}

export async function updateBrandingSettings(payload: {
  panelName: string;
  accentColor: string;
  logoLinkUrl: string;
}) {
  const response = await apiClient.put<ApiEnvelope<Branding>>("/branding/settings", payload);
  return response.data.data;
}

export async function uploadBrandingImage(kind: BrandingImageKind, file: File) {
  const body = new FormData();
  body.append("file", file);
  const response = await apiClient.post<ApiEnvelope<Branding>>(`/branding/${kind}`, body, {
    headers: { "Content-Type": "multipart/form-data" }
  });
  return response.data.data;
}

export async function removeBrandingImage(kind: BrandingImageKind) {
  const response = await apiClient.delete<ApiEnvelope<Branding>>(`/branding/${kind}`);
  return response.data.data;
}
