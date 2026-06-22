import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type ServerIpAddress = { address: string; family: string; interfaceName: string };

export async function getServerIpAddresses() {
  const response = await apiClient.get<ApiEnvelope<ServerIpAddress[]>>("/system/ip-addresses");
  return response.data.data;
}
