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

export async function installAppTemplate(payload: { templateId: string; siteName: string; targetPath: string; cleanInstall?: boolean; }) {
  const response = await apiClient.post<ApiEnvelope<AppTemplate>>("/store/install", payload);
  return response.data.data;
}
