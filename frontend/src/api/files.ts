import { apiClient } from "./client";
import { UserFacingError } from "../utils/apiError";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type FileEntry = {
  name: string;
  path: string;
  isDirectory: boolean;
  size: number;
  lastModifiedUtc: string | null;
  extension: string;
};

export type FileWorkspace = {
  path: string;
  parentPath: string;
  displayName: string;
  physicalPath: string;
  isDirectory: boolean;
  content: string;
  contentLoaded?: boolean;
  contentUnavailableReasonKey?: string;
  permissionsVerified?: boolean | null;
  permissionsDetail?: string;
  entries: FileEntry[];
};

export async function getFileWorkspace(path = "") {
  const response = await apiClient.get<ApiEnvelope<FileWorkspace>>("/files", {
    params: { path }
  });

  // Scoped accounts see the sites listing directly when they request the virtual root.
  const responsePath = response.data?.data?.path;
  const expectedPath = !filePathIdentity(path) && typeof responsePath === "string" && filePathIdentity(responsePath) === "sites" ? "sites" : path;
  return measuredReadableWorkspace(response.data, expectedPath);
}

function measuredReadableWorkspace(envelope: ApiEnvelope<FileWorkspace>, path: string) {
  const result = measuredWorkspace(envelope, path);
  const rows = new Set<string>();
  const validEntries = result.entries.every(entry => {
    if (typeof entry.name !== "string" || !entry.name.trim() || /[\\/]/.test(entry.name) || entry.name === "." || entry.name === ".." ||
      typeof entry.isDirectory !== "boolean" || typeof entry.size !== "number" || !Number.isFinite(entry.size) || entry.size < 0 ||
      typeof entry.extension !== "string" || (entry.lastModifiedUtc !== null && typeof entry.lastModifiedUtc !== "string")) return false;
    const key = filePathIdentity(entry.path);
    if (key !== filePathIdentity(`${result.path}/${entry.name}`) || rows.has(key)) return false;
    rows.add(key);
    return true;
  });
  if (!validEntries || [result.parentPath, result.displayName, result.physicalPath].some(value => typeof value !== "string") ||
    (result.contentLoaded !== undefined && typeof result.contentLoaded !== "boolean") ||
    (result.contentUnavailableReasonKey !== undefined && typeof result.contentUnavailableReasonKey !== "string") ||
    (!result.isDirectory && result.entries.length > 0)) {
    throw new UserFacingError("panel.files.resultUnconfirmed");
  }
  return result;
}

export async function saveFile(path: string, content: string) {
  const response = await apiClient.put<ApiEnvelope<FileWorkspace>>("/files", {
    path,
    content
  });

  const result = measuredWorkspace(response.data, path);
  if (result.isDirectory || result.contentLoaded !== true || result.content !== content) throw new UserFacingError("panel.files.resultUnconfirmed");
  return result;
}

/**
 * BOŞ dosya oluşturur (ör. `.htaccess`, `robots.txt`).
 *
 * `saveFile` ile AYNI ucu kullanır ama `failIfExists` bayrağıyla: dosya varsa sunucu isteği
 * `files.already_exists` (409) ile reddeder, üstüne YAZMAZ.
 *
 * Bayrak neden şart: ekran adın listede olup olmadığına da bakıyor, ama o denetim İSTEMCİDE.
 * Liste eskimişse ya da ikinci bir oturum aynı anda dosyayı oluşturmuşsa "oluştur" demek var
 * olan dosyayı SIFIRLAMAK olurdu. Yarışı yalnız dosyayı yaratan işlemin kendisi kapatabilir;
 * sunucu bunu `FileMode.CreateNew` ile tek işleme indiriyor.
 */
export async function createFile(path: string, content = "") {
  const response = await apiClient.put<ApiEnvelope<FileWorkspace>>("/files", {
    path,
    content,
    failIfExists: true
  });

  const result = measuredWorkspace(response.data, path);
  if (result.isDirectory || result.contentLoaded !== true || result.content !== content || result.entries.length !== 0) {
    throw new UserFacingError("panel.files.resultUnconfirmed");
  }
  return result;
}

