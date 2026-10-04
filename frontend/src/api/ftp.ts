import { apiClient } from "./client";
import { UserFacingError } from "../utils/apiError";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

function record(value: unknown): value is Record<string, unknown> { return !!value && typeof value === 'object' && !Array.isArray(value); }
function measured<T>(body: unknown, valid: (data: unknown) => data is T): T {
  if (!record(body) || body.success !== true || !valid(body.data)) throw new UserFacingError('panel.ftp.resultUnconfirmed');
  return body.data;
}
function isUser(value: unknown): value is FtpUser {
  return record(value) && typeof value.userName === 'string' && value.userName.trim().length > 0
    && ['homePath','siteName','createdAtUtc'].every(key => typeof value[key] === 'string')
    && typeof value.isEnabled === 'boolean' && typeof value.existsOnSystem === 'boolean';
}
function isStatus(value: unknown): value is FtpServerStatus {
  return record(value) && ['serverAddress','statusMessage','serviceState','siteState','controlPortOwner'].every(key => typeof value[key] === 'string')
    && ['isConfigured','isSiteRunning','hasControlPortConflict'].every(key => typeof value[key] === 'boolean')
    && ['controlPort','passivePortStart','passivePortEnd'].every(key => typeof value[key] === 'number' && Number.isInteger(value[key]) && (value[key] as number) > 0 && (value[key] as number) <= 65535);
}

export type FtpUser = {
  userName: string;
  homePath: string;
  siteName: string;
  existsOnSystem: boolean;
  /*
   * EV KLASÖRÜ ÖLÇÜMÜ. `homePath` bir KAYITTIR (oluştururken yazılan hedef); aşağıdakiler ise
   * DİSKTEN okunur. Sunucu bu üç alanı zaten döndürüyordu ama tip bildirmediği için ekran hiç
   * okumuyordu — canlı bir sunucuda FTP hesabı sapasağlam görünürken her giriş
   * "530 home directory inaccessible" alıyordu ve panel bunu söyleyemiyordu.
   *
   * `null` = ÖLÇÜLEMEDİ; `false` = ölçüldü ve yok. İkisini birleştirmek, bilmediğimizi
   * biliyormuş gibi göstermek olurdu. Alanlar OPSİYONEL: bu alanları göndermeyen eski bir
   * sunucuda `undefined` gelir ve arayüz sessiz kalır — "ölçüldü, bozuk" demez.
   */
  homeLinkPath?: string;
  homeLinkExists?: boolean | null;
  homeTargetExists?: boolean | null;
  /** Oluşturmada ölçülür: true = süre dolumu kapalı, false = açık, null = doğrulanamadı. */
  passwordNeverExpires?: boolean | null;
  isEnabled: boolean;
  createdAtUtc: string;
};

export type FtpServerStatus = {
  serverAddress: string;
  controlPort: number;
  passivePortStart: number;
  passivePortEnd: number;
  /** YALNIZ `statusMessage === "ftp.server_ready"` iken true (bkz. FtpService.GetServerStatusAsync). */
  isConfigured: boolean;
  /** Durumun SEBEBİ, i18n anahtarı olarak: `ftp.server_ready`, `ftp.control_port_conflict`, … */
  statusMessage: string;
  /** Microsoft FTP Service (FTPSVC) durumu — ölçülemezse boş. */
  serviceState: string;
  /** IIS FTP sitesinin durumu (`Started`/`Stopped`/…) — ölçülemezse boş. */
  siteState: string;
  isSiteRunning: boolean;
  /** Denetim portunu (21) panelin servisi DIŞINDA bir süreç de tutuyor mu. */
  hasControlPortConflict: boolean;
  /** Çakışmada portu tutan yabancı süreç ("FileZilla Server.exe (PID 5036, …)"); yoksa boş. */
  controlPortOwner: string;
};

export async function ensureFtpServer() {
  const response = await apiClient.post<ApiEnvelope<FtpServerStatus>>("/ftp/ensure", {});
  return measured(response.data, isStatus);
}

/**
 * FTP'nin GERÇEK durumunu ÖLÇER — kurmaz, servisi yeniden başlatmaz.
 *
 * `POST /ftp/ensure` bir MUTASYON; "neden kimse bağlanamıyor" sorusunun bedeli altyapıyı yeniden
 * yapılandırmak olmamalı. Bu yüzden yenileme düğmesi bu salt-okunur ucu kullanır.
 * Uç yönetici-özel: yanıt sunucudaki yabancı süreçlerin adlarını ve PID'lerini taşır.
 */
export async function getFtpStatus() {
  const response = await apiClient.get<ApiEnvelope<FtpServerStatus>>("/ftp/status");
  return measured(response.data, isStatus);
}

export async function getFtpUsers() {
  const response = await apiClient.get<ApiEnvelope<FtpUser[]>>("/ftp/users");
  return measured(response.data, (value): value is FtpUser[] => Array.isArray(value) && value.every(isUser));
}

export async function createFtpUser(payload: { userName: string; password: string; siteName: string; homePath: string; }): Promise<FtpUser & { message?: string }> {
  const response = await apiClient.post<ApiEnvelope<FtpUser>>("/ftp/users", payload);
  return { ...response.data.data, message: response.data.message };
}

export async function setFtpPassword(userName: string, password: string) {
  await apiClient.post<ApiEnvelope<object>>("/ftp/users/password", { userName, password });
}

export type FtpTlsStatus = {
  requireSsl: boolean;
  // IIS'ten ÖLÇÜLEN sonuç: true = yürürlükte, false = uygulanmadı, null = ölçülemedi.
  // Okuma ucunda (GET /ftp/tls) her zaman null'dır: orası yalnız panelin kendi kaydını okur.
  verified?: boolean | null;
  detail?: string;
};

// M5 (panel "yalnız güvenli FTP"): admin-only server-level FTPS zorunluluğu.
export async function getFtpTls() {
  const response = await apiClient.get<ApiEnvelope<FtpTlsStatus>>("/ftp/tls");
  return measured(response.data, (value): value is FtpTlsStatus => record(value) && typeof value.requireSsl === 'boolean');
}

export async function setFtpTls(requireSsl: boolean) {
  const response = await apiClient.put<ApiEnvelope<FtpTlsStatus>>("/ftp/tls", { requireSsl });
  return response.data.data;
}

export async function deleteFtpUser(userName: string) {
  await apiClient.delete<ApiEnvelope<object>>(`/ftp/users/${encodeURIComponent(userName)}`);
}
