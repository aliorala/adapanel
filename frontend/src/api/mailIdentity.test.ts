import { beforeEach, expect, it, vi } from "vitest";
import { apiClient } from "./client";
import { getMailServerIdentity, setMailServerIdentity } from "./mailIdentity";
vi.mock("./client", () => ({ apiClient: { get: vi.fn(), post: vi.fn() } }));
beforeEach(() => { vi.clearAllMocks(); });
it("uses the global endpoint and a twelve-minute timeout for certificate preparation", async () => {
  vi.mocked(apiClient.post).mockResolvedValue({ data: { data: { applied: false } } });
  const request = { domain: "example.test", hostName: "mail.example.test", contactEmail: "admin@example.test" };
  expect(await setMailServerIdentity(request)).toEqual({ applied: false });
  expect(apiClient.post).toHaveBeenCalledWith("/mail/server-identity", request, { timeout: 720000 });
});
it("reads measured server state using GET", async () => {
  vi.mocked(apiClient.get).mockResolvedValue({ data: { data: { pinned: true } } });
  expect(await getMailServerIdentity()).toEqual({ pinned: true }); expect(apiClient.get).toHaveBeenCalledWith("/mail/server-identity");
});