export async function deletePath(path: string) {
  const response = await apiClient.delete<ApiEnvelope<FileWorkspace>>("/files", {
    data: { path }
  });

  const result = measuredWorkspace(response.data, parentPathOf(path));
  if (!result.isDirectory || result.entries.some(entry => filePathIdentity(entry.path) === filePathIdentity(path))) {
    throw new UserFacingError("panel.files.resultUnconfirmed");
  }
  return result;
}

export async function createDirectory(path: string) {
  const response = await apiClient.post<ApiEnvelope<FileWorkspace>>("/files/mkdir", {
    path
  });

  const result = measuredWorkspace(response.data, path);
  if (!result.isDirectory) throw new UserFacingError("panel.files.resultUnconfirmed");
  return result;
}

export async function updatePermissions(path: string, principal: string, rights: string) {
  const response = await apiClient.post<ApiEnvelope<FileWorkspace>>("/files/acl", {
    path,
    principal,
    rights
  });

  const result = measuredWorkspace(response.data, path);
  if ((result.permissionsVerified != null && typeof result.permissionsVerified !== "boolean") ||
    (result.permissionsDetail !== undefined && typeof result.permissionsDetail !== "string")) {
    throw new UserFacingError("panel.files.resultUnconfirmed");
  }
  return { ...result, permissionsVerified: result.permissionsVerified ?? null, permissionsDetail: result.permissionsDetail ?? "" };
}

export async function uploadFile(path: string, file: File) {
  const expectedPath = filePathIdentity(`${path}/${file.name}`);
  const body = new FormData();
  body.append("file", file);
  const response = await apiClient.post<ApiEnvelope<{ path: string }>>("/files/upload", body, {
    params: { path },
    headers: { "Content-Type": "multipart/form-data" }
  });
  const result = response.data?.data;
  if (response.data?.success !== true || !result || typeof result.path !== "string" || filePathIdentity(result.path) !== expectedPath) {
    throw new UserFacingError("panel.files.resultUnconfirmed");
  }
  return result;
}

/**
 * KLASÖR BOYUTU — sonucun EKSİK olabileceği açıkça bildirilir.
 *
 * Listede klasörler için boyut `0` gelir (dizin girdisinin kendi uzunluğu); gerçek boyut ancak
 * ağacı gezerek bulunur ve bu PAHALI bir iştir. Sunucu öge/süre bütçesini aşarsa taramayı keser
 * ve `truncated` ile söyler: o hâlde `totalBytes` bir ALT SINIRDIR, toplam değildir.
 *
 * `truncatedReasonKey` serbest metin değil çeviri anahtarıdır (`errorKey` ile aynı sözleşme):
 * sebebi kullanıcının dilinde göstermek arayüzün işi.
 */
export type DirectorySizeResult = {
  path: string;
  totalBytes: number;
  fileCount: number;
  directoryCount: number;
  /** true ise sayım TAMAMLANMADI; gösterilen değer gerçek boyutun altındadır. */
  truncated: boolean;
  truncatedReasonKey: string;
};

export async function getDirectorySize(path: string) {
  const response = await apiClient.post<ApiEnvelope<DirectorySizeResult>>("/files/size", { path });
  const value = response.data?.data;
  if (response.data?.success !== true || !value || typeof value.path !== "string" || filePathIdentity(value.path) !== filePathIdentity(path) ||
    ![value.totalBytes, value.fileCount, value.directoryCount].every(count => Number.isSafeInteger(count) && count >= 0) ||
    typeof value.truncated !== "boolean" || typeof value.truncatedReasonKey !== "string" || (value.truncated && !value.truncatedReasonKey.trim())) {
    throw new UserFacingError("panel.files.resultUnconfirmed");
  }
  return value;
}

export function getDownloadUrl(path: string) {
  return `/api/v1/files/download?path=${encodeURIComponent(path)}`;
}

