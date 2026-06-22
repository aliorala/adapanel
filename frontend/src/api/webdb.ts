import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type WebDbDeployment = {
  provider: string;
  displayName: string;
  componentId: string;
  siteName: string;
  port: number;
  url: string;
  installDir: string;
  entryFilePath: string;
  phpComponentId: string;
  supportedEngines: string;
  accessMode: string;
  isInstalled: boolean;
  isDeployed: boolean;
  isHealthy: boolean;
  healthMessage: string;
  healthDetails: string;
};

export type WebDbWorkspace = {
  deployments: WebDbDeployment[];
};

export async function getWebDbWorkspace() {
  const response = await apiClient.get<ApiEnvelope<WebDbWorkspace>>("/webdb/url");
  return response.data.data;
}

export async function deployPhpMyAdmin() {
  const response = await apiClient.post<ApiEnvelope<WebDbDeployment>>("/webdb/phpmyadmin", {});
  return response.data.data;
}

export async function deployAdminer() {
  const response = await apiClient.post<ApiEnvelope<WebDbDeployment>>("/webdb/adminer", {});
  return response.data.data;
}
