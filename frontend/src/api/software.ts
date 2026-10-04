import { apiClient } from "./client";
import type { ServerRunningJobs } from "../composables/useRunningTask";
import { UserFacingError } from "../utils/apiError";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type ComponentRemovalResult = { componentId: string; warnings: string[] };

export type ComponentManifestItem = {
  id: string;
  displayName: string;
  version: string;
  type: string;
  downloadUrl: string;
  sha256: string;
  installDir: string;
  postInstall: string[];
  dependencies: string[];
  serviceName: string;
  silentArgs: string;
  note: string;
  default: boolean;
};

export type InstalledComponent = {
  id: string;
  componentId: string;
  displayName: string;
  version: string;
  type: string;
  installDir: string;
  serviceName: string;
  status: string;
  lastError: string;
  generatedPassword: string;
  isSystemDetected: boolean;
  detectionSource: string;
  availableVersion: string;
  updateAvailable: boolean;
  versionCheckSupported: boolean;
  versionCheckFailed: boolean;
  installedAtUtc: string;
};

export type SoftwareCatalog = {
  manifest: ComponentManifestItem[];
  defaults: ComponentManifestItem[];
  installed: InstalledComponent[];
};

function invalidCatalog(): never { throw new UserFacingError("panel.software.resultUnconfirmed"); }
function catalogObject(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value)) return invalidCatalog();
  return value as Record<string, unknown>;
}
function catalogText(row: Record<string, unknown>, key: string, emptyDefault = false): string {
  const value = row[key];
  // The manifest producer permits null optional text; keep it usable as unknown,
  // rather than treating missing versions, notes or service names as evidence.
  if (emptyDefault && value == null) return "";
  return typeof value === "string" ? value : invalidCatalog();
}
function catalogIdentity(row: Record<string, unknown>, key: string): string {
  const value = catalogText(row, key);
  return value.trim() ? value : invalidCatalog();
}
function catalogBoolean(row: Record<string, unknown>, key: string): boolean {
  return typeof row[key] === "boolean" ? row[key] as boolean : invalidCatalog();
}
function catalogStrings(value: unknown): string[] {
  if (!Array.isArray(value) || value.some(item => typeof item !== "string")) return invalidCatalog();
  return [...value];
}
function catalogManifest(value: unknown): ComponentManifestItem {
  const row = catalogObject(value);
  return {
    ...row,
    id: catalogIdentity(row, "id"), displayName: catalogText(row, "displayName"),
    version: catalogText(row, "version", true), type: catalogText(row, "type"),
    downloadUrl: catalogText(row, "downloadUrl", true), sha256: catalogText(row, "sha256", true),
    installDir: catalogText(row, "installDir"), postInstall: catalogStrings(row.postInstall ?? []),
    dependencies: catalogStrings(row.dependencies ?? []), serviceName: catalogText(row, "serviceName", true),
    silentArgs: catalogText(row, "silentArgs", true), note: catalogText(row, "note", true),
    default: row.default === undefined ? false : catalogBoolean(row, "default")
  };
}
function catalogInstalled(value: unknown): InstalledComponent {
  const row = catalogObject(value), status = catalogText(row, "status");
  if (!["pending", "installed", "failed"].includes(status.toLowerCase())) return invalidCatalog();
  return {
    ...row,
    id: catalogIdentity(row, "id"), componentId: catalogIdentity(row, "componentId"),
    displayName: catalogText(row, "displayName"), status,
    version: catalogText(row, "version", true), type: catalogText(row, "type", true),
    installDir: catalogText(row, "installDir", true), serviceName: catalogText(row, "serviceName", true),
    lastError: catalogText(row, "lastError", true), generatedPassword: catalogText(row, "generatedPassword", true),
    detectionSource: catalogText(row, "detectionSource", true), availableVersion: catalogText(row, "availableVersion", true),
    installedAtUtc: catalogText(row, "installedAtUtc", true),
    isSystemDetected: catalogBoolean(row, "isSystemDetected"), updateAvailable: catalogBoolean(row, "updateAvailable"),
    versionCheckSupported: catalogBoolean(row, "versionCheckSupported"), versionCheckFailed: catalogBoolean(row, "versionCheckFailed")
  };
}

/** Both catalog endpoints publish the same DTO; validate before replacing a measured snapshot. */
export function measuredSoftwareCatalog(value: unknown): SoftwareCatalog {
  const envelope = catalogObject(value), data = catalogObject(envelope.data);
  if (envelope.success !== true || !Array.isArray(data.manifest) || !Array.isArray(data.defaults) || !Array.isArray(data.installed)) return invalidCatalog();
  const manifest = data.manifest.map(catalogManifest), defaults = data.defaults.map(catalogManifest), installed = data.installed.map(catalogInstalled);
  const unique = (ids: string[]) => new Set(ids.map(id => id.toLowerCase())).size === ids.length;
  if (!unique(manifest.map(item => item.id)) || !unique(defaults.map(item => item.id)) ||
    !unique(installed.map(item => item.componentId)) || !unique(installed.map(item => item.id))) return invalidCatalog();
  return { manifest, defaults, installed };
}

