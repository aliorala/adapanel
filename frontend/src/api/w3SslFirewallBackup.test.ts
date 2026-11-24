import { afterEach, beforeEach, expect, it, vi } from "vitest";
import type { InternalAxiosRequestConfig } from "axios";
import { apiClient, invalidateSessionRequests } from "./client";
import { createSslCsr, setRenewalState } from "./ssl";
import { addIpBlock, getIpBlocks, removeIpBlock } from "./firewall";
import { testRestoreBackup } from "./backup";
import { UserFacingError } from "../utils/apiError";

const original = apiClient.defaults.adapter;
let body: unknown, calls: InternalAxiosRequestConfig[];
beforeEach(() => {
  invalidateSessionRequests(); calls = []; body = { success: true, data: {} };
  vi.stubGlobal("window", { location: { pathname: "/panel" } });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture" } }), { status: 200 })));
  apiClient.defaults.adapter = async config => { calls.push(config); return { config, status: 200, statusText: "OK", headers: {}, data: body }; };
});
afterEach(() => { apiClient.defaults.adapter = original; invalidateSessionRequests(); vi.unstubAllGlobals(); });

it("sends renewal actions to the dedicated endpoint and refuses an unconfirmed response", async () => {
  body = { success: true, data: { hostName: "a.com", renewalPaused: true } };
  await setRenewalState({ siteName: "a.com", hostName: "a.com", port: 443, action: "pause" });
  expect(calls[0].url).toBe("/ssl/renewal");
  expect(JSON.parse(calls[0].data as string)).toMatchObject({ action: "pause", hostName: "a.com" });
  body = { success: false, data: null };
  await expect(setRenewalState({ siteName: "a.com", hostName: "a.com", port: 443, action: "cancel" })).rejects.toBeInstanceOf(UserFacingError);
});

it("does not treat a CSR response without the request text as created", async () => {
  body = { success: true, data: { id: "1", hostName: "a.com", csrPem: "" } };
  await expect(createSslCsr({ hostName: "a.com", altNames: [], organization: "", country: "", state: "", locality: "", keySize: 2048 })).rejects.toBeInstanceOf(UserFacingError);
});

it("reads, adds and removes IP blocks through the firewall endpoints", async () => {
  body = { success: true, data: { entries: [], countryBlockSupported: false } };
  expect((await getIpBlocks()).countryBlockSupported).toBe(false);
  body = { success: true, data: { id: "x", target: "1.2.3.4/32", ruleState: "applied" } };
  await addIpBlock({ target: "1.2.3.4", reason: "", durationMinutes: 60 });
  expect(JSON.parse(calls[1].data as string)).toMatchObject({ target: "1.2.3.4", durationMinutes: 60 });
  await removeIpBlock("x");
  expect(calls[2].method).toBe("delete");
  expect(calls[2].url).toBe("/firewall/ip-blocks/x");
});

it.each(["bogus", undefined])("rejects a test restore response with outcome %s instead of reporting success", async outcome => {
  body = { success: true, data: { outcome } };
  await expect(testRestoreBackup("b1")).rejects.toBeInstanceOf(UserFacingError);
});

it("returns a measured test restore result", async () => {
  body = { success: true, data: { outcome: "failed", issues: ["entry=a; reason=crc_mismatch"] } };
  expect((await testRestoreBackup("b1")).outcome).toBe("failed");
  expect(calls[0].url).toBe("/backup/b1/test-restore");
});
