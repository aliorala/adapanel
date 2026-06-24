import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type DatabaseRecord = {
  engine: string;
  name: string;
  owner: string;
  encoding: string;
  collation: string;
};

export type DatabaseUserRecord = {
  engine: string;
  userName: string;
  host: string;
  canLogin: boolean;
};

export type DatabaseWorkspace = {
  engine: string;
  displayName: string;
  componentId: string;
  serviceName: string;
  host: string;
  port: number;
  adminUserName: string;
  adminPassword: string;
  supportsHostScopedUsers: boolean;
  databases: DatabaseRecord[];
  users: DatabaseUserRecord[];
};

export type DatabaseEngineStatus = {
  engine: string;
  displayName: string;
  installed: boolean;
  ready: boolean;
  systemDetected: boolean;
  host: string;
  port: number;
  adminUserName: string;
};

export type RegisterCredentialsPayload = {
  engine: string;
  adminUserName?: string;
  password: string;
  port?: number;
};

export type CreateDatabasePayload = {
  engine: string;
  name: string;
  // profesyonel paneller gibi: veritabanıyla birlikte kullanıcı (boşsa kullanıcı oluşturulmaz)
  userName?: string;
  password?: string;
};

export type CreateDatabaseUserPayload = {
  engine: string;
  userName: string;
  password: string;
  allowedHost: string;
  databaseName?: string;
};

export async function getDatabaseEngines() {
  const response = await apiClient.get<ApiEnvelope<DatabaseEngineStatus[]>>("/db/engines");
  return response.data.data;
}

export async function registerDatabaseCredentials(payload: RegisterCredentialsPayload) {
  const response = await apiClient.post<ApiEnvelope<DatabaseEngineStatus>>("/db/credentials", payload);
  return response.data.data;
}

export async function getDatabaseWorkspace(engine: string) {
  const response = await apiClient.get<ApiEnvelope<DatabaseWorkspace>>("/db", {
    params: { engine }
  });

  return response.data.data;
}

export async function createDatabase(payload: CreateDatabasePayload) {
  const response = await apiClient.post<ApiEnvelope<DatabaseWorkspace>>("/db", payload);
  return response.data.data;
}

export async function deleteDatabase(engine: string, name: string) {
  await apiClient.delete<ApiEnvelope<object>>(`/db/${encodeURIComponent(name)}`, {
    params: { engine }
  });
}

export async function createDatabaseUser(payload: CreateDatabaseUserPayload) {
  const response = await apiClient.post<ApiEnvelope<DatabaseWorkspace>>("/db/users", payload);
  return response.data.data;
}

export type ImportDatabasePayload = {
  engine: string;
  databaseName: string;
  userName: string;
  host: string;
  password: string;
};

// Var olan DB+kullanıcıyı panele tanıt (GRANT ALL eksikse + parolayı sakla).
export async function importDatabase(payload: ImportDatabasePayload) {
  const response = await apiClient.post<ApiEnvelope<DatabaseWorkspace>>("/db/import", payload);
  return response.data.data;
}

export async function openPhpMyAdmin(engine: string, databaseName?: string) {
  const response = await apiClient.post<ApiEnvelope<{ url: string }>>("/webdb/phpmyadmin/open", { engine, databaseName });
  return response.data.data;
}

// profesyonel panel benzeri uzak erişim: 3306'yı yalnız belirtilen IP'ye aç/kapat (masaüstü DB istemcileri için).
export type RemoteDbAccess = {
  enabled: boolean;
  allowedIp: string;
  serverHost: string;
  port: number;
};

export async function getRemoteDbAccess() {
  const response = await apiClient.get<ApiEnvelope<RemoteDbAccess>>("/db/remote-access");
  return response.data.data;
}

export async function setRemoteDbAccess(enabled: boolean, allowedIp: string) {
  const response = await apiClient.put<ApiEnvelope<RemoteDbAccess>>("/db/remote-access", { enabled, allowedIp });
  return response.data.data;
}

export async function setDatabaseUserPassword(payload: { engine: string; userName: string; password: string; allowedHost?: string }) {
  const response = await apiClient.post<ApiEnvelope<DatabaseWorkspace>>("/db/users/password", payload);
  return response.data.data;
}

export async function deleteDatabaseUser(engine: string, userName: string, host: string) {
  await apiClient.delete<ApiEnvelope<object>>(`/db/users/${encodeURIComponent(userName)}`, {
    params: { engine, host }
  });
}

export type DatabaseTransferPayload = {
  engine: string;
  sourceHost: string;
  sourcePort: number;
  sourceUserName: string;
  sourcePassword: string;
  sourceDatabase: string;
  targetDatabase?: string;
};

export type DatabaseTransferResult = {
  engine: string;
  sourceDatabase: string;
  targetDatabase: string;
  size: number;
};

// profesyonel paneller gibi sunucu taraflı taşıma: kaynak DB bilgileri → sistem mysqldump→mysql ile otomatik aktarır.
export async function transferDatabase(payload: DatabaseTransferPayload) {
  const response = await apiClient.post<ApiEnvelope<DatabaseTransferResult>>("/db/transfer", payload);
  return response.data.data;
}

// K2 (panel mysql_secure_installation gibi): güvensiz DB yönetici hesaplarını (anonim + uzak root) kaldırır.
// root@localhost'a dokunmaz. Kaldırılanların listesini döner (boş = zaten güvenli).
export async function hardenDatabase(engine: string) {
  const response = await apiClient.post<ApiEnvelope<string[]>>("/db/harden", null, { params: { engine } });
  return response.data.data;
}

