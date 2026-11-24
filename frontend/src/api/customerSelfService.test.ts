import { expect, it, vi } from "vitest";
import { rotateWebhookSecret } from "./notifications";
import { getResellerQuota, setResellerQuota } from "./subscriptions";
import { setUserPassword } from "./users";
import { apiClient } from "./client";

vi.mock("./client", () => ({ apiClient: { get: vi.fn(), put: vi.fn(), post: vi.fn(), delete: vi.fn() } }));

it("sets another user's password through the credential endpoint and reports the revoked API keys", async () => {
  vi.mocked(apiClient.post).mockResolvedValue({ data: { success: true, data: { revokedApiTokens: 2 } } });
  const revoked = await setUserPassword("u 1", "a-very-long-password");
  expect(revoked).toBe(2);
  expect(apiClient.post).toHaveBeenLastCalledWith("/auth/password/set/u%201", { newPassword: "a-very-long-password" });
});

it("reads and writes the reseller ceiling on the administrator-only route", async () => {
  const usage = { resellerId: "r", maxSites: 5, maxDiskMb: 1000, allocatedSites: 1, allocatedDiskMb: 10, usedSites: 1, customerCount: 1, hasUnlimitedCustomer: false };
  vi.mocked(apiClient.get).mockResolvedValue({ data: { data: usage } });
  vi.mocked(apiClient.put).mockResolvedValue({ data: { data: usage } });
  expect(await getResellerQuota("r1")).toEqual(usage);
  expect(apiClient.get).toHaveBeenLastCalledWith("/subscriptions/resellers/r1/quota");
  await setResellerQuota("r1", { maxSites: 5, maxDiskMb: 1000 });
  expect(apiClient.put).toHaveBeenLastCalledWith("/subscriptions/resellers/r1/quota", { maxSites: 5, maxDiskMb: 1000 });
});

it("rotates the webhook secret and refuses an unconfirmed answer instead of showing an empty secret", async () => {
  vi.mocked(apiClient.post).mockResolvedValue({ data: { success: true, data: { webhookSecret: "abc123", masked: "••••c123" } } });
  expect(await rotateWebhookSecret()).toEqual({ webhookSecret: "abc123", masked: "••••c123" });
  expect(apiClient.post).toHaveBeenLastCalledWith("/notify/webhook-secret/rotate", {});
  vi.mocked(apiClient.post).mockResolvedValue({ data: { success: true, data: { webhookSecret: "", masked: "" } } });
  await expect(rotateWebhookSecret()).rejects.toThrow();
});
