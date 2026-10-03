import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { apiClient, invalidateSessionRequests } from "./client";
import { createFtpUser } from "./ftp";

const originalAdapter = apiClient.defaults.adapter;
beforeEach(() => {
  vi.stubGlobal("window", { location: { pathname: "/entry" } });
  vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(JSON.stringify({ data: { requestToken: "fixture-token" } }))));
  invalidateSessionRequests();
});
afterEach(() => { apiClient.defaults.adapter = originalAdapter; invalidateSessionRequests(); vi.unstubAllGlobals(); });

it.each([
  [false, "ftp.user_created_password_expires"],
  [null, "ftp.user_created_expiry_unverified"],
  [true, "ftp.user_created"]
])("preserves the FTP creation envelope message and expiry measurement (%s)", async (passwordNeverExpires, message) => {
  const payload = { userName: "fixture_ftp", password: "FixturePassword123!", siteName: "fixture.test", homePath: "" };
  const adapter = vi.fn(async config => ({ config, status: 200, statusText: "OK", headers: {}, data: {
    success: true, code: 200, message,
    data: { userName: "fixture_ftp", siteName: "fixture.test", homePath: "C:/sites/fixture.test", passwordNeverExpires, existsOnSystem: true, isEnabled: true, createdAtUtc: "2026-10-02T10:00:00Z" }
  } }));
  apiClient.defaults.adapter = adapter;
  const result = await createFtpUser(payload);
  expect(result).toMatchObject({ userName: "fixture_ftp", passwordNeverExpires, message });
  expect(adapter).toHaveBeenCalledOnce();
  expect(adapter.mock.calls[0][0]).toMatchObject({ url: "/ftp/users", method: "post" });
  expect(JSON.parse(adapter.mock.calls[0][0].data)).toEqual(payload);
});
