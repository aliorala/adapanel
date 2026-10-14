import { expect, it, vi } from "vitest";
import { createSSRApp, type Component } from "vue";
import { renderToString } from "vue/server-renderer";
import SiteHealthStrip from "./SiteHealthStrip.vue";
import FieldHint from "./FieldHint.vue";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));

it("shows the measured web domain and the separate domain used for mail policies", async () => {
  const html = await renderToString(createSSRApp({ ...SiteHealthStrip, setup: () => ({
    loading: false, health: { domain: "www.example.test", mailDomain: "example.test", durationMs: 10 },
    loadError: { message: { value: "" } }, label: (_key: string, fallback: string) => fallback,
    checkedAtText: "now", load: vi.fn(), badges: [], mailBadges: [], FieldHint
  }) } as Component, { siteName: "example.test" }));
  expect(html).toContain("www.example.test");
  expect(html).toContain("MX / SPF / DKIM / DMARC: example.test");
  expect(html).not.toContain("nip.io");
});
