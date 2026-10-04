import { apiClient } from "./client";
import { UserFacingError } from "../utils/apiError";

type ApiEnvelope<T> = { success: boolean; data: T; message: string; code: number };

export type DnsRecord = {
  id: string;
  type: string;
  host: string;
  value: string;
  ttl: number;
  priority: number | null;
  isManaged: boolean;
};

export type DnsZone = {
  siteName: string;
  zoneName: string;
  nameServers: string[];
  zoneTransferIps: string[];
  /** Native zone settings are shared; record contributions remain site-specific. */
  isSharedZone?: boolean;
  modeConfigurationConflict?: boolean;
  transferConfigurationConflict?: boolean;
  /** Bölge ikincil (slave) mi: kayıtların kaynağı panel değil, dış master sunucudur. */
  isSecondary: boolean;
  /** İkincil kipte bölgenin çekildiği master DNS sunucularının IP'leri. */
  masterServers: string[];
  /** Bölgeye özel + sunucu geneli listelerin birleşimi: AXFR/NOTIFY'ı FİİLEN alanlar. */
  effectiveNotifyServers: string[];
  /** Windows DNS'in bildirdiği tür (Primary/Secondary/…); ölçülemediyse boş. */
  windowsZoneType: string;
  records: DnsRecord[];
  isWindowsDnsAvailable: boolean;
  isWindowsZonePresent: boolean;
  source: string;
  statusMessage: string;
  isDnssecSigned: boolean;
  dnssecDsRecords: string[];
};

export type UpsertDnsRecordPayload = {
  id?: string;
  type: string;
  host: string;
  value: string;
  ttl: number;
  priority?: number | null;
};

export async function getDnsZone(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<DnsZone>>(`/sites/${encodeURIComponent(siteName)}/dns`);
  return measuredZone(response.data, siteName);
}

export async function updateDnsZone(siteName: string, payload: { zoneName: string; nameServers: string[]; zoneTransferIps: string[] }) {
  const response = await apiClient.put<ApiEnvelope<DnsZone>>(`/sites/${encodeURIComponent(siteName)}/dns/zone`, payload);
  return measuredZone(response.data, siteName);
}

/**
 * Bölgeyi birincil ↔ ikincil (slave) kipleri arasında çevirir (Plesk "Switch to Secondary").
 * İkincile geçerken en az bir master IP zorunludur; sunucu boş listeyi dns.master_required ile reddeder.
 */
export async function setDnsZoneMode(siteName: string, secondary: boolean, masterServers: string[]) {
  const response = await apiClient.post<ApiEnvelope<DnsZone>>(
    `/sites/${encodeURIComponent(siteName)}/dns/mode`,
    { secondary, masterServers });
  return measuredZone(response.data, siteName);
}

/** İkincil bölge için master'dan tam zone transferini (AXFR) hemen başlatır. */
export async function transferDnsZoneNow(siteName: string) {
  const response = await apiClient.post<ApiEnvelope<DnsZone>>(`/sites/${encodeURIComponent(siteName)}/dns/transfer`, {});
  return measuredZone(response.data, siteName);
}

export async function resetDnsDefaults(siteName: string) {
  const response = await apiClient.post<ApiEnvelope<DnsZone>>(`/sites/${encodeURIComponent(siteName)}/dns/defaults`, {});
  return measuredZone(response.data, siteName);
}

export async function installWindowsDns(siteName: string) {
  const response = await apiClient.post<ApiEnvelope<DnsZone>>(`/sites/${encodeURIComponent(siteName)}/dns/windows/install`, {});
  return measuredZone(response.data, siteName);
}

export async function upsertDnsRecord(siteName: string, payload: UpsertDnsRecordPayload) {
  const response = await apiClient.post<ApiEnvelope<DnsZone>>(`/sites/${encodeURIComponent(siteName)}/dns/records`, payload);
  return measuredZone(response.data, siteName);
}

export async function deleteDnsRecord(siteName: string, recordId: string) {
  const response = await apiClient.delete<ApiEnvelope<DnsZone>>(`/sites/${encodeURIComponent(siteName)}/dns/records/${encodeURIComponent(recordId)}`);
  return measuredZone(response.data, siteName);
}

function measuredZone(envelope: ApiEnvelope<DnsZone>, siteName: string): DnsZone {
  const row = envelope?.data;
  if (envelope?.success !== true || !row || typeof row !== "object" ||
      [row.siteName, row.zoneName, row.windowsZoneType, row.source, row.statusMessage].some(value => typeof value !== "string") ||
      !row.zoneName.trim() || row.siteName.trim().toLowerCase() !== siteName.trim().toLowerCase() ||
      [row.isSecondary, row.isWindowsDnsAvailable, row.isWindowsZonePresent, row.isDnssecSigned].some(value => typeof value !== "boolean") ||
      [row.isSharedZone, row.modeConfigurationConflict, row.transferConfigurationConflict].some(value => value !== undefined && typeof value !== "boolean") ||
      [row.nameServers, row.zoneTransferIps, row.masterServers, row.effectiveNotifyServers, row.dnssecDsRecords].some(values => !Array.isArray(values) || values.some(value => typeof value !== "string")) ||
      !Array.isArray(row.records) || row.records.some(record => !record ||
        [record.id, record.type, record.host, record.value].some(value => typeof value !== "string") || !record.id.trim() || !record.type.trim() ||
        typeof record.isManaged !== "boolean" || !Number.isSafeInteger(record.ttl) || record.ttl < 0 ||
        (record.priority !== null && (!Number.isSafeInteger(record.priority) || record.priority < 0))) ||
      new Set(row.records.map(record => record.id.toLowerCase())).size !== row.records.length) {
    throw new UserFacingError("panel.dns.resultUnconfirmed");
  }
  return row;
}

