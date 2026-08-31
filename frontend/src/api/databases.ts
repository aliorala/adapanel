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
  users: string[];
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
  /** Sunucu bildirir: bu motorda "guvenligi sikilastir" anlamli bir is yapiyor mu. */
  supportsHardening: boolean;
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
  // Plesk gibi: siteye bağla → site silinince bu DB de cascade ile gider.
  siteName?: string;
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
  /**
   * İSTEĞE BAĞLI ve normalde GÖNDERİLMEZ. Sabit "%" gönderiliyordu; panelin kendi kullanıcıları
   * localhost'ta durduğu için yetki var olmayan bir hesaba yazılıyordu. Boş bırakıldığında sunucu
   * hesabın host'larını motordan OKUR (bkz. ImportDatabaseRequest.Host).
   */
  host?: string;
  password: string;
  // Plesk gibi: siteye bağla → site silinince bu DB de cascade ile gider.
  siteName?: string;
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

// KULLANICI bazlı uzak erişim. Sunucu geneli tek anahtar yerine her veritabanı kullanıcısı için
// ayrı kip + kaynak listesi; motor tarafı (MySQL host / pg_hba) ve güvenlik duvarı birlikte yönetilir.
export type RemoteAccessMode = "closed" | "allowlist" | "anywhere";

export type DatabaseUserRemoteAccess = {
  engine: string;
  userName: string;
  mode: RemoteAccessMode;
  allowedOrigins: string[];
  // Motordan OKUNAN gerçek (panelin kaydı değil): hesap fiilen nereden geçerli.
  engineHosts: string[];
  remoteOpen: boolean;
  databases: string[];
};

export type DatabaseRemoteAccessEngine = {
  engine: string;
  displayName: string;
  installed: boolean;
  supported: boolean;
  mechanism: "mysqlHost" | "pgHba" | "none";
  serverHost: string;
  port: number;
  firewallOpen: boolean;
  firewallAllowedAddresses: string[];
  // i18n anahtarı; boş = uyarı yok.
  warningKey: string;
  users: DatabaseUserRemoteAccess[];
};

export async function getUserRemoteAccess() {
  const response = await apiClient.get<ApiEnvelope<{ engines: DatabaseRemoteAccessEngine[] }>>(
    "/db/remote-access/users"
  );
  return response.data.data.engines;
}

export async function setUserRemoteAccess(payload: {
  engine: string;
  userName: string;
  mode: RemoteAccessMode;
  allowedOrigins: string[];
  // "Her yer" açık onay ister; onaysız istek sunucuda reddedilir.
  confirmAnywhere: boolean;
  // Güvenlik duvarına yalnız bu bayrak açıkken dokunulur (port sessizce açılmaz).
  applyFirewall: boolean;
}) {
  const response = await apiClient.put<ApiEnvelope<DatabaseRemoteAccessEngine>>(
    "/db/remote-access/users",
    payload
  );
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
  targetUserName?: string;
  targetPassword?: string;
};

export type DatabaseTransferResult = {
  engine: string;
  sourceDatabase: string;
  targetDatabase: string;
  size: number;
  userName: string;
  password: string;
};

// profesyonel paneller gibi sunucu taraflı taşıma: kaynak DB bilgileri → sistem mysqldump→mysql ile otomatik aktarır.
// Veritabanini baska bir siteye tasir. MySQL yetkilerine DOKUNULMAZ; calisan uygulamanin
// baglantisi kopmaz. Degisen sey panelde kimin gordugu ve kimin kotasina sayildigidir.
export async function moveDatabase(engine: string, name: string, targetSiteName: string) {
  await apiClient.post<ApiEnvelope<object>>(
    `/db/${encodeURIComponent(name)}/move?engine=${encodeURIComponent(engine)}`,
    { targetSiteName }
  );
}

// Adminer'i tek tikla acar: panel tek kullanimlik bir belirtec uretir, Adminer o kimlikle
// oturum acar. Adminer'in signon kipi yoktur; eklenti mekanizmasi kullanilir.
export async function openAdminer(engine: string, databaseName: string) {
  const response = await apiClient.post<ApiEnvelope<{ url: string }>>("/webdb/adminer/open", {
    engine,
    databaseName
  });
  return response.data.data;
}

export type DatabaseUserRef = { userName: string; host: string };

export type DatabaseDetail = {
  engine: string;
  name: string;
  host: string;
  port: number;
  engineName: string;
  users: DatabaseUserRef[];
  // null = OLCULEMEDI (0 degil). "0 bayt" bos bir veritabani demektir; ikisi ayri seylerdir.
  sizeBytes: number | null;
  tableCount: number;
  siteName: string;
};

export async function getDatabaseDetail(engine: string, name: string) {
  const response = await apiClient.get<ApiEnvelope<DatabaseDetail>>(
    `/db/${encodeURIComponent(name)}/detail?engine=${encodeURIComponent(engine)}`
  );
  return response.data.data;
}

export type DatabaseTableCheck = {
  table: string;
  engine: string;
  sizeBytes: number;
  status: string;
  canRepair: boolean;
  canRebuild: boolean;
  messages: string[];
};

export type DatabaseCheckResult = {
  engine: string;
  database: string;
  tables: DatabaseTableCheck[];
  healthyCount: number;
  problemCount: number;
  skippedCount: number;
  repairAttempted: boolean;
};

// repairTables bos ise YALNIZCA denetler (hicbir sey degistirmez).
export async function checkAndRepairDatabase(engine: string, name: string, repairTables: string[] = []) {
  const response = await apiClient.post<ApiEnvelope<DatabaseCheckResult>>(
    `/db/${encodeURIComponent(name)}/check?engine=${encodeURIComponent(engine)}`,
    repairTables
  );
  return response.data.data;
}

// Var olan bir kullaniciya bir veritabani uzerinde yetki verir.
// Panelde bir veritabanina SONRADAN kullanici baglamanin tek yolu buydu ve arayuzde karsiligi yoktu:
// disaridan gelmis (ya da kullanicisi silinmis) bir veritabani kullanicisiz kaliyor ve ona
// dokunulamiyordu -- yani veritabani fiilen erisilemez oluyordu.
export async function grantDatabaseAccess(payload: {
  engine: string;
  databaseName: string;
  userName: string;
  allowedHost?: string;
  privileges?: string[];
}) {
  const response = await apiClient.post<ApiEnvelope<DatabaseWorkspace>>("/db/grant", payload);
  return response.data.data;
}

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

