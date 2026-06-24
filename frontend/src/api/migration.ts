import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type MigrateSitePayload = {
  siteName: string;
  createSite: boolean;
  physicalPath: string;
  pullFiles: boolean;
  fileProtocol: string;
  fileHost: string;
  filePort: number;
  fileUserName: string;
  filePassword: string;
  remotePath: string;
  transferDatabase: boolean;
  databaseEngine: string;
  dbHost: string;
  dbPort: number;
  dbUserName: string;
  dbPassword: string;
  sourceDatabase: string;
  targetDatabase: string;
};

export type MigrationStep = { step: string; success: boolean; message: string };
export type MigrateSiteResult = { siteName: string; overallSuccess: boolean; steps: MigrationStep[] };

// Tek akış site göçü: site oluştur + dosya çek (FTP/SFTP) + DB aktar. Adım-adım sonuç döner.
export async function migrateSite(payload: MigrateSitePayload) {
  const response = await apiClient.post<ApiEnvelope<MigrateSiteResult>>("/migration/site", payload);
  return response.data.data;
}
