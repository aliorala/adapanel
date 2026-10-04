import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import DnsView from "./DnsView.vue";
import { deleteDnsRecord, getDnsZone, resetDnsDefaults, setDnssec, setDnsZoneMode, upsertDnsRecord, type DnsZone } from "../api/dns";
import { useSitesStore } from "../stores/sites";
import { useAuthStore } from "../stores/auth";

const confirm = vi.hoisted(() => vi.fn());
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm }) }));
vi.mock("../api/dns", async original => ({ ...await original<object>(), getDnsZone: vi.fn(), deleteDnsRecord: vi.fn(), resetDnsDefaults: vi.fn(), setDnssec: vi.fn(), setDnsZoneMode: vi.fn(), upsertDnsRecord: vi.fn() }));
vi.mock("../api/subscriptions", async original => ({ ...await original<object>(), getMySubscription: vi.fn().mockResolvedValue(null) }));
let app: App | undefined;
beforeEach(() => {
  vi.mocked(getDnsZone).mockImplementation(async name => zone(name));
  confirm.mockImplementation(async options => { await options.action?.(); return true; });
});
afterEach(() => { app?.unmount(); vi.clearAllMocks(); });
function zone(siteName: string): DnsZone { return { siteName, zoneName: siteName, nameServers: [], records: [], zoneTransferIps: [],
  masterServers: [], effectiveNotifyServers: [], windowsZoneType: "Primary", isSecondary: false, isWindowsDnsAvailable: true,
  isWindowsZonePresent: true, source: "windows-dns", statusMessage: "dns.windows_zone_present", isDnssecSigned: false, dnssecDsRecords: [] }; }
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
type State = {
  zone: DnsZone | null; busy: boolean; editingId: string; showRecordForm: boolean;
  removeRecord(id: string): Promise<void>; restoreDefaults(): Promise<void>; switchToPrimary(): Promise<void>;
  toggleDnssec(enabled: boolean): Promise<void>; saveRecord(): Promise<void>;
};
async function mount() {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia);
  useAuthStore().session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "actor", userName: "actor", email: "actor@example.test", role: "Administrator", preferredLanguage: "en", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } };
  useSitesStore().selectedSiteName = "a.test";
  app = renderer.createApp({ ...DnsView, render: () => h("section") } as Component).use(pinia);
  app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState;
  for (let i = 0; i < 8; i++) await nextTick();
  return state;
}
async function selectSite(name: string) { useSitesStore().selectedSiteName = name; for (let i = 0; i < 8; i++) await nextTick(); }

it("does not show an old DNS zone or clear loading while the newly selected zone is pending", async () => {
  const first = deferred<DnsZone>(), second = deferred<DnsZone>();
  vi.mocked(getDnsZone).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
  const state = await mount(); await selectSite("b.test");
  first.resolve(zone("a.test")); await nextTick(); await nextTick();
  expect(state.zone).toBeNull(); expect(state.busy).toBe(true);
  second.resolve(zone("b.test")); await nextTick(); await nextTick();
  expect(state.zone?.siteName).toBe("b.test"); expect(state.busy).toBe(false);
});

it.each(["removeRecord", "restoreDefaults", "switchToPrimary", "toggleDnssec"] as const)("does not redirect confirmed %s to another site", async operation => {
  const state = await mount();
  confirm.mockImplementationOnce(async options => { await selectSite("b.test"); await options.action?.(); return true; });
  if (operation === "removeRecord") await state.removeRecord("old-record");
  else if (operation === "toggleDnssec") await state.toggleDnssec(false);
  else await state[operation]();
  expect(deleteDnsRecord).not.toHaveBeenCalled(); expect(resetDnsDefaults).not.toHaveBeenCalled();
  expect(setDnsZoneMode).not.toHaveBeenCalled(); expect(setDnssec).not.toHaveBeenCalled();
});

it("does not replace the next site's records or form when an old save finishes", async () => {
  const state = await mount(), pending = deferred<DnsZone>();
  vi.mocked(upsertDnsRecord).mockReturnValueOnce(pending.promise);
  const saving = state.saveRecord(); await selectSite("b.test");
  state.editingId = "b-record"; state.showRecordForm = true;
  pending.resolve(zone("a.test")); await saving;
  expect(state.zone?.siteName).toBe("b.test");
  expect(state.editingId).toBe("b-record"); expect(state.showRecordForm).toBe(true);
});

it.each(["removeRecord", "restoreDefaults", "switchToPrimary", "toggleDnssec"] as const)("still performs confirmed %s when the site has not changed", async operation => {
  const state = await mount();
  vi.mocked(deleteDnsRecord).mockResolvedValue(zone("a.test"));
  vi.mocked(resetDnsDefaults).mockResolvedValue(zone("a.test"));
  vi.mocked(setDnsZoneMode).mockResolvedValue(zone("a.test"));
  vi.mocked(setDnssec).mockResolvedValue(zone("a.test"));
  if (operation === "removeRecord") { await state.removeRecord("record"); expect(deleteDnsRecord).toHaveBeenCalledWith("a.test", "record"); }
  else if (operation === "toggleDnssec") { await state.toggleDnssec(false); expect(setDnssec).toHaveBeenCalledWith("a.test", false); }
  else if (operation === "restoreDefaults") { await state.restoreDefaults(); expect(resetDnsDefaults).toHaveBeenCalledWith("a.test"); }
  else { await state.switchToPrimary(); expect(setDnsZoneMode).toHaveBeenCalledWith("a.test", false, []); }
  expect(state.zone?.siteName).toBe("a.test");
});
