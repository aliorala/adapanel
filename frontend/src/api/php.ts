import { apiClient } from "./client";
import type { HandlerOrderMeasurement } from "./asp";
import { UserFacingError } from "../utils/apiError";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type PhpVersionRecord = {
  componentId: string;
  displayName: string;
  version: string;
  installDir: string;
  executablePath: string;
  status: string;
  isFastCgiRegistered: boolean;
  installedAtUtc: string;
};

export type SitePhpAssignment = {
  siteName: string;
  componentId: string;
  displayName: string;
  version: string;
  executablePath: string;
  handlerName: string;
  isFastCgiRegistered: boolean;
  handlerOrder?: HandlerOrderMeasurement;
  handlerOrderNoticeKey?: string;
};

function unconfirmed(): never { throw new UserFacingError("panel.php.resultUnconfirmed"); }
function object(value: unknown): Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value)
    ? value as Record<string, unknown> : unconfirmed();
}
function data(value: unknown): unknown {
  const envelope = object(value);
  if (envelope.success !== true) return unconfirmed();
  return envelope.data;
}
function text(row: Record<string, unknown>, key: string, fallback?: string): string {
  if (row[key] === undefined && fallback !== undefined) return fallback;
  return typeof row[key] === "string" ? row[key] as string : unconfirmed();
}
function identity(row: Record<string, unknown>, key: string): string {
  const value = text(row, key);
  return value.trim() ? value : unconfirmed();
}
function bool(row: Record<string, unknown>, key: string): boolean {
  return typeof row[key] === "boolean" ? row[key] as boolean : unconfirmed();
}
function optionalBool(row: Record<string, unknown>, key: string): boolean | undefined {
  return row[key] === undefined ? undefined : bool(row, key);
}
function sameIdentity(left: string, right: string): boolean {
  return left.trim().toLowerCase() === right.trim().toLowerCase();
}
function versionRecord(value: unknown): PhpVersionRecord {
  const row = object(value), status = text(row, "status");
  if (!["pending", "installed", "failed"].includes(status.toLowerCase())) return unconfirmed();
  return {
    componentId: identity(row, "componentId"), displayName: text(row, "displayName"),
    version: text(row, "version", ""), installDir: text(row, "installDir", ""),
    executablePath: text(row, "executablePath", ""), status,
    isFastCgiRegistered: bool(row, "isFastCgiRegistered"), installedAtUtc: text(row, "installedAtUtc", "")
  };
}
function handlerOrder(value: unknown): HandlerOrderMeasurement {
  // Older servers did not publish the measurement. Absence must not become
  // "ordered" merely because the assignment itself was acknowledged.
  const row = value == null ? { measured: false, handlerFound: false, precedesCatchAll: false } : object(value);
  const measured = bool(row, "measured"), handlerFound = bool(row, "handlerFound"), precedesCatchAll = bool(row, "precedesCatchAll");
  const index = (key: string) => {
    const result = row[key] === undefined ? -1 : row[key];
    return typeof result === "number" && Number.isInteger(result) && result >= -1 ? result : unconfirmed();
  };
  // Match HandlerOrderMeasurement.NoticeKey, not its possibly stale summaries.
  const ordered = measured && handlerFound && precedesCatchAll;
  const unverified = !measured, missing = measured && !handlerFound;
  const noticeKey = ordered ? "" : unverified ? "aspnet.handler_order_unverified" : missing ? "aspnet.handler_missing" : "aspnet.handler_shadowed";
  return { measured, handlerFound, precedesCatchAll, handlerIndex: index("handlerIndex"), catchAllIndex: index("catchAllIndex"),
    blockingHandlerName: text(row, "blockingHandlerName", ""), insertedAtFront: optionalBool(row, "insertedAtFront") ?? false,
    ordered, shadowedByCatchAll: measured && handlerFound && !precedesCatchAll, missing, unverified, noticeKey };
}
function assignmentRecord(value: unknown, siteName: string, componentId: string): SitePhpAssignment {
  const row = object(value), actualSite = identity(row, "siteName"), actualComponent = identity(row, "componentId");
  if (!sameIdentity(actualSite, siteName) || !sameIdentity(actualComponent, componentId)) return unconfirmed();
  const order = handlerOrder(row.handlerOrder);
  return { siteName: actualSite, componentId: actualComponent, displayName: text(row, "displayName"),
    version: text(row, "version", ""), executablePath: text(row, "executablePath", ""), handlerName: text(row, "handlerName"),
    isFastCgiRegistered: bool(row, "isFastCgiRegistered"), handlerOrder: order, handlerOrderNoticeKey: order.noticeKey };
}

