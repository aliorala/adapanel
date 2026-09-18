import { expect, it, vi } from "vitest";
import { createSSRApp, type Component } from "vue";
import { renderToString } from "vue/server-renderer";
import SetupWizardView from "./SetupWizardView.vue";
import SectionLoadError from "../components/SectionLoadError.vue";
import PasswordField from "../components/PasswordField.vue";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../i18n", () => ({ setPanelLocale: vi.fn() }));
vi.mock("../router", () => ({ default: {} }));

async function render(bootstrapState: string) {
  return renderToString(createSSRApp({ ...SetupWizardView, setup: () => ({
    bootstrapState, mode: "setup", banner: bootstrapState === "failed" ? "Network unavailable" : "",
    bannerError: true, t: (key: string) => key, initializeView: vi.fn(),
    brandLogo: "", brandName: "", heading: "Setup", subtitle: "", installing: false,
    setupForm: { password: "", confirmPassword: "" }, setupStore: { loading: false }, submitSetup: vi.fn(),
    SectionLoadError, PasswordField
  }) } as Component));
}

it("does not offer first-time installation while server status is still unknown", async () => {
  const html = await render("loading");
  expect(html).toContain('role="status"');
  expect(html).toContain("panel.common.loading");
  expect(html).not.toContain("<form");
  expect(html).not.toContain("panel.setup.submit");
});

it("offers a retry after startup failure without offering installation", async () => {
  const html = await render("failed");
  expect(html).toContain("Network unavailable");
  expect(html).toContain("panel.common.retry");
  expect(html).not.toContain("<form");
});

it("still offers installation once the server confirms a new installation", async () => {
  const html = await render("ready");
  expect(html).toContain("<form");
  expect(html).toContain("panel.setup.submit");
});
