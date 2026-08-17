import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type ServicePlan = {
  id: string;
  name: string;
  maxSites: number;
  maxDiskMb: number;
  maxDatabases: number;
  maxMailboxes: number;
  maxScheduledTasks: number;
  maxFtpUsers: number;
  /** Aylık trafik kotası GB (0 = sınırsız). Aşılınca abonelik otomatik askıya alınır. */
  maxTrafficGbPerMonth: number;
  /** Geçerlilik süresi gün (0 = süresiz). Atandığında bitiş tarihi buradan hesaplanır. */
  validityDays: number;
  /**
   * Aboneliğin GÜNDE en fazla kaç ZAMANLANMIŞ yedek alabileceği (0 = sınırsız).
   * Elle alınan yedekler sayılmaz. Program kurulurken sıklık bu kotaya ÇEKİLİR.
   */
  maxBackupsPerDay: number;
};

export type ServicePlanPayload = {
  name: string;
  maxSites: number;
  maxDiskMb: number;
  maxDatabases?: number;
  maxMailboxes?: number;
  maxScheduledTasks?: number;
  maxFtpUsers?: number;
  maxTrafficGbPerMonth?: number;
  validityDays?: number;
  /**
   * GÖNDERİLMEZSE sunucu 0 yazar (sınırsız) — yani formun bu alanı atlaması, her plan kaydında
   * yedek kotasını sessizce SIFIRLAR. Aynı hata daha önce trafik kotasında yaşandı; bu yüzden
   * `UsersView` isteği formun kendisinden türetir ve alan `BOS_PLAN_FORMU` içinde tanımlıdır.
   */
  maxBackupsPerDay?: number;
};

export async function getServicePlans() {
  const response = await apiClient.get<ApiEnvelope<ServicePlan[]>>("/service-plans");
  return response.data.data;
}

export async function createServicePlan(payload: ServicePlanPayload) {
  const response = await apiClient.post<ApiEnvelope<ServicePlan>>("/service-plans", payload);
  return response.data.data;
}

export async function deleteServicePlan(id: string) {
  await apiClient.delete(`/service-plans/${encodeURIComponent(id)}`);
}

/**
 * Planı GÜNCELLER. Uç vardı ama arayüzde çağıran yoktu; plan düzenleme ekranı bu yüzden eksikti.
 * Plan artık aboneliklere BAĞLI olduğu için düzenlemek bağlı bütün müşterileri etkiler.
 */
export async function updateServicePlan(id: string, payload: ServicePlanPayload) {
  const response = await apiClient.put<ApiEnvelope<ServicePlan>>(`/service-plans/${encodeURIComponent(id)}`, payload);
  return response.data.data;
}
