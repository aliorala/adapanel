import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { AxiosError, type InternalAxiosRequestConfig } from "axios";
import { apiClient, invalidateSessionRequests } from "./client";
import { deleteBackupChain, type BackupDeleteResult } from "./backup";

const originalAdapter = apiClient.defaults.adapter;
const sendDeletion = deleteBackupChain as (backupId: string, expectedBackupIds: readonly string[]) => Promise<BackupDeleteResult>;
beforeEach(() => {
  vi.stubGlobal("window", { location: { pathname: "/entry" } });
  vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(JSON.stringify({ data: { requestToken: "fixture-token" } }))));
  invalidateSessionRequests();
});
afterEach(() => { apiClient.defaults.adapter = originalAdapter; invalidateSessionRequests(); vi.unstubAllGlobals(); });
function response(config: InternalAxiosRequestConfig) {
  return { config, status: 200, statusText: "OK", headers: {}, data: { success: true, data: { deletedIds: ["branch/a", "child"], deletedNames: ["a", "child"] } } };
}
it("sends the exact confirmed set in the real DELETE request body and encodes the selected backup ID", async () => {
  const adapter = vi.fn(async (config: InternalAxiosRequestConfig) => response(config)); apiClient.defaults.adapter = adapter;
  await sendDeletion("branch/a", ["branch/a", "child"]);
  const request = adapter.mock.calls[0][0]; expect(request).toMatchObject({ method: "delete", url: "/backup/branch%2Fa/chain" });
  expect(JSON.parse(request.data)).toEqual({ expectedBackupIds: ["branch/a", "child"] });
});
it("snapshots the approved IDs before asynchronous CSRF acquisition", async () => {
  let release!: (value: Response) => void; vi.mocked(fetch).mockReturnValueOnce(new Promise<Response>(resolve => { release = resolve; }));
  const adapter = vi.fn(async (config: InternalAxiosRequestConfig) => response(config)); apiClient.defaults.adapter = adapter;
  const ids = ["branch/a", "child"], deleting = sendDeletion("branch/a", ids); ids.push("unapproved");
  await vi.waitFor(() => expect(release).toBeDefined()); release(new Response(JSON.stringify({ data: { requestToken: "fixture-token" } }))); await deleting;
  expect(JSON.parse(adapter.mock.calls[0][0].data)).toEqual({ expectedBackupIds: ["branch/a", "child"] });
});
it("preserves the server's changed-target rejection without retrying the mutation", async () => {
  const adapter = vi.fn(async (config: InternalAxiosRequestConfig) => { throw new AxiosError("Changed targets", "ERR_BAD_REQUEST", config, null,
    { config, status: 409, statusText: "Conflict", headers: {}, data: { message: "backup.chain_changed" } }); });
  apiClient.defaults.adapter = adapter;
  await expect(sendDeletion("branch/a", ["branch/a", "child"])).rejects.toMatchObject({ response: { data: { message: "backup.chain_changed" } } });
  expect(adapter).toHaveBeenCalledOnce();
});
