import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import SslView from "./SslView.vue";
import { getSslWorkspace, issueLetsEncrypt, type SslCertificate } from "../api/ssl";
import { addSiteAlias } from "../api/aliases";
import { getSslRepairPlan } from "../api/sslRepair";
import { useSitesStore } from "../stores/sites";
import { useAuthStore } from "../stores/auth";
import { companionWwwHost } from "../utils/wwwSsl";
import { useSubscriptionStore } from "../stores/subscription";
import type { SubscriptionUsage } from "../api/subscriptions";
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../api/ssl", async original => ({ ...await original<object>(), getSslWorkspace: vi.fn(), issueLetsEncrypt: vi.fn() }));
vi.mock("../api/aliases", () => ({ addSiteAlias: vi.fn() }));
vi.mock("../api/sslRepair", () => ({ getSslRepairPlan: vi.fn() }));
type State = { includeWww: boolean; wwwCompanion: string; issueForm: { hostName: string; contactEmail: string; wildcard: boolean }; notice: string; noticeError: boolean; retryWwwHost: string; issue(): Promise<void>; openWwwRetry(): Promise<void> };
const root = "example.com", www = `www.${root}`;
const binding = (hostName = root) => ({ protocol: "http", hostName, port: 80, ipAddress: "*", bindingInformation: `*:80:${hostName}` });
const certificate = (hostName: string): SslCertificate => ({ siteName: root, hostName, port: 443, thumbprint: hostName, friendlyName: hostName, subject: `CN=${hostName}`, notAfterUtc: "2027-01-01T00:00:00Z", source: "letsencrypt", isHttpsBindingPresent: true, isCertificateBound: true, isPresentInStore: true, bindingDetail: "", fallbackReason: "" });
let app: App | undefined, hostNames: string[], certificates: SslCertificate[], events: string[];
async function flush() { for (let i = 0; i < 12; i++) await nextTick(); }
async function mount(role = "Administrator") {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia); const sites = useSitesStore();
  sites.selectedSiteName = root; sites.sites = [{ id: 1, name: root, state: "Started", physicalPath: "C:/site", applicationRootPath: "C:/site", applicationPoolName: root, autoStart: true, bindings: [binding()] }];
  const auth = useAuthStore(); auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "actor", userName: "actor", email: "owner@example.com", role, preferredLanguage: "en", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } };
  app = renderer.createApp({ ...SslView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState;
  await flush(); state.issueForm.contactEmail = "owner@example.com"; return { state, sites, auth };
}
beforeEach(() => {
  hostNames = [root]; certificates = []; events = [];
  vi.mocked(getSslWorkspace).mockImplementation(async siteName => ({ siteName, hostNames: [...hostNames], certificates: [...certificates], httpsRedirectEnabled: false, httpsRedirectMeasured: true }));
  vi.mocked(issueLetsEncrypt).mockImplementation(async payload => {
    events.push(`issue:${payload.hostName}`); const result = certificate(payload.hostName); certificates = [...certificates.filter(item => item.hostName !== payload.hostName), result]; return result;
  });
  vi.mocked(addSiteAlias).mockImplementation(async (siteName, payload) => {
    events.push(`alias:${payload.hostName}`); hostNames.push(payload.hostName);
    return { siteName, primaryHostName: root, aliases: [{ hostName: payload.hostName, hasHttps: false, includesWww: false, redirectToPrimary: true }] };
  });
  vi.mocked(getSslRepairPlan).mockResolvedValue({ siteName: root, recordHostName: www, options: [] });
});
afterEach(() => { app?.unmount(); app = undefined; vi.restoreAllMocks(); vi.clearAllMocks(); });
it("prepares www after verified primary SSL, then verifies both certificates", async () => {
  const { state } = await mount(); expect(state.includeWww).toBe(true); expect(state.wwwCompanion).toBe(www);
  await state.issue(); expect(events).toEqual([`issue:${root}`, `alias:${www}`, `issue:${www}`]);
  expect(addSiteAlias).toHaveBeenCalledWith(root, { hostName: www, redirectToPrimary: true, includeWww: false });
  expect(vi.mocked(issueLetsEncrypt).mock.calls[1]![0]).toMatchObject({ hostName: www, contactEmail: "owner@example.com", wildcard: false });
  expect(state.noticeError).toBe(false); expect(state.notice).toBe("panel.ssl.mainAndWwwIssued");
  expect(certificates.map(item => item.hostName)).toEqual([root, www]);
});
it("does not re-add existing www bindings or alter their redirect behavior", async () => {
  hostNames.push(www); const { state } = await mount(); await state.issue();
  expect(addSiteAlias).not.toHaveBeenCalled(); expect(events).toEqual([`issue:${root}`, `issue:${www}`]);
});
it("preserves a www certificate only after independent current-certificate validation", async () => {
  hostNames.push(www); certificates.push(certificate(www));
  vi.mocked(getSslRepairPlan).mockResolvedValue({ siteName: root, recordHostName: www, options: [{ hostName: www, bindingInformation: `*:443:${www}`, current: { thumbprint: www, valid: true, subject: `CN=${www}`, notAfterUtc: "2027-01-01T00:00:00Z" }, candidate: { thumbprint: www, valid: true, subject: `CN=${www}`, notAfterUtc: "2027-01-01T00:00:00Z" }, blockedReason: "", planToken: "test" }] });
  const { state } = await mount(); await state.issue(); expect(getSslRepairPlan).toHaveBeenCalledWith({ siteName: root, hostName: www, port: 443 });
  expect(events).toEqual([`issue:${root}`]); expect(state.noticeError).toBe(false);
});
it.each(["Customer", "Reseller"])("preserves existing www SSL for %s without calling administrator-only validation or replacing it", async role => {
  hostNames.push(www); const existing = certificate(www); certificates.push(existing);
  const { state } = await mount(role); await state.issue();
  expect(getSslRepairPlan).not.toHaveBeenCalled(); expect(addSiteAlias).not.toHaveBeenCalled();
  expect(events).toEqual([`issue:${root}`]); expect(certificates.find(item => item.hostName === www)).toBe(existing);
  expect(state.notice).toContain("panel.ssl.issued"); expect(state.notice).toContain("panel.ssl.wwwPreservedNeedsAdmin");
  expect(state.notice).not.toContain("panel.ssl.mainAndWwwIssued"); expect(state.noticeError).toBe(true); expect(state.retryWwwHost).toBe("");
});
it.each(["Customer", "Reseller"])("continues automatic www creation for %s when no www certificate exists", async role => {
  const { state } = await mount(role); await state.issue();
  expect(getSslRepairPlan).not.toHaveBeenCalled(); expect(events).toEqual([`issue:${root}`, `alias:${www}`, `issue:${www}`]);
  expect(state.noticeError).toBe(false); expect(state.notice).toBe("panel.ssl.mainAndWwwIssued");
});
it.each(["Customer", "Reseller"])("also preserves an existing self-signed www certificate for %s without claiming trust", async role => {
  hostNames.push(www); const existing = { ...certificate(www), source: "self-signed", fallbackReason: "ssl.acme_failed" }; certificates.push(existing);
  const { state } = await mount(role); await state.issue();
  expect(getSslRepairPlan).not.toHaveBeenCalled(); expect(events).toEqual([`issue:${root}`]);
  expect(certificates.find(item => item.hostName === www)).toBe(existing);
  expect(state.notice).toContain("panel.ssl.wwwPreservedNeedsAdmin"); expect(state.noticeError).toBe(true); expect(state.retryWwwHost).toBe("");
});
it("reports partial failure and retries only www without replacing the working root certificate", async () => {
  vi.mocked(issueLetsEncrypt).mockImplementation(async payload => {
    events.push(`issue:${payload.hostName}`); if (payload.hostName === www) throw new Error("DNS validation failed");
    const result = certificate(root); certificates.push(result); return result;
  });
  const { state } = await mount(); await state.issue(); expect(state.noticeError).toBe(true);
  expect(state.notice).toContain("panel.ssl.wwwFailed"); expect(state.notice).toContain("DNS validation failed");
  expect(certificates).toHaveLength(1); expect(certificates[0]!.hostName).toBe(root);
  await state.openWwwRetry(); expect(state.issueForm.hostName).toBe(www); expect(state.wwwCompanion).toBe("");
  expect(events.filter(value => value === `issue:${root}`)).toHaveLength(1);
});
it("can prepare a missing www binding from the retry action without reissuing root SSL", async () => {
  vi.mocked(addSiteAlias).mockRejectedValueOnce(new Error("binding failed"));
  const { state } = await mount(); await state.issue(); expect(state.noticeError).toBe(true); expect(state.retryWwwHost).toBe(www);
  await state.openWwwRetry(); expect(state.issueForm.hostName).toBe(www);
  expect(events).toEqual([`issue:${root}`, `alias:${www}`]); expect(issueLetsEncrypt).toHaveBeenCalledTimes(1);
});
it("stops before www when primary certificate installation is unverified", async () => {
  vi.mocked(issueLetsEncrypt).mockResolvedValue({ ...certificate(root), isCertificateBound: false });
  const { state } = await mount(); await state.issue(); expect(addSiteAlias).not.toHaveBeenCalled(); expect(issueLetsEncrypt).toHaveBeenCalledTimes(1); expect(state.noticeError).toBe(true);
});
it("keeps www self-signed fallback visibly failed while preserving primary SSL", async () => {
  vi.mocked(issueLetsEncrypt).mockImplementation(async payload => {
    const result = { ...certificate(payload.hostName), ...(payload.hostName === www ? { source: "self-signed", fallbackReason: "ssl.acme_failed" } : {}) };
    certificates.push(result); return result;
  });
  const { state } = await mount(); await state.issue(); expect(state.noticeError).toBe(true);
  expect(state.notice).toContain("panel.ssl.wwwFailed"); expect(state.notice).toContain("panel.ssl.fallbackTitle");
  expect(state.retryWwwHost).toBe(www); expect(certificates.find(item => item.hostName === root)?.source).toBe("letsencrypt");
});
it("does not report both hosts as ready when www's refreshed binding is unverified", async () => {
  vi.mocked(issueLetsEncrypt).mockImplementation(async payload => {
    const result = certificate(payload.hostName);
    certificates.push(payload.hostName === www ? { ...result, isCertificateBound: null } : result); return result;
  });
  const { state } = await mount(); await state.issue(); expect(state.noticeError).toBe(true);
  expect(state.notice).toContain("panel.ssl.wwwFailed"); expect(state.notice).not.toBe("panel.ssl.mainAndWwwIssued");
});
it("honors disabling the visible www option", async () => {
  const { state } = await mount(); state.includeWww = false; await state.issue(); expect(events).toEqual([`issue:${root}`]); expect(state.notice).toBe("panel.ssl.issued");
});
it("prevents duplicate issuance while primary ACME is pending and stops after site changes", async () => {
  let finish!: (value: SslCertificate) => void;
  vi.mocked(issueLetsEncrypt).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
  const { state, sites } = await mount(); const first = state.issue(); await state.issue(); expect(issueLetsEncrypt).toHaveBeenCalledTimes(1);
  sites.selectedSiteName = "other.test"; await flush(); finish(certificate(root)); await first;
  expect(addSiteAlias).not.toHaveBeenCalled(); expect(issueLetsEncrypt).toHaveBeenCalledTimes(1);
});
it("does not start alias writes if the subscription becomes read-only during primary issuance", async () => {
  let finish!: (value: SslCertificate) => void;
  vi.mocked(issueLetsEncrypt).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
  const { state } = await mount(); const first = state.issue();
  useSubscriptionStore().usage = { isSuspended: true } as SubscriptionUsage;
  const main = certificate(root); certificates.push(main); finish(main); await first;
  expect(addSiteAlias).not.toHaveBeenCalled(); expect(issueLetsEncrypt).toHaveBeenCalledTimes(1);
  expect(state.noticeError).toBe(true); expect(state.notice).toContain("panel.subscription.readOnlyAction");
});
it("does not continue www setup under another signed-in actor", async () => {
  let finish!: (value: SslCertificate) => void;
  vi.mocked(issueLetsEncrypt).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
  const { state, auth } = await mount(); const first = state.issue(); auth.session!.user!.id = "other-actor";
  finish(certificate(root)); await first; expect(addSiteAlias).not.toHaveBeenCalled(); expect(issueLetsEncrypt).toHaveBeenCalledTimes(1);
});
it.each(["www.example.com", "mail.example.com", "smtp.example.com", "192.168.1.1", "app.nip.io", "localhost", "*.example.com"])("does not invent www companions for %s", host => {
  expect(companionWwwHost(host, host, [binding(host)], false)).toBe("");
});
it("does not broaden alias, wildcard or unbound requests", () => {
  expect(companionWwwHost(root, "alias.com", [binding("alias.com")], false)).toBe("");
  expect(companionWwwHost(root, root, [binding()], true)).toBe("");
  expect(companionWwwHost(root, root, [], false)).toBe("");
});
