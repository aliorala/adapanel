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
