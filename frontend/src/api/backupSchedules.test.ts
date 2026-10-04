import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { AxiosError, type InternalAxiosRequestConfig } from "axios";
import { apiClient, invalidateSessionRequests } from "./client";
import { deleteBackupSchedule, getBackupSchedule, getBackupSchedules, runBackupScheduleNow, updateBackupSchedule, type SubscriptionBackupSchedule, type SubscriptionBackupSchedulePayload } from "./backup";
import { UserFacingError } from "../utils/apiError";

const userId = "12345678-1234-5678-90ab-123456789012", otherId = "87654321-4321-8765-90ab-210987654321";
function schedule(): SubscriptionBackupSchedule {
  return { userId, userName: "Tenant", enabled: true, scheduleMode: "daily", intervalHours: 24, scheduleHour: 3, scheduleMinute: 30, scheduleDayOfWeek: 1, scheduleDayOfMonth: 1,
    scheduleSecondHour: null, scheduleSecondMinute: 0, retentionDays: 14, keepCount: 0, incremental: false, fullBackupEveryDays: 7, includeFiles: true, includeDatabases: true, includeMail: false, includeFtp: true,
    targetType: "local", remoteUrl: "", remoteUserName: "", remoteRegion: "", remotePasswordSet: false, lastRunUtc: "2026-10-04T09:00:00Z", lastStatus: "completed", lastDetails: "", nextRunUtc: "2026-10-05T00:30:00Z", planId: "", planName: "", maxBackupsPerDay: 0, throttledByQuota: false };
}
const payload = (): SubscriptionBackupSchedulePayload => ({ ...schedule(), remotePassword: "new-secret" });
const original = apiClient.defaults.adapter;
let body: unknown, calls: InternalAxiosRequestConfig[];
const routes = [
  { name: "read", run: () => getBackupSchedule(userId) },
  { name: "save", run: () => updateBackupSchedule(userId, payload()) },
  { name: "run", run: () => runBackupScheduleNow(userId) }
];
beforeEach(() => {
  invalidateSessionRequests(); calls = []; body = { success: true, data: schedule() };
  vi.stubGlobal("window", { location: { pathname: "/panel" } });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture" } }), { status: 200 })));
  apiClient.defaults.adapter = async config => { calls.push(config); return { config, status: 200, statusText: "OK", headers: {}, data: body }; };
});
afterEach(() => { apiClient.defaults.adapter = original; invalidateSessionRequests(); vi.unstubAllGlobals(); });
for (const route of routes) {
  it.each(["failed", "missing-success", "missing-data", "other-user"])(`${route.name} rejects %s instead of confirming a schedule`, async kind => {
    body = kind === "failed" ? { success: false, data: schedule() } : kind === "missing-success" ? { data: schedule() } : kind === "missing-data" ? { success: true } : { success: true, data: { ...schedule(), userId: otherId } };
    await expect(route.run()).rejects.toEqual(new UserFacingError("panel.backup.schedule.resultUnconfirmed"));
  });
  it(`${route.name} accepts matching GUID identity in the server's canonical format`, async () => {
    body = { success: true, data: { ...schedule(), userId: userId.replace(/-/g, "").toUpperCase() } };
    await expect(route.run()).resolves.toMatchObject({ userId: userId.replace(/-/g, "").toUpperCase() });
  });
}
it.each([
  ["userId", ""], ["enabled", "false"], ["includeDatabases", null], ["remotePasswordSet", undefined], ["throttledByQuota", undefined],
  ["scheduleMode", "unknown-mode"], ["intervalHours", 0], ["scheduleHour", 24], ["scheduleMinute", 60], ["scheduleSecondHour", 24], ["scheduleSecondMinute", 60],
  ["retentionDays", -1], ["keepCount", Number.NaN], ["maxBackupsPerDay", -1], ["lastDetails", {}], ["lastRunUtc", "not-a-date"], ["nextRunUtc", undefined]
] as const)("does not publish the malformed %s field as a measured form", async (field, value) => {
  body = { success: true, data: { ...schedule(), [field]: value } };
  await expect(getBackupSchedule(userId)).rejects.toEqual(new UserFacingError("panel.backup.schedule.resultUnconfirmed"));
});
it.each(["failed", "not-array", "partial-row", "duplicate-user"])("list rejects %s instead of displaying confirmed rows", async kind => {
  body = kind === "failed" ? { success: false, data: [] } : { success: true, data: kind === "not-array" ? {} : kind === "partial-row" ? [schedule(), { userId: otherId }] : [schedule(), { ...schedule(), userId: userId.replace(/-/g, "").toUpperCase() }] };
  await expect(getBackupSchedules()).rejects.toEqual(new UserFacingError("panel.backup.schedule.resultUnconfirmed"));
});
it("accepts an empty measured list and a never-run schedule with zero unlimited quotas", async () => {
  body = { success: true, data: [] }; await expect(getBackupSchedules()).resolves.toEqual([]);
  body = { success: true, data: { ...schedule(), retentionDays: 0, keepCount: 0, lastRunUtc: null, lastStatus: "", nextRunUtc: null, scheduleSecondHour: undefined, scheduleSecondMinute: undefined } };
  await expect(getBackupSchedule(userId)).resolves.toMatchObject({ retentionDays: 0, lastRunUtc: null });
});
it.each(["failed", "missing-data", "other-user"])("delete rejects %s instead of acknowledging deletion", async kind => {
  body = kind === "failed" ? { success: false, data: { userId } } : kind === "missing-data" ? { success: true } : { success: true, data: { userId: otherId } };
  await expect(deleteBackupSchedule(userId)).rejects.toEqual(new UserFacingError("panel.backup.schedule.resultUnconfirmed"));
});
it("preserves the exact save payload and acknowledges the matching deleted subscription", async () => {
  const submitted = payload(); await updateBackupSchedule(userId, submitted); expect(JSON.parse(String(calls[0]?.data))).toEqual(submitted);
  body = { success: true, data: { userId } }; await expect(deleteBackupSchedule(userId)).resolves.toBeUndefined();
  expect(calls[1]?.url).toBe(`/backup/schedules/${userId}`);
});
it("preserves the backend's actual quota/authorization error", async () => {
  apiClient.defaults.adapter = async config => { throw new AxiosError("denied", "ERR_BAD_REQUEST", config, null, { config, status: 403, statusText: "Forbidden", headers: {}, data: { success: false, message: "auth.subscription_forbidden" } }); };
  await expect(updateBackupSchedule(userId, payload())).rejects.toMatchObject({ response: { data: { message: "auth.subscription_forbidden" } } });
});
