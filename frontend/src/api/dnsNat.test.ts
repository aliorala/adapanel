import { beforeEach, expect, it, vi } from "vitest";
const { get, put } = vi.hoisted(() => ({ get: vi.fn(), put: vi.fn() }));
vi.mock("./client", () => ({ apiClient: { get, put } }));
import { getDnsTemplate, updateDnsTemplate } from "./dns";

beforeEach(() => vi.clearAllMocks());
const mappings = [{ privateAddress: "192.168.1.80", publicAddress: "203.0.113.10" }];
const template = { records: [{ type: "A", host: "@", value: "{ip}", ttl: 3600 }], secondaryDnsServers: [], ipMappings: mappings, mappingResults: [] };

it("sends the private/public mapping and explicit apply-to-existing choice", async () => {
  put.mockResolvedValue({ data: { success: true, data: template } });
  await updateDnsTemplate(template.records, [], mappings, true);
  expect(put).toHaveBeenCalledWith("/dns/template", { records: template.records, secondaryDnsServers: [], ipMappings: mappings, applyIpMappingsToExistingZones: true });
});

it("returns partial native failure results so the view can report saved settings without claiming DNS success", async () => {
  const partial = { ...template, mappingResults: [{ siteName: "example.test", succeeded: false, changedRecords: 0, error: "dns.sync_failed" }] };
  put.mockResolvedValue({ data: { success: false, data: partial } });
  expect((await updateDnsTemplate(template.records, [], mappings, true)).mappingResults?.[0].succeeded).toBe(false);
});

it("rejects a failed envelope which has no explicit partial result", async () => {
  get.mockResolvedValue({ data: { success: false, data: template } });
  await expect(getDnsTemplate()).rejects.toThrow();
});

it("rejects malformed mappings rather than displaying an empty successful configuration", async () => {
  get.mockResolvedValue({ data: { success: true, data: { ...template, ipMappings: [{ privateAddress: 80, publicAddress: null }] } } });
  await expect(getDnsTemplate()).rejects.toThrow();
});
