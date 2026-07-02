import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type AppTemplate = {
  id: string;
  displayName: string;
  description: string;
  runtime: string;
  category: string;
  downloadUrl: string;
  requiresDatabase: boolean;
  defaultRootFolder: string;
  requirements: string[];
  automaticSetup: string[];
  featured: boolean;
};

export async function getAppTemplates() {
  const response = await apiClient.get<ApiEnvelope<AppTemplate[]>>("/store");
  return response.data.data;
}

export type AppInstallResult = {
  templateId: string;
  adminUrl: string;
  adminUser: string;
  adminPassword: string;
};

export async function installAppTemplate(payload: { templateId: string; siteName: string; targetPath: string; cleanInstall?: boolean; }) {
  const response = await apiClient.post<ApiEnvelope<AppInstallResult>>("/store/install", payload);
  return response.data.data;
}
