import { apiClient } from "./client";
import { UserFacingError } from "../utils/apiError";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type ServerIpAddress = { address: string; family: string; interfaceName: string };

export async function getServerIpAddresses() {
  const response = await apiClient.get<ApiEnvelope<ServerIpAddress[]>>("/system/ip-addresses");
  const rows = response.data?.data;
  if (response.data?.success !== true || !Array.isArray(rows) || !rows.every(row => row && typeof row.interfaceName === "string" && validIp(row)))
    throw new UserFacingError("system.ip_addresses_unmeasured");
  return rows;
}

function validIp(row: ServerIpAddress): boolean {
  if (typeof row.address !== "string" || row.address !== row.address.trim()) return false;
  if (["IPv4", "InterNetwork"].includes(row.family)) {
    const parts = row.address.split(".");
    return parts.length === 4 && parts.every(part => /^\d{1,3}$/.test(part) && Number(part) <= 255);
  }
  if (!["IPv6", "InterNetworkV6"].includes(row.family) || !row.address.includes(":") || !/^[0-9a-f:.]+$/i.test(row.address)) return false;
  try { return new URL(`http://[${row.address}]/`).hostname.startsWith("["); } catch { return false; }
}
