import { apiClient } from "./client";

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
  records: DnsRecord[];
  isWindowsDnsAvailable: boolean;
  isWindowsZonePresent: boolean;
  source: string;
  statusMessage: string;
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
  return response.data.data;
}

export async function updateDnsZone(siteName: string, payload: { zoneName: string; nameServers: string[]; zoneTransferIps: string[] }) {
  const response = await apiClient.put<ApiEnvelope<DnsZone>>(`/sites/${encodeURIComponent(siteName)}/dns/zone`, payload);
  return response.data.data;
}

export async function resetDnsDefaults(siteName: string) {
  const response = await apiClient.post<ApiEnvelope<DnsZone>>(`/sites/${encodeURIComponent(siteName)}/dns/defaults`, {});
  return response.data.data;
}

export async function installWindowsDns(siteName: string) {
  const response = await apiClient.post<ApiEnvelope<DnsZone>>(`/sites/${encodeURIComponent(siteName)}/dns/windows/install`, {});
  return response.data.data;
}

export async function upsertDnsRecord(siteName: string, payload: UpsertDnsRecordPayload) {
  const response = await apiClient.post<ApiEnvelope<DnsZone>>(`/sites/${encodeURIComponent(siteName)}/dns/records`, payload);
  return response.data.data;
}

export async function deleteDnsRecord(siteName: string, recordId: string) {
  const response = await apiClient.delete<ApiEnvelope<DnsZone>>(`/sites/${encodeURIComponent(siteName)}/dns/records/${encodeURIComponent(recordId)}`);
  return response.data.data;
}
