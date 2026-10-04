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
