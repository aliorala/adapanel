import { expect, it, vi } from "vitest";
import { updateSitePhpSettings } from "./php";
import { apiClient } from "./client";

vi.mock("./client", () => ({ apiClient: { get: vi.fn(), post: vi.fn() } }));

const row = (extra: object) => ({ siteName: "a.test", memoryLimit: "256M", maxExecutionTime: 120, uploadMaxFilesize: "64M",
  postMaxSize: "64M", displayErrors: false, configured: true, openBasedirEnabled: true, ...extra });
const payload = { memoryLimit: "256M", maxExecutionTime: 120, uploadMaxFilesize: "64M", postMaxSize: "64M", displayErrors: false, openBasedirEnabled: true };

it("carries the server's poolRecycleFailed flag through to the caller", async () => {
  vi.mocked(apiClient.post).mockResolvedValue({ data: { success: true, data: row({ poolRecycleFailed: true }), message: "php.pool_recycle_failed", code: 0 } });
  expect((await updateSitePhpSettings("a.test", payload)).poolRecycleFailed).toBe(true);
});

it("leaves the flag undefined when the server does not send it", async () => {
  vi.mocked(apiClient.post).mockResolvedValue({ data: { success: true, data: row({}), message: "php.settings.success", code: 0 } });
  expect((await updateSitePhpSettings("a.test", payload)).poolRecycleFailed).toBeUndefined();
});
