import { expect, it, vi } from "vitest";
import { createSSRApp, type Component } from "vue";
import { renderToString } from "vue/server-renderer";
import ServerHealthView from "./ServerHealthView.vue";
import SectionLoadError from "../components/SectionLoadError.vue";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../router", () => ({ default: {} }));

async function render(serviceStatus: string, failed: boolean) {
  return renderToString(createSSRApp({ ...ServerHealthView, setup: () => ({
    t: (key: string) => key, loading: false, notice: "", noticeError: false,
    actionBusy: false, canReload: true, canRepairDns: true, canRefreshDomains: true, canRestart: () => true,
    servicesLoad: { message: { value: "" } }, servicesConfirmed: true, dnsConfirmed: serviceStatus !== "Unknown", domainsConfirmed: true, hasUnresolvedFindings: false,
    load: vi.fn(), SectionLoadError, dnsLoad: { message: { value: "" } },
    dns: { serviceStatus, installed: serviceStatus !== "Unknown", publiclyBound: true,
      needsRepair: false, listeningAddresses: [], expectedAddresses: [], missingAddresses: [] },
    dnsBusy: false, dnsDetailsIsDiagnostic: false, startModeText: () => "Auto", pillClass: () => "ok", statusText: () => "panel.health.statusRunning",
    domains: { totalSites: 1, updatedSites: 0, failedSites: failed ? 1 : 0, results: [], serverFindings: [] },
    domainsLoading: false, domainsRepaired: false, domainCheckFailed: failed, onarilacakVar: false, domainsBusy: false,
    refreshAllDomains: vi.fn(), domainsLoad: { message: { value: "" } }, domainReport: [],
    services: [], loaded: true
  }) } as Component));
}

it("shows an unreadable DNS measurement as a retryable error rather than absent DNS", async () => {
  const html = await render("Unknown", false);
  expect(html).toContain("dns.health_unmeasurable");
  expect(html).toContain("panel.common.retry");
  expect(html).not.toContain("panel.health.dnsNotInstalled");
});

it("does not label failed domain inspections as current", async () => {
  const html = await render("Running", true);
  expect(html).toContain("panel.health.statusUnknown");
  expect(html).not.toContain("panel.health.domainsCurrent");
});

it("still displays successful DNS and domain measurements", async () => {
  const html = await render("Running", false);
  expect(html).toContain("panel.health.dnsBound");
  expect(html).toContain("panel.health.domainsCurrent");
  expect(html).not.toContain("panel.health.statusUnknown");
});
