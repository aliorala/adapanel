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
