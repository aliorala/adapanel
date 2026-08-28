import { apiClient } from "./client";

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
  entries: FileEntry[];
};

export async function getFileWorkspace(path = "") {
  const response = await apiClient.get<ApiEnvelope<FileWorkspace>>("/files", {
    params: { path }
  });

  return response.data.data;
}

export async function saveFile(path: string, content: string) {
  const response = await apiClient.put<ApiEnvelope<FileWorkspace>>("/files", {
    path,
    content
  });

  return response.data.data;
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

  return response.data.data;
}

export async function deletePath(path: string) {
  const response = await apiClient.delete<ApiEnvelope<FileWorkspace>>("/files", {
    data: { path }
  });

  return response.data.data;
}

export async function createDirectory(path: string) {
  const response = await apiClient.post<ApiEnvelope<FileWorkspace>>("/files/mkdir", {
    path
  });

  return response.data.data;
}

export async function updatePermissions(path: string, principal: string, rights: string) {
  const response = await apiClient.post<ApiEnvelope<FileWorkspace>>("/files/acl", {
    path,
    principal,
    rights
  });

  return response.data.data;
}

export async function uploadFile(path: string, file: File) {
  const body = new FormData();
  body.append("file", file);
  const response = await apiClient.post<ApiEnvelope<{ path: string }>>("/files/upload", body, {
    params: { path },
    headers: { "Content-Type": "multipart/form-data" }
  });
  return response.data.data;
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
  return response.data.data;
}

export function getDownloadUrl(path: string) {
  return `/api/v1/files/download?path=${encodeURIComponent(path)}`;
}

export async function compressPath(path: string, destinationPath = "") {
  const response = await apiClient.post<ApiEnvelope<FileWorkspace>>("/files/compress", { path, destinationPath });
  return response.data.data;
}

export async function extractPath(path: string, destinationPath = "") {
  const response = await apiClient.post<ApiEnvelope<FileWorkspace>>("/files/extract", { path, destinationPath });
  return response.data.data;
}

export async function searchFiles(path: string, query: string) {
  const response = await apiClient.post<ApiEnvelope<FileEntry[]>>("/files/search", { path, query });
  return response.data.data;
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

/** Sanal yolun üst klasörü; kök seviyesindeki bir öge için boş dize. */
function parentPathOf(path: string) {
  const normalized = path.replace(/\\/g, "/").replace(/\/+$/, "");
  const index = normalized.lastIndexOf("/");
  return index <= 0 ? "" : normalized.slice(0, index);
}

/** Bir ya da birden çok ögeyi başka klasöre taşır. Yanıt öge başına sonucu taşır. */
export async function moveEntries(paths: string[], destinationPath: string, overwrite = false) {
  const response = await apiClient.post<ApiEnvelope<MoveResult>>("/files/move", {
    paths,
    destinationPath,
    overwrite
  });

  return response.data.data;
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

  return response.data.data;
}

/** Bir ya da birden çok ögeyi başka klasöre kopyalar. Yanıt öge başına sonucu taşır. */
export async function copyEntries(paths: string[], destinationPath: string, overwrite = false) {
  const response = await apiClient.post<ApiEnvelope<CopyResult>>("/files/copy", {
    paths,
    destinationPath,
    overwrite
  });

  return response.data.data;
}
