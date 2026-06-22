import { apiClient } from "./client";

type ApiEnvelope<T> = { success: boolean; data: T; message: string; code: number };

export type ServiceHealth = {
  name: string;
  label: string;
  status: string; // Running | Stopped | NotInstalled | Unknown
};

export async function getServerHealth() {
  const response = await apiClient.get<ApiEnvelope<ServiceHealth[]>>("/server-health");
  return response.data.data;
}

export async function restartService(name: string) {
  const response = await apiClient.post<ApiEnvelope<ServiceHealth>>("/server-health/restart", { name });
  return response.data.data;
}
