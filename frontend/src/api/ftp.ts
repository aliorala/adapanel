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
function sameIdentity(value: unknown, expected: string) { return typeof value === 'string' && value.trim().toLowerCase() === expected.trim().toLowerCase(); }
function isStatus(value: unknown): value is FtpServerStatus {
  return record(value) && ['serverAddress','statusMessage','serviceState','siteState','controlPortOwner'].every(key => typeof value[key] === 'string')
    && ['isConfigured','isSiteRunning','hasControlPortConflict'].every(key => typeof value[key] === 'boolean')
    && typeof value.controlPort === 'number' && Number.isInteger(value.controlPort) && value.controlPort > 0 && value.controlPort <= 65535
    && ['passivePortStart','passivePortEnd'].every(key => value[key] == null || (typeof value[key] === 'number' && Number.isInteger(value[key]) && (value[key] as number) >= 1025 && (value[key] as number) <= 65535))
    && (value.passivePortStart == null || value.passivePortEnd == null || (value.passivePortStart as number) <= (value.passivePortEnd as number));
}
function measuredStatus(body: unknown): FtpServerStatus {
  const status = measured(body, isStatus);
  return status.passivePortStart == null || status.passivePortEnd == null
    ? { ...status, passivePortStart: null, passivePortEnd: null } : status;
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
  passivePortStart: number | null;
  passivePortEnd: number | null;
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
  return measuredStatus(response.data);
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
  return measuredStatus(response.data);
}

export async function getFtpUsers() {
  const response = await apiClient.get<ApiEnvelope<FtpUser[]>>("/ftp/users");
  return measured(response.data, (value): value is FtpUser[] => Array.isArray(value) && value.every(isUser));
}

export async function createFtpUser(payload: { userName: string; password: string; siteName: string; homePath: string; }): Promise<FtpUser & { message?: string }> {
  const accepted = { ...payload };
  const response = await apiClient.post<ApiEnvelope<FtpUser>>("/ftp/users", accepted);
  const user = measured(response.data, (value): value is FtpUser => isUser(value)
    && sameIdentity(value.userName, accepted.userName) && sameIdentity(value.siteName, accepted.siteName)
    && (value.passwordNeverExpires == null || typeof value.passwordNeverExpires === 'boolean'));
  return { ...user, passwordNeverExpires: user.passwordNeverExpires ?? null, message: response.data.message };
}

export async function setFtpPassword(userName: string, password: string) {
  const response = await apiClient.post<ApiEnvelope<object>>("/ftp/users/password", { userName, password });
  measured(response.data, (value): value is {userName: string} => record(value) && sameIdentity(value.userName, userName));
}

export type FtpCertificate = {
  hostName?: string;
  certificateThumbprint?: string;
  certificateSubject?: string;
  certificateNotAfterUtc?: string | null;
  certificateStoreName?: string;
  certificateVerified?: boolean | null;
  certificateDetail?: string;
};

function isCertificate(value: unknown): value is FtpCertificate {
  return record(value) && ['hostName', 'certificateThumbprint', 'certificateSubject', 'certificateNotAfterUtc', 'certificateStoreName', 'certificateDetail'].every(key => value[key] == null || typeof value[key] === 'string')
    && (value.certificateVerified == null || typeof value.certificateVerified === 'boolean');
}

export async function applyFtpCertificate(hostName: string): Promise<FtpCertificate> {
  const response = await apiClient.post<ApiEnvelope<FtpCertificate>>("/ftp/certificate", { hostName });
  return measured(response.data, (value): value is FtpCertificate => isCertificate(value)
    && (value.certificateVerified !== true || (sameIdentity(value.hostName, hostName) && Boolean(value.certificateThumbprint?.trim()))));
}

export type FtpTlsStatus = FtpCertificate & {
  requireSsl: boolean;
  // IIS'ten ÖLÇÜLEN sonuç: true = yürürlükte, false = uygulanmadı, null = ölçülemedi.
  // Eski sunucularda null olabilir; bilinmeyen durum doğrulanmış sayılmaz.
  verified?: boolean | null;
  detail?: string;
};

// M5 (panel "yalnız güvenli FTP"): admin-only server-level FTPS zorunluluğu.
export async function getFtpTls() {
  const response = await apiClient.get<ApiEnvelope<FtpTlsStatus>>("/ftp/tls");
  return measured(response.data, (value): value is FtpTlsStatus => record(value) && typeof value.requireSsl === 'boolean'
    && (value.verified == null || typeof value.verified === 'boolean')
    && (value.detail == null || typeof value.detail === 'string') && isCertificate(value));
}

export async function setFtpTls(requireSsl: boolean) {
  const response = await apiClient.put<ApiEnvelope<FtpTlsStatus>>("/ftp/tls", { requireSsl });
  return measured(response.data, (value): value is FtpTlsStatus => record(value) && typeof value.requireSsl === 'boolean'
    && (value.verified == null || typeof value.verified === 'boolean')
    && (value.verified !== true || value.requireSsl === requireSsl)
    && (value.detail == null || typeof value.detail === 'string') && isCertificate(value));
}

export async function deleteFtpUser(userName: string) {
  const response = await apiClient.delete<ApiEnvelope<object>>(`/ftp/users/${encodeURIComponent(userName)}`);
  measured(response.data, (value): value is {userName: string} => record(value) && sameIdentity(value.userName, userName));
}