// DNS sunucusunun dışarıya yanıt verebilirliği: servis "Running" olsa bile genel IP'ye bind
// olmamış olabilir (reboot yarış durumu) — o zaman hiçbir alan adı dışarıdan çözülmez.
export type DnsServerHealth = {
  installed: boolean;
  serviceStatus: string;
  startMode: string;
  delayedAutoStart: boolean;
  listeningAddresses: string[];
  expectedAddresses: string[];
  missingAddresses: string[];
  publiclyBound: boolean;
  zoneCount: number;
  needsRepair: boolean;
  lastAutoRepairUtc: string | null;
  lastAutoRepairStatus: string;
  /*
   * DURUM SUNUCUDAN GELİR, DİZGEDEN ÇIKARILMAZ.
   * `lastAutoRepairStatus` bir çeviri anahtarı taşır; onun başına bakıp ("failed ile mi
   * başlıyor") renk seçmek, sunucu anahtarı değiştirdiği gün sessizce bozulan bir yama olurdu.
   * Başarı/başarısızlık kararını ölçen taraf sunucudur, bu alan onun cevabıdır.
   * `null` = nöbetçi hiç çalışmadı ya da bu alan yokken yazılmış eski kayıt: ÖLÇÜLMEDİ.
   */
  lastAutoRepairSucceeded: boolean | null;
  details: string;
};

export async function getDnsServerHealth() {
  const response = await apiClient.get<ApiEnvelope<DnsServerHealth>>("/dns/server/health");
  return measuredDnsHealth(response.data);
}

export async function repairDnsServer(applyDelayedAutoStart = true) {
  const response = await apiClient.post<ApiEnvelope<DnsServerHealth>>("/dns/server/repair", { applyDelayedAutoStart });
  return measuredDnsHealth(response.data);
}

function measuredDnsHealth(envelope: ApiEnvelope<DnsServerHealth>): DnsServerHealth {
  const row = envelope?.data;
  if (envelope?.success !== true || !row || [row.installed, row.delayedAutoStart, row.publiclyBound, row.needsRepair].some(value => typeof value !== "boolean") ||
    [row.serviceStatus, row.startMode, row.details, row.lastAutoRepairStatus].some(value => typeof value !== "string") ||
    !["Running", "Stopped", "NotInstalled", "Unknown", "StartPending", "StopPending", "ContinuePending", "PausePending", "Paused"].includes(row.serviceStatus) ||
    !Number.isSafeInteger(row.zoneCount) || row.zoneCount < 0 ||
    [row.listeningAddresses, row.expectedAddresses, row.missingAddresses].some(values => !Array.isArray(values) || values.some(value => typeof value !== "string")) ||
    (row.lastAutoRepairUtc !== null && typeof row.lastAutoRepairUtc !== "string") ||
    (row.lastAutoRepairSucceeded != null && typeof row.lastAutoRepairSucceeded !== "boolean")) throw new UserFacingError("dns.health_unmeasurable");
  return row;
}

// Sunucu-geneli DNS şablonu (Plesk "DNS template"): yeni site bölgeleri bununla seed edilir.
/** priority yalnızca MX (ve SRV) için anlamlıdır; gönderilmezse sunucu MX'e 10 uygular. */
export type DnsTemplateRecord = { type: string; host: string; value: string; ttl: number; priority?: number | null };
/** secondaryDnsServers: sunucu geneli ikincil (slave) DNS sunucuları — HER bölgeye AXFR izni + NOTIFY. */
export type DnsTemplate = { records: DnsTemplateRecord[]; secondaryDnsServers: string[] };

export async function getDnsTemplate() {
  const response = await apiClient.get<ApiEnvelope<DnsTemplate>>("/dns/template");
  return response.data.data;
}

export async function updateDnsTemplate(records: DnsTemplateRecord[], secondaryDnsServers: string[] = []) {
  const response = await apiClient.put<ApiEnvelope<DnsTemplate>>("/dns/template", { records, secondaryDnsServers });
  return response.data.data;
}

// DNSSEC aç/kapat (Windows DNS zone-imzalama). Açıkken dönen zone'da dnssecDsRecords (registrar'a verilecek) dolu olur.
export async function setDnssec(siteName: string, enabled: boolean) {
  const response = await apiClient.post<ApiEnvelope<DnsZone>>(`/sites/${encodeURIComponent(siteName)}/dns/dnssec`, { enabled });
  return measuredZone(response.data, siteName);
}