export type ComponentInstallResult = {
  componentId: string;
  displayName: string;
  version: string;
  versionMeasured?: boolean;
  status: string;
  installDir: string;
  serviceName: string;
  generatedPassword: string;
  message: string;
};

function installResult(value: unknown): ComponentInstallResult {
  const result = value as Partial<ComponentInstallResult> | null;
  const fields = ["componentId", "displayName", "version", "status", "installDir", "serviceName", "generatedPassword", "message"] as const;
  if (!result || fields.some(field => typeof result[field] !== "string") || !result.componentId!.trim() ||
    !result.displayName!.trim() || !["installed", "failed", "pending", "skipped"].includes(result.status!.toLowerCase()) ||
    (result.versionMeasured !== undefined && typeof result.versionMeasured !== "boolean")) {
    throw new UserFacingError("panel.software.resultUnconfirmed");
  }
  return {
    componentId: result.componentId!, displayName: result.displayName!, version: result.version!, status: result.status!,
    installDir: result.installDir!, serviceName: result.serviceName!, generatedPassword: result.generatedPassword!, message: result.message!,
    ...(result.versionMeasured === undefined ? {} : { versionMeasured: result.versionMeasured })
  };
}

export async function installSoftwareComponent(componentId: string, force = false): Promise<ComponentInstallResult> {
  const response = await apiClient.post<ApiEnvelope<unknown>>("/software/install", { componentId, force });
  if (response.data?.success !== true) throw new UserFacingError("panel.software.resultUnconfirmed");
  const result = installResult(response.data.data);
  if (result.componentId !== componentId) throw new UserFacingError("panel.software.resultUnconfirmed");
  return result;
}

export async function installSoftwareDefaults(expectedIds?: readonly string[]): Promise<ComponentInstallResult[]> {
  const response = await apiClient.post<ApiEnvelope<unknown>>("/software/install-defaults", {});
  if (response.data?.success !== true || !Array.isArray(response.data.data) || !response.data.data.length) {
    throw new UserFacingError("panel.software.resultUnconfirmed");
  }
  const results = response.data.data.map(installResult);
  const ids = new Set(results.map(result => result.componentId));
  if (ids.size !== results.length || (expectedIds && (expectedIds.length !== results.length || expectedIds.some(id => !ids.has(id))))) {
    throw new UserFacingError("panel.software.resultUnconfirmed");
  }
  return results;
}

export async function removeSoftwareComponent(componentId: string): Promise<ComponentRemovalResult> {
  const response = await apiClient.delete<ApiEnvelope<unknown>>(`/software/${encodeURIComponent(componentId)}`);
  const envelope = response.data;
  const result = envelope?.data as Partial<ComponentRemovalResult> | null;
  if (envelope?.success !== true || !result || result.componentId !== componentId ||
    (result.warnings !== undefined && (!Array.isArray(result.warnings) ||
      result.warnings.some(warning => typeof warning !== "string" || !warning.trim())))) {
    throw new UserFacingError("panel.software.resultUnconfirmed");
  }
  return { componentId, warnings: [...(result.warnings ?? [])] };
}

/**
 * Bu sunucuda ŞU AN süren bileşen kurulumu + yarıda kalmış olanı.
 *
 * NEDEN `operations/progress` YETMİYOR (o uç zaten var ve `stores/software` onu okuyor):
 * o uç `IOperationProgressService`'i okur ve o servis yalnız BELLEKTEDİR. Panel, PostgreSQL'in
 * 365 MB'lık indirmesi sürerken yeniden başlarsa bütün adımlar yok olur ve ekran, kurulumun
 * yarıda kaldığını değil HİÇ BAŞLAMADIĞINI gösterir. Bu uç kaydı DİSKTEN okur ve sahibini
 * ölçer, yani "kesildi" durumunu da söyleyebilir.
 *
 * İKİSİ BİRBİRİNİN YERİNİ ALMAZ: `operations/progress` "hangi adımda" der (gerçek, ölçülmüş
 * adım adları — uydurma yüzde değil), bu uç "sürüyor mu ve ne zamandır" der.
 */
export async function getSoftwareRunningJobs() {
  const response = await apiClient.get<ApiEnvelope<ServerRunningJobs>>("/software/running");
  return response.data.data;
}
