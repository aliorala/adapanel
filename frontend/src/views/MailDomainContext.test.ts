import { afterEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { createRenderer, h, nextTick, ref, ssrContextKey, type App, type Component } from "vue";
import MailView from "./MailView.vue";
import { createMailDomain, getMailWorkspace } from "../api/mail";
import { useSitesStore } from "../stores/sites";
import { siteWebDomain } from "../utils/siteDomain";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key, locale: ref("en") }) }));
vi.mock("../api/mail", async original => ({
  ...await original<typeof import("../api/mail")>(), createMailDomain: vi.fn(),
  getMailWorkspace: vi.fn(), getMailPublicBaseUrl: vi.fn(async () => ""),
  getMailClientSettings: vi.fn(async () => null),
  getOutboundLimit: vi.fn(async () => ({ domain: "adapanel.host", messagesPerHour: 0, configured: false })),
}));
const site = { id: 1, name: "web-adapanel.host", state: "Started", autoStart: true,
  physicalPath: "", applicationRootPath: "", applicationPoolName: "web-adapanel.host",
  bindings: ["mail.adapanel.host", "adapanel.host", "www.adapanel.host"].map(hostName =>
    ({ protocol: "https", hostName, port: 443, ipAddress: "*", bindingInformation: "" })) };
let app: App | undefined;
afterEach(() => { app?.unmount(); app = undefined; vi.clearAllMocks(); });

it("selects the real bound domain instead of the technical IIS name or service alias", () => {
  expect(siteWebDomain(site)).toBe("adapanel.host");
  expect(siteWebDomain({ ...site, name: "technical", bindings: [] })).toBe("");
  expect(siteWebDomain({ ...site, name: "technical", bindings: [
    { ...site.bindings[0]!, hostName: "203.0.113.1.nip.io" },
    { ...site.bindings[0]!, hostName: "203.0.113.1" },
  ] })).toBe("");
});

it.each([false, true])("opening the site mail tab never creates mail and selects only its existing domain (exists: %s)", async exists => {
  vi.mocked(getMailWorkspace).mockImplementation(async domain => ({
    installed: true, configured: true, selectedDomain: domain ?? "web-adapanel.host",
    domains: [{ name: "web-adapanel.host", accountCount: 2, maxSizeMB: 0, active: true, allocatedMB: 0 },
      ...(exists ? [{ name: "adapanel.host", accountCount: 0, maxSizeMB: 0, active: true, allocatedMB: 0 }] : [])],
    accounts: [], aliases: [], lists: [],
  }));
  const pinia = createPinia();
  useSitesStore(pinia).sites = [site];
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}),
    insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...MailView, render: () => h("section") } as Component, { domain: siteWebDomain(site) }).use(pinia);
  app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: { selectedDomain: string; notice: string } }).setupState;
  for (let i = 0; i < 30; i++) await nextTick();
  expect(createMailDomain).not.toHaveBeenCalled();
  expect(state.selectedDomain).toBe(exists ? "adapanel.host" : "");
  if (!exists) expect(state.notice).toContain("adapanel.host");
});