export async function getPhpVersions() {
  const response = await apiClient.get<ApiEnvelope<unknown>>("/php/versions");
  const rows = data(response.data);
  if (!Array.isArray(rows)) return unconfirmed();
  const result = rows.map(versionRecord);
  if (new Set(result.map(row => row.componentId.trim().toLowerCase())).size !== result.length) return unconfirmed();
  return result;
}

export async function installPhpVersion(componentId: string) {
  const response = await apiClient.post<ApiEnvelope<unknown>>("/php/install", { componentId });
  const result = object(data(response.data));
  if (!sameIdentity(identity(result, "componentId"), componentId) || text(result, "status").toLowerCase() !== "installed") return unconfirmed();
  if (result.versionMeasured !== undefined) bool(result, "versionMeasured");
  const message = text(object(response.data), "message");
  return message.trim() ? message : unconfirmed();
}

export async function setSitePhpVersion(siteName: string, componentId: string) {
  const response = await apiClient.post<ApiEnvelope<unknown>>(`/sites/${encodeURIComponent(siteName)}/php`, {
    componentId
  });

  return assignmentRecord(data(response.data), siteName, componentId);
}

export type SitePhpSettings = {
  siteName: string;
  memoryLimit: string;
  maxExecutionTime: number;
  uploadMaxFilesize: string;
  postMaxSize: string;
  displayErrors: boolean;
  openBasedirEnabled?: boolean;
  configured: boolean;
  redisEnabled?: boolean;
};

function settingsRecord(value: unknown, siteName: string): SitePhpSettings {
  const row = object(value), actualSite = identity(row, "siteName"), maxExecutionTime = row.maxExecutionTime;
  if (!sameIdentity(actualSite, siteName) || typeof maxExecutionTime !== "number" || !Number.isInteger(maxExecutionTime)) return unconfirmed();
  const openBasedirEnabled = optionalBool(row, "openBasedirEnabled"), redisEnabled = optionalBool(row, "redisEnabled");
  return { siteName: actualSite, memoryLimit: text(row, "memoryLimit"), maxExecutionTime,
    uploadMaxFilesize: text(row, "uploadMaxFilesize"), postMaxSize: text(row, "postMaxSize"),
    displayErrors: bool(row, "displayErrors"), configured: bool(row, "configured"),
    ...(openBasedirEnabled === undefined ? {} : { openBasedirEnabled }), ...(redisEnabled === undefined ? {} : { redisEnabled }) };
}

export async function getSitePhpSettings(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<unknown>>(`/sites/${encodeURIComponent(siteName)}/php/settings`);
  return settingsRecord(data(response.data), siteName);
}

export async function updateSitePhpSettings(siteName: string, payload: Omit<SitePhpSettings, "siteName" | "configured">) {
  const response = await apiClient.post<ApiEnvelope<unknown>>(`/sites/${encodeURIComponent(siteName)}/php/settings`, payload);
  return settingsRecord(data(response.data), siteName);
}

// Native phpredis (redis) PHP uzantısını etkinleştir (Laravel "Class Redis not found" hatasını çözer).
export async function enableRedisExtension() {
  const response = await apiClient.post<ApiEnvelope<unknown>>("/php/enable-redis", {});
  const row = object(data(response.data)), enabled = bool(row, "enabled"), message = text(row, "message");
  if (!enabled || !["already", "installed"].includes(message)) return unconfirmed();
  return { enabled, message, fileName: text(row, "fileName", "") };
}

// GENEL php.ini ayarları (phpMyAdmin/Adminer dahil tüm PHP araçlarının yükleme/süre limitleri).
export async function getGlobalPhpSettings() {
  const response = await apiClient.get<ApiEnvelope<unknown>>("/php/global-settings");
  return settingsRecord(data(response.data), "global");
}

export async function updateGlobalPhpSettings(payload: Omit<SitePhpSettings, "siteName" | "configured">) {
  const response = await apiClient.post<ApiEnvelope<unknown>>("/php/global-settings", payload);
  return settingsRecord(data(response.data), "global");
}
