import { afterEach, beforeEach, expect, it, vi } from "vitest";
import type { AxiosResponse, InternalAxiosRequestConfig } from "axios";
import { apiClient } from "./client";
import { getBackupChain, type BackupChain, type BackupChainLink } from "./backup";

const originalAdapter = apiClient.defaults.adapter;
const link = (backupId: string, chainIndex: number): BackupChainLink => ({ backupId, name: `${backupId}.zip`, chainIndex, backupMode: chainIndex ? "incremental" : "full", createdAtUtc: "2026-10-04T12:00:00Z", size: 123, available: true });
const chain = (): BackupChain => ({ chainId: "chain-a", broken: false, links: [link("base", 0), link("selected", 1)], missingLinks: [] });
let payload: unknown;
beforeEach(() => {
  vi.stubGlobal("window", { location: { pathname: "/panel" } }); payload = { success: true, data: chain() };
  apiClient.defaults.adapter = async (config: InternalAxiosRequestConfig): Promise<AxiosResponse> => ({ config, data: payload, headers: {}, status: 200, statusText: "OK" });
});
afterEach(() => { apiClient.defaults.adapter = originalAdapter; vi.unstubAllGlobals(); });
it("accepts a measured path ending at the requested backup and a case-insensitive chain ID", async () => {
  await expect(getBackupChain("selected", "CHAIN-A")).resolves.toEqual(chain());
});
it("preserves an explicitly broken graph's missing-link diagnostics", async () => {
  const broken = { ...chain(), broken: true, links: [link("selected", 2)], missingLinks: ["Missing base archive"] }; payload = { success: true, data: broken };
  await expect(getBackupChain("selected", "chain-a")).resolves.toEqual(broken);
});
it("preserves duplicate-node graph diagnostics when the server explicitly marks it broken", async () => {
  const broken = { ...chain(), broken: true, links: [link("selected", 1), link("selected", 1)], missingLinks: ["Duplicate archive ID"] }; payload = { success: true, data: broken };
  await expect(getBackupChain("selected", "chain-a")).resolves.toEqual(broken);
});
it("supports a measured standalone legacy archive with no chain ID", async () => {
  const standalone = { chainId: "", links: [link("selected", 0)], broken: false, missingLinks: [] }; payload = { success: true, data: standalone };
  await expect(getBackupChain("selected", "")).resolves.toEqual(standalone);
});
it.each([
  "false-envelope", "missing-result", "missing-broken", "wrong-chain", "empty-links", "wrong-target", "duplicate-id", "noncontiguous", "wrong-mode", "unavailable", "contradictory-missing", "missing-names", "null-link", "bad-link-name", "bad-date", "bad-size", "bad-availability"
])("rejects an unmeasured or contradictory chain: %s", async kind => {
  const value: Record<string, unknown> = chain();
  if (kind === "missing-broken") delete value.broken;
  if (kind === "wrong-chain") value.chainId = "other-chain";
  if (kind === "empty-links") value.links = [];
  if (kind === "wrong-target") value.links = [link("base", 0), link("other", 1)];
  if (kind === "duplicate-id") value.links = [link("selected", 0), link("selected", 1)];
  if (kind === "noncontiguous") value.links = [link("base", 0), link("selected", 2)];
  if (kind === "wrong-mode") value.links = [{ ...link("base", 0), backupMode: "incremental" }, link("selected", 1)];
  if (kind === "unavailable") value.links = [link("base", 0), { ...link("selected", 1), available: false }];
  if (kind === "contradictory-missing") value.missingLinks = ["base"];
  if (kind === "missing-names") value.missingLinks = undefined;
  if (kind === "null-link") value.links = [null];
  if (kind === "bad-link-name") value.links = [{ ...link("selected", 0), name: null }];
  if (kind === "bad-date") value.links = [{ ...link("selected", 0), createdAtUtc: "unknown" }];
  if (kind === "bad-size") value.links = [{ ...link("selected", 0), size: -1 }];
  if (kind === "bad-availability") value.links = [{ ...link("selected", 0), available: "true" }];
  payload = { success: kind !== "false-envelope", data: kind === "missing-result" ? null : value };
  await expect(getBackupChain("selected", "chain-a")).rejects.toThrow("panel.backup.resultUnconfirmed");
});
