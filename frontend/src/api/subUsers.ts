import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
  details?: string;
};

export type SubUser = {
  id: string;
  userName: string;
  email: string;
  isActive: boolean;
  lastLoginAtUtc: string | null;
  createdAtUtc: string;
  allowedSections: string[] | null;
};

export type CreateSubUserPayload = {
  userName: string;
  email: string;
  password: string;
  allowedSections: string[] | null;
};

export type UpdateSubUserPayload = {
  email: string;
  password?: string | null;
  isActive: boolean;
  allowedSections: string[] | null;
};

/** Alt-kullanıcı izin matrisinin evreni; backend CustomerPanelSections.All ile birebir aynı sırada. */
export const SUB_USER_SECTIONS = ["sites", "mail", "database", "files", "backup", "tasks"] as const;

export async function fetchSubUsers(): Promise<SubUser[]> {
  const response = await apiClient.get<ApiEnvelope<SubUser[]>>("/sub-users");
  return response.data.data;
}

export async function createSubUser(payload: CreateSubUserPayload): Promise<SubUser> {
  const response = await apiClient.post<ApiEnvelope<SubUser>>("/sub-users", payload);
  return response.data.data;
}

export async function updateSubUser(id: string, payload: UpdateSubUserPayload): Promise<SubUser> {
  const response = await apiClient.put<ApiEnvelope<SubUser>>(`/sub-users/${encodeURIComponent(id)}`, payload);
  return response.data.data;
}

export async function deleteSubUser(id: string): Promise<void> {
  await apiClient.delete<ApiEnvelope<object>>(`/sub-users/${encodeURIComponent(id)}`);
}