export async function compressPath(path: string, destinationPath = "") {
  const response = await apiClient.post<ApiEnvelope<FileWorkspace>>("/files/compress", { path, destinationPath });
  const destination = destinationPath.trim() || `${path.replace(/[\\/]+$/, "")}.zip`;
  const result = measuredReadableWorkspace(response.data, parentPathOf(destination));
  if (!result.isDirectory || !result.entries.some(entry => !entry.isDirectory && filePathIdentity(entry.path) === filePathIdentity(destination))) {
    throw new UserFacingError("panel.files.resultUnconfirmed");
  }
  return result;
}

export async function extractPath(path: string, destinationPath = "") {
  const response = await apiClient.post<ApiEnvelope<FileWorkspace>>("/files/extract", { path, destinationPath });
  const result = measuredReadableWorkspace(response.data, destinationPath.trim() || parentPathOf(path));
  if (!result.isDirectory) throw new UserFacingError("panel.files.resultUnconfirmed");
  return result;
}

export type FileSearchResult = {
  path: string;
  query: string;
  items: FileEntry[];
  scannedFiles: number;
  truncated: boolean;
  truncatedReasonKey: string;
};

export async function searchFiles(path: string, query: string) {
  const response = await apiClient.post<ApiEnvelope<FileSearchResult>>("/files/search", { path, query });
  const value = response.data?.data;
  const prefix = `${filePathIdentity(path)}/`, seen = new Set<string>();
  const validItems = Array.isArray(value?.items) && value.items.every(entry => {
    if (!entry || typeof entry.path !== "string" || typeof entry.name !== "string" || !entry.name.trim() || /[\\/]/.test(entry.name) ||
      entry.isDirectory !== false || typeof entry.size !== "number" || !Number.isFinite(entry.size) || entry.size < 0 ||
      typeof entry.extension !== "string" || (entry.lastModifiedUtc !== null && typeof entry.lastModifiedUtc !== "string")) return false;
    const key = filePathIdentity(entry.path), parts = key.split("/");
    if (!key.startsWith(prefix) || parts.some(part => !part || part === "." || part === "..") || parts[parts.length - 1] !== entry.name.toLowerCase() || seen.has(key)) return false;
    seen.add(key);
    return true;
  });
  if (response.data?.success !== true || !value || typeof value.path !== "string" || filePathIdentity(value.path) !== filePathIdentity(path) ||
    value.query !== query.trim() || !validItems || !Number.isSafeInteger(value.scannedFiles) || value.scannedFiles < value.items.length ||
    typeof value.truncated !== "boolean" || typeof value.truncatedReasonKey !== "string" || (value.truncated && !value.truncatedReasonKey.trim())) {
    throw new UserFacingError("panel.files.resultUnconfirmed");
  }
  return value;
}

/**
 * ÖGE BAŞINA sonuç — "kaçı oldu, kaçı olmadı, neden olmadı".
 *
 * Taşıma/kopyalama çoğul bir iştir: sekiz ögeden ikisi takıldığında tek bir "başarılı" yanıtı
 * kullanıcıya dosyalarının NEREDE olduğunu söylemez. `errorKey` serbest metin değil çeviri
 * anahtarıdır (ör. `files.move_target_exists`); sebebi kullanıcının dilinde göstermek arayüzün işi.
 */
export type TransferItemResult = {
  sourcePath: string;
  targetPath: string;
  errorKey: string;
};

export type MoveItemResult = TransferItemResult & { moved: boolean };

export type MoveResult = {
  movedCount: number;
  failedCount: number;
  items: MoveItemResult[];
  workspace: FileWorkspace;
};

export type CopyItemResult = TransferItemResult & { copied: boolean };

export type CopyResult = {
  copiedCount: number;
  failedCount: number;
  items: CopyItemResult[];
  workspace: FileWorkspace;
};

function filePathIdentity(path: string) {
  return path.trim().replace(/\\/g, "/").replace(/^\/+|\/+$/g, "").toLowerCase();
}

function measuredWorkspace(envelope: ApiEnvelope<FileWorkspace>, path: string): FileWorkspace {
  const result = envelope?.data;
  if (envelope?.success !== true || !result || typeof result.path !== "string" || filePathIdentity(result.path) !== filePathIdentity(path) ||
    typeof result.isDirectory !== "boolean" || typeof result.content !== "string" || !Array.isArray(result.entries) ||
    result.entries.some(entry => !entry || typeof entry.path !== "string" || !entry.path.trim())) {
    throw new UserFacingError("panel.files.resultUnconfirmed");
  }
  return result;
}

