import { apiClient } from './client';
import { UserFacingError } from '../utils/apiError';
export type SslRepairTarget = { siteName: string; hostName: string; port: number };
type Certificate = { thumbprint: string; subject: string; notAfterUtc: string; valid: boolean; validationIssues?: string[] | null };
export type SslRepairOption = { hostName: string; bindingInformation: string; current: Certificate | null; candidate: Certificate; planToken: string; blockedReason: string };
export type SslRepairPlan = { siteName: string; recordHostName: string; options: SslRepairOption[] };
type Result = { bindingApplied: boolean; tlsVerified: boolean; detail: string };
export async function getSslRepairPlan(target: SslRepairTarget): Promise<SslRepairPlan> {
  const response = await apiClient.post('/ssl/repair/plan', target, { timeout: 120_000 });
  const value = response.data?.data;
  if (response.data?.success !== true || !value || value.siteName !== target.siteName || !Array.isArray(value.options))
    throw new UserFacingError('ssl.repair.inventory_failed');
  return value;
}
export async function applySslRepair(planToken: string): Promise<Result> {
  const response = await apiClient.post('/ssl/repair/apply', { planToken }, { timeout: 120_000 });
  const value = response.data?.data;
  if (response.data?.success !== true || !value || typeof value.bindingApplied !== 'boolean' || typeof value.tlsVerified !== 'boolean')
    throw new UserFacingError('ssl.repair.readback_failed');
  return value;
}
