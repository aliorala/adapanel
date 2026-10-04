import { apiClient } from "./client";
import { UserFacingError } from "../utils/apiError";

type ApiEnvelope<T> = { success: boolean; data: T; message: string; code: number };

// Self-servis (bayi/admin) sahip olunan veritabanı — parola/kredensiyel İÇERMEZ.
export type OwnedDatabase = { engine: string; name: string };

function measuredDatabases(envelope: ApiEnvelope<OwnedDatabase[]>): OwnedDatabase[] {
  const rows = envelope?.data;
  if (envelope?.success !== true || !Array.isArray(rows) || rows.some(row => !row ||
    typeof row.engine !== "string" || !row.engine.trim() || typeof row.name !== "string" || !row.name.trim())) {
    throw new UserFacingError("panel.myDb.resultUnconfirmed");
  }
  return rows;
}

export async function getMyDatabases() {
  const response = await apiClient.get<ApiEnvelope<OwnedDatabase[]>>("/my-databases");
  return measuredDatabases(response.data);
}

export async function createMyDatabase(payload: { engine: string; name: string; userName: string; password: string }) {
  const response = await apiClient.post<ApiEnvelope<OwnedDatabase[]>>("/my-databases", payload);
  return measuredDatabases(response.data);
}

export async function deleteMyDatabase(engine: string, name: string) {
  const response = await apiClient.delete<ApiEnvelope<OwnedDatabase[]>>(`/my-databases/${encodeURIComponent(name)}`, {
    params: { engine }
  });
  return measuredDatabases(response.data);
}
