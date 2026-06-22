import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type NginxStatus = {
  isInstalled: boolean;
  isRunning: boolean;
  isStartupTaskRegistered: boolean;
  status: string;
  version: string;
  installDir: string;
  configPath: string;
  listenAddress: string;
  listenPort: number;
  serverName: string;
  upstreamUrl: string;
  cacheEnabled: boolean;
  cacheSizeMb: number;
  lastConfigTest: string;
  accessUrl: string;
};

export type NginxConfiguration = {
  listenAddress: string;
  listenPort: number;
  serverName: string;
  upstreamUrl: string;
  cacheEnabled: boolean;
  cacheSizeMb: number;
};

async function postAction(path: string, payload: object = {}) {
  const response = await apiClient.post<ApiEnvelope<NginxStatus>>(`/nginx/${path}`, payload);
  return response.data.data;
}

export async function getNginxStatus() {
  const response = await apiClient.get<ApiEnvelope<NginxStatus>>("/nginx/status");
  return response.data.data;
}

export async function configureNginx(configuration: NginxConfiguration) {
  return postAction("configure", configuration);
}

export async function startNginx() {
  return postAction("start");
}

export async function stopNginx() {
  return postAction("stop");
}

export async function reloadNginx() {
  return postAction("reload");
}

export async function testNginxConfiguration() {
  return postAction("test");
}
