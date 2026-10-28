import { apiClient } from "./client";

type ApiEnvelope<T> = { success: boolean; data: T; message: string; code: number };
export type MailServerIdentity = {
  supported: boolean;
  configuredHostName: string;
  currentHostName: string;
  domain: string;
  contactEmail: string;
  pinned: boolean;
  applied: boolean;
  steps: { key: string; status: string; message: string }[];
  ptrRequest: string;
};
export type MailServerIdentityRequest = { domain: string; hostName: string; contactEmail: string };
export async function getMailServerIdentity() {
  const response = await apiClient.get<ApiEnvelope<MailServerIdentity>>("/mail/server-identity");
  return response.data.data;
}
export async function setMailServerIdentity(request: MailServerIdentityRequest) {
  const response = await apiClient.post<ApiEnvelope<MailServerIdentity>>("/mail/server-identity", request, { timeout: 12 * 60 * 1000 });
  return response.data.data;
}
