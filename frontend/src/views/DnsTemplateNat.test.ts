import { expect, it, vi } from "vitest";
import { createSSRApp, type Component } from "vue";
import { renderToString } from "vue/server-renderer";
import DnsTemplateView from "./DnsTemplateView.vue";
import FieldHint from "../components/FieldHint.vue";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));

it("shows both NAT addresses and exposes partial failures instead of a successful update label", async () => {
  const html = await renderToString(createSSRApp({ ...DnsTemplateView, setup: () => ({
    t: (key: string) => key, FieldHint, loading: false, saving: false, templateLoaded: true,
    notice: "panel.dnsTemplate.mappingPartial", noticeError: true,
    load: vi.fn(), addRow: vi.fn(), save: vi.fn(), records: [], types: [], secondaryServersText: "",
    ipMappings: [{ privateAddress: "192.168.1.80", publicAddress: "203.0.113.10" }], applyMappings: true,
    mappingResults: [{ siteName: "example.test", succeeded: false, changedRecords: 0, error: "native access denied" }]
  }) } as Component));
  expect(html).toContain('value="192.168.1.80"');
  expect(html).toContain('value="203.0.113.10"');
  expect(html).toContain('role="alert"');
  expect(html).toContain("panel.dnsTemplate.mappingFailed");
  expect(html).toContain("native access denied");
  expect(html).not.toContain("panel.dnsTemplate.mappingDone");
});
