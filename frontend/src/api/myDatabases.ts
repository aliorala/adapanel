import { apiClient } from "./client";

type ApiEnvelope<T> = { success: boolean; data: T; message: string; code: number };

// Self-servis (bayi/admin) sahip olunan veritabanı — parola/kredensiyel İÇERMEZ.
export type OwnedDatabase = { engine: string; name: string };

export async function getMyDatabases() {
  const response = await apiClient.get<ApiEnvelope<OwnedDatabase[]>>("/my-databases");
  return response.data.data;
}

export async function createMyDatabase(payload: { engine: string; name: string; userName: string; password: string }) {
  const response = await apiClient.post<ApiEnvelope<OwnedDatabase[]>>("/my-databases", payload);
  return response.data.data;
}

export async function deleteMyDatabase(engine: string, name: string) {
  const response = await apiClient.delete<ApiEnvelope<OwnedDatabase[]>>(`/my-databases/${encodeURIComponent(name)}`, {
    params: { engine }
  });
  return response.data.data;
}
