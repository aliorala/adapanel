import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type CacheMetrics = {
  version: string;
  usedMemory: string;
  keys: number;
  connectedClients: number;
  hits: number;
  misses: number;
  uptimeSeconds: number;
  measuredAtUtc?: string;
  runId?: string | null;
  databases?: { index: number; keys: number; expires: number | null; averageTtlMilliseconds: number | null }[];
  clients?: { name: string; databaseIndex: number; idleSeconds: number }[] | null;
};

export type CacheRuntime = {
  componentId: string;
  displayName: string;
  serviceName: string;
  status: string;
  installDir: string;
  executablePath: string;
  defaultPort: number;
  isInstalled: boolean;
  isServiceRegistered: boolean;
  metrics?: CacheMetrics | null;
};

export type CacheStatus = {
  redis: CacheRuntime;
  memcached: CacheRuntime;
};

export async function getCacheStatus() {
  const response = await apiClient.get<ApiEnvelope<CacheStatus>>("/cache/status");
  return response.data.data;
}

export async function startRedis() {
  const response = await apiClient.post<ApiEnvelope<CacheRuntime>>("/cache/redis/start", {});
  return response.data.data;
}

export async function stopRedis() {
  const response = await apiClient.post<ApiEnvelope<CacheRuntime>>("/cache/redis/stop", {});
  return response.data.data;
}

export async function startMemcached() {
  const response = await apiClient.post<ApiEnvelope<CacheRuntime>>("/cache/memcached/start", {});
  return response.data.data;
}

export async function stopMemcached() {
  const response = await apiClient.post<ApiEnvelope<CacheRuntime>>("/cache/memcached/stop", {});
  return response.data.data;
}
