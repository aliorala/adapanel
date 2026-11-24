import { apiClient } from "./client";

export type BackupNetworkTrust = { target: string; allowed: boolean };
type Envelope = { data: BackupNetworkTrust };

// Only a canonical endpoint crosses this API; never send a password, path or query in GET.
export function ftpTrustTarget(value: string): string | null {
  try {
    const url = new URL(value);
    if (url.protocol !== "ftp:" || !url.hostname || url.username || url.password || url.search || url.hash) return null;
    return `ftp://${url.hostname.toLowerCase().replace(/\.$/, "")}:${url.port || "21"}/`;
  } catch { return null; }
}

export async function getBackupNetworkTrust(target: string) {
  const response = await apiClient.get<Envelope>("/backup/network-trust", { params: { target } });
  return response.data.data;
}

export async function setBackupNetworkTrust(target: string, allowed: boolean) {
  const response = await apiClient.put<Envelope>("/backup/network-trust", { target, allowed });
  return response.data.data;
}
