import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { AxiosError, type AxiosResponse, type InternalAxiosRequestConfig } from "axios";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import DnsView from "./DnsView.vue";
import { apiClient, invalidateSessionRequests } from "../api/client";
import type { DnsRecord, DnsZone } from "../api/dns";
import { useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
import { useSubscriptionStore } from "../stores/subscription";
import type { SubscriptionUsage } from "../api/subscriptions";
import type { ConfirmOptions } from "../components/ConfirmDialog.vue";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string, args?: unknown) => args ? `${key}:${JSON.stringify(args)}` : key }) }));
const { confirm } = vi.hoisted(() => ({ confirm: vi.fn() }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm }) }));

type State = {
  zone: DnsZone | null; busy: boolean; actionBusy: boolean; zoneConfirmed: boolean; loadError: string;
  recordForm: { type: string; host: string; value: string; ttl: number; priority: number | null };
  zoneForm: { zoneName: string }; nameServersText: string; masterServersText: string;
  editingId: string; showRecordForm: boolean; notice: string; noticeError: boolean;
  selection: { toggle(id: string): void; selected: { value: string[] } };
  load(): Promise<void>; saveRecord(): Promise<void>; saveZone(): Promise<void>; editRecord(record: DnsRecord): void;
  removeRecord(id: string): Promise<void>; removeSelectedRecords(): Promise<void>; restoreDefaults(): Promise<void>;
  switchToPrimary(): Promise<void>; switchToSecondary(): Promise<void>; toggleDnssec(enabled: boolean): Promise<void>; installWindowsDns(): Promise<void>;
};
const record = (id = "one", value = "192.0.2.1"): DnsRecord => ({ id, type: "A", host: id, value, ttl: 3600, priority: null, isManaged: false });
const zone = (siteName = "a.test"): DnsZone => ({ siteName, zoneName: siteName, nameServers: ["ns1.a.test"], zoneTransferIps: [], masterServers: [], effectiveNotifyServers: [], windowsZoneType: "Primary", records: [record(), record("two")], isSecondary: false, isWindowsDnsAvailable: true, isWindowsZonePresent: true, source: "windows-dns", statusMessage: "dns.windows_zone_present", isDnssecSigned: false, dnssecDsRecords: [] });
function deferred<T>() { let resolve!: (value: T) => void, reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
const reply = (config: InternalAxiosRequestConfig, data: unknown, success = true): AxiosResponse => ({ config, data: { success, data, message: "", code: 200 }, status: 200, statusText: "OK", headers: {} });
const error = (config: InternalAxiosRequestConfig) => new AxiosError("fixture", "ERR_BAD_RESPONSE", config, undefined, { ...reply(config, null), status: 503, data: { success: false, message: "dns.fixture_failed" } });
const siteOf = (config: InternalAxiosRequestConfig) => decodeURIComponent(config.url!.split("/")[2]!);
let app: App | undefined, calls: InternalAxiosRequestConfig[];
let readReply: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>, writeReply: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
const originalAdapter = apiClient.defaults.adapter;
async function flush() { for (let n = 0; n < 32; n++) await nextTick(); }
const writes = () => calls.filter(config => config.method !== "get");
function unmount() { app?.unmount(); app = undefined; }
async function mount(role = "Administrator") {
  const pinia = createPinia(), auth = useAuthStore(pinia), sites = useSitesStore(pinia), subscription = useSubscriptionStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "actor-a", userName: "actor", email: "actor@example.test", role, preferredLanguage: "en", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } };
  sites.selectedSiteName = "a.test";
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...DnsView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush();
  return { state, auth, sites, subscription };
}
beforeEach(() => {
  invalidateSessionRequests(); calls = []; confirm.mockReset(); confirm.mockImplementation(async (options: ConfirmOptions) => { await options.action?.(); return true; });
  vi.stubGlobal("window", { location: { pathname: "/panel", hostname: "localhost" } });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fake-only" } }), { status: 200 })));
  readReply = async config => reply(config, zone(siteOf(config)));
  writeReply = async config => {
    const next = zone(siteOf(config)), body = config.data ? JSON.parse(String(config.data)) : {};
    if (config.method === "delete") next.records = next.records.filter(row => row.id !== config.url?.split("/").slice(-1)[0]);
    else if (config.url?.endsWith("/records")) next.records = [record(body.id || "created", body.value)];
    else if (config.url?.endsWith("/zone")) Object.assign(next, body);
    else if (config.url?.endsWith("/dnssec")) next.isDnssecSigned = body.enabled;
    else if (config.url?.endsWith("/mode")) next.isSecondary = body.secondary;
    return reply(config, next);
  };
  apiClient.defaults.adapter = async config => { calls.push(config); return config.method === "get" ? readReply(config) : writeReply(config); };
});
afterEach(() => { unmount(); apiClient.defaults.adapter = originalAdapter; invalidateSessionRequests(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it("keeps the next editable record draft when the previous record save completes", async () => {
  const { state } = await mount(), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  state.editRecord(record()); state.recordForm.value = "192.0.2.2";
  writeReply = config => (request = config, pending.promise); const saving = state.saveRecord(); await vi.waitFor(() => expect(request).toBeDefined());
  state.recordForm.value = "192.0.2.3";
  pending.resolve(reply(request, { ...zone(), records: [record("one", "192.0.2.2")] })); await saving;
  expect(state.recordForm.value).toBe("192.0.2.3"); expect(state.editingId).toBe("one"); expect(state.zone?.records[0]?.value).toBe("192.0.2.2");
  expect(state.notice).toBe("panel.dns.savedWithDraft");
});
it("still clears the unchanged record form after a measured save", async () => {
  const { state } = await mount(); state.editRecord(record()); await state.saveRecord();
  expect(writes()).toHaveLength(1); expect(state.editingId).toBe(""); expect(state.showRecordForm).toBe(false); expect(state.noticeError).toBe(false);
});
it.each(["before", "during"])("preserves a zone draft changed %s a manual refresh", async timing => {
  const { state } = await mount(), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  if (timing === "before") state.nameServersText = "ns2.a.test";
  readReply = config => (request = config, pending.promise); const reading = state.load(); await vi.waitFor(() => expect(request).toBeDefined());
  if (timing === "during") state.nameServersText = "ns2.a.test";
  pending.resolve(reply(request, zone())); await reading; expect(state.nameServersText).toBe("ns2.a.test");
});
it("keeps a new zone draft while displaying the submitted save result", async () => {
  const { state } = await mount(), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  state.nameServersText = "ns2.a.test"; writeReply = config => (request = config, pending.promise);
  const saving = state.saveZone(); await vi.waitFor(() => expect(request).toBeDefined()); state.nameServersText = "ns3.a.test";
  pending.resolve(reply(request, { ...zone(), nameServers: ["ns2.a.test"] })); await saving;
  expect(state.nameServersText).toBe("ns3.a.test"); expect(state.zone?.nameServers).toEqual(["ns2.a.test"]);
  expect(state.notice).toBe("panel.dns.savedWithDraft");
});
it("does not erase unsaved zone settings when deleting an unrelated record", async () => {
  const { state } = await mount(); state.nameServersText = "ns2.a.test"; state.masterServersText = "192.0.2.53";
  await state.removeRecord("one"); expect(state.nameServersText).toBe("ns2.a.test"); expect(state.masterServersText).toBe("192.0.2.53");
});
it.each(["first", "refresh"])("requires successful retry after a failed %s zone read before another write", async kind => {
  if (kind === "first") readReply = async config => { throw error(config); };
  const { state } = await mount();
  if (kind === "refresh") { readReply = async config => { throw error(config); }; await state.load(); }
  state.recordForm.value = "192.0.2.2"; await state.saveRecord();
  expect(writes()).toHaveLength(0); expect(state.zoneConfirmed).toBe(false); expect(state.loadError).toContain("dns.fixture_failed"); expect(state.busy).toBe(false);
  readReply = async config => reply(config, zone()); await state.load(); await state.saveRecord();
  expect(writes()).toHaveLength(1); expect(state.zoneConfirmed).toBe(true); expect(state.loadError).toBe("");
});
it("reserves a single action while a delete confirmation is open", async () => {
  const { state } = await mount(), answer = deferred<boolean>(); let dialog!: ConfirmOptions;
  confirm.mockImplementationOnce(options => { dialog = options; return answer.promise.then(async yes => { if (yes) await options.action?.(); return yes; }); });
  const deleting = state.removeRecord("one"); await flush(); await state.removeRecord("two"); await state.saveRecord();
  expect(dialog).toBeDefined(); expect(confirm).toHaveBeenCalledTimes(1); expect(writes()).toHaveLength(0);
  answer.resolve(false); await deleting; await state.saveRecord(); expect(writes()).toHaveLength(1);
});
it.each(["actor", "session", "lock", "unmount"])("does not dispatch a confirmed deletion after %s changed", async change => {
  const { state, auth, subscription } = await mount();
  confirm.mockImplementationOnce(async options => {
    if (change === "actor") auth.session = { ...auth.session!, user: { ...auth.session!.user!, id: "actor-b" } };
    else if (change === "session") auth.session = { ...auth.session!, user: { ...auth.session!.user! } };
    else if (change === "lock") { subscription.usage = { isSuspended: true } as SubscriptionUsage; subscription.applyReadOnly(); }
    else unmount();
    await flush(); await options.action?.(); return true;
  });
  await state.removeRecord("one"); expect(writes()).toHaveLength(0);
});
it("does not publish an old actor's save response into the replacement session", async () => {
  const { state, auth } = await mount(), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  state.editRecord(record()); writeReply = config => (request = config, pending.promise); const saving = state.saveRecord(); await vi.waitFor(() => expect(request).toBeDefined());
  auth.session = { ...auth.session!, user: { ...auth.session!.user! } }; await flush();
  state.recordForm.value = "new-session-draft";
  pending.resolve(reply(request, { ...zone(), records: [record("old-session")] })); await saving;
  expect(state.zone?.records[0]?.id).not.toBe("old-session"); expect(state.notice).toBe(""); expect(state.recordForm.value).toBe("new-session-draft");
});
it("does not start bulk delete alongside an already pending record save", async () => {
  const { state } = await mount(), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  state.selection.toggle("one"); writeReply = config => (request = config, pending.promise);
  const saving = state.saveRecord(); await vi.waitFor(() => expect(request).toBeDefined()); const first = request;
  const deleting = state.removeSelectedRecords(); await flush();
  expect(writes()).toHaveLength(1); pending.resolve(reply(first, zone())); await Promise.all([saving, deleting]);
});
it("stops queued deletes when the actor changes after the first response", async () => {
  const { state, auth } = await mount(), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  state.selection.toggle("one"); state.selection.toggle("two"); writeReply = config => (request = config, pending.promise);
  const deleting = state.removeSelectedRecords(); await vi.waitFor(() => expect(request).toBeDefined());
  auth.session = { ...auth.session!, user: { ...auth.session!.user!, id: "actor-b" } }; await flush();
  pending.resolve(reply(request, { ...zone(), records: [record("two")] })); await deleting;
  expect(writes()).toHaveLength(1); expect(state.notice).toBe("");
});
it("preserves partial bulk failures and the records that remain after a successful response", async () => {
  const { state } = await mount(); state.selection.toggle("one"); state.selection.toggle("two");
  writeReply = async config => { if (config.url?.endsWith("/one")) throw error(config); return reply(config, { ...zone(), records: [record("one")] }); };
  await state.removeSelectedRecords(); expect(writes()).toHaveLength(2); expect(state.noticeError).toBe(true); expect(state.notice).toContain('"done":1'); expect(state.zone?.records.map(row => row.id)).toEqual(["one"]);
});
it.each(["false-envelope", "wrong-site", "malformed-records"])("does not announce a successful mutation for %s", async kind => {
  const { state } = await mount(); state.editRecord(record());
  writeReply = async config => reply(config, kind === "wrong-site" ? zone("b.test") : kind === "malformed-records" ? { ...zone(), records: [{ id: "one" }] } : zone(), kind !== "false-envelope");
  await state.saveRecord(); expect(state.noticeError).toBe(true); expect(state.notice).toContain("panel.dns.resultUnconfirmed"); expect(state.recordForm.value).toBe("192.0.2.1");
});
it("keeps reseller record editing but prevents admin-only DNS server actions", async () => {
  const { state } = await mount("Reseller"); await state.installWindowsDns(); await state.toggleDnssec(true); await state.switchToPrimary();
  expect(writes()).toHaveLength(0); state.editRecord(record()); await state.saveRecord(); expect(writes()).toHaveLength(1);
});
it("does not call the DNS endpoint for a customer session", async () => {
  const { state } = await mount("Customer"); await state.load(); await state.saveRecord(); expect(calls).toHaveLength(0);
});

it.each([false, true])("reports Windows DNS installation only when the returned measurement is %s", async installed => {
  readReply = async config => reply(config, { ...zone(), isWindowsDnsAvailable: false, isWindowsZonePresent: false, windowsZoneType: "", source: "panel-managed", statusMessage: "dns.windows_dns_unavailable" });
  const { state } = await mount();
  writeReply = async config => reply(config, { ...zone(), isWindowsDnsAvailable: installed, isWindowsZonePresent: installed });
  await state.installWindowsDns(); expect(writes()).toHaveLength(1);
  expect(state.noticeError).toBe(!installed);
  expect(state.notice).toBe(installed ? "panel.dns.windowsInstalled" : "panel.dns.resultUnconfirmed");
});
it.each([false, true])("reports DNSSEC signing only when the returned measurement is %s", async signed => {
  const { state } = await mount(); writeReply = async config => reply(config, { ...zone(), isDnssecSigned: signed, dnssecDsRecords: signed ? ["1234 13 2 AABB"] : [] });
  await state.toggleDnssec(true); expect(writes()).toHaveLength(1);
  expect(state.noticeError).toBe(!signed);
  expect(state.notice).toBe(signed ? "panel.dns.dnssecEnabledMsg" : "panel.dns.resultUnconfirmed");
});
it.each([false, true])("does not report a zone mode change when the returned mode still equals %s", async secondary => {
  readReply = async config => reply(config, { ...zone(), isSecondary: secondary, windowsZoneType: secondary ? "Secondary" : "Primary" });
  const { state } = await mount(); state.masterServersText = "192.0.2.53";
  writeReply = async config => reply(config, { ...zone(), isSecondary: secondary });
  if (secondary) await state.switchToPrimary(); else await state.switchToSecondary();
  expect(writes()).toHaveLength(1); expect(state.noticeError).toBe(true); expect(state.notice).toBe("panel.dns.resultUnconfirmed");
  expect(state.masterServersText).toBe("192.0.2.53");
});
it("accepts the confirmed secondary master snapshot and preserves later edits", async () => {
  const { state } = await mount(); state.masterServersText = "192.0.2.53";
  confirm.mockImplementationOnce(async () => { state.masterServersText = "192.0.2.54"; return true; });
  writeReply = async config => reply(config, { ...zone(), isSecondary: true, masterServers: ["192.0.2.53"], windowsZoneType: "Secondary", statusMessage: "dns.secondary_zone_active" });
  await state.switchToSecondary(); expect(writes()).toHaveLength(1); expect(JSON.parse(writes()[0]!.data).masterServers).toEqual(["192.0.2.53"]);
  expect(state.masterServersText).toBe("192.0.2.54"); expect(state.noticeError).toBe(false);
});
it("allows a measured empty panel-managed zone and multiline Unicode TXT records", async () => {
  readReply = async config => reply(config, { ...zone("A.TEST"), records: [], isWindowsDnsAvailable: false, isWindowsZonePresent: false, windowsZoneType: "", source: "panel-managed", statusMessage: "dns.windows_dns_unavailable" });
  const { state } = await mount("Reseller"); expect(state.zoneConfirmed).toBe(true); expect(state.zone?.records).toEqual([]);
  state.recordForm.type = "TXT"; state.recordForm.value = "özgün metin\nsecond line";
  writeReply = async config => reply(config, { ...zone(), records: [{ ...record(), type: "TXT", value: "özgün metin\nsecond line" }] });
  await state.saveRecord(); expect(writes()).toHaveLength(1); expect(state.noticeError).toBe(false);
});