function measuredTransfer<T extends MoveResult | CopyResult>(
  envelope: ApiEnvelope<T>, paths: readonly string[], destination: string, copied: boolean, newName?: string
): T {
  const result = envelope?.data;
  const items = result?.items;
  const count = result && (copied ? (result as CopyResult).copiedCount : (result as MoveResult).movedCount);
  const expected = new Map(paths.map(path => [filePathIdentity(path), filePathIdentity(`${destination}/${newName ?? path.replace(/\\/g, "/").replace(/\/+$/, "").split("/").pop()}`)]));
  const finished = (item: MoveItemResult | CopyItemResult) => copied ? (item as CopyItemResult).copied : (item as MoveItemResult).moved;
  const rowIds = new Set<string>();
  const validItems = Array.isArray(items) && items.length === expected.size && items.every(item => {
    if (!item || typeof item.sourcePath !== "string" || typeof item.targetPath !== "string" || typeof item.errorKey !== "string" || typeof finished(item) !== "boolean") return false;
    const source = filePathIdentity(item.sourcePath), target = expected.get(source);
    if (!target || rowIds.has(source)) return false;
    rowIds.add(source);
    return (finished(item) || item.targetPath !== "") ? filePathIdentity(item.targetPath) === target : true;
  });
  const workspace = result?.workspace;
  if (envelope?.success !== true || !result || !validItems || !Number.isSafeInteger(count) || !Number.isSafeInteger(result.failedCount) ||
    count !== items!.filter(item => finished(item)).length || result.failedCount !== items!.filter(item => !finished(item)).length ||
    !workspace || typeof workspace.path !== "string" || filePathIdentity(workspace.path) !== filePathIdentity(destination) || workspace.isDirectory !== true || !Array.isArray(workspace.entries)) {
    throw new UserFacingError("panel.files.resultUnconfirmed");
  }
  return result;
}

/** Sanal yolun üst klasörü; kök seviyesindeki bir öge için boş dize. */
function parentPathOf(path: string) {
  const normalized = path.replace(/\\/g, "/").replace(/\/+$/, "");
  const index = normalized.lastIndexOf("/");
  return index <= 0 ? "" : normalized.slice(0, index);
}

/** Bir ya da birden çok ögeyi başka klasöre taşır. Yanıt öge başına sonucu taşır. */
export async function moveEntries(paths: string[], destinationPath: string, overwrite = false) {
  paths = [...paths];
  const response = await apiClient.post<ApiEnvelope<MoveResult>>("/files/move", {
    paths,
    destinationPath,
    overwrite
  });

  return measuredTransfer(response.data, paths, destinationPath, false);
}

/**
 * Yeniden adlandırma AYRI bir uç değil: hedefi ögenin KENDİ klasörü olan tek ögelik bir taşımadır
 * (bkz. `MoveFilesRequest`). Üst klasör burada tek yerde hesaplanır; her ekran kendi yol
 * kırpmasını yazarsa biri "/" ile diğeri "\" ile uğraşır ve ikisi ayrışır.
 */
export async function renameEntry(path: string, newName: string, overwrite = false) {
  const response = await apiClient.post<ApiEnvelope<MoveResult>>("/files/move", {
    paths: [path],
    destinationPath: parentPathOf(path),
    newName,
    overwrite
  });

  return measuredTransfer(response.data, [path], parentPathOf(path), false, newName.trim());
}

/** Bir ya da birden çok ögeyi başka klasöre kopyalar. Yanıt öge başına sonucu taşır. */
export async function copyEntries(paths: string[], destinationPath: string, overwrite = false) {
  paths = [...paths];
  const response = await apiClient.post<ApiEnvelope<CopyResult>>("/files/copy", {
    paths,
    destinationPath,
    overwrite
  });

  return measuredTransfer(response.data, paths, destinationPath, true);
}
