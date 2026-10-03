import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import type { AxiosResponse, InternalAxiosRequestConfig } from "axios";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import NotificationsView from "./NotificationsView.vue";
import ErrorReportsView from "./ErrorReportsView.vue";
import { apiClient, invalidateSessionRequests } from "../api/client";
import { useAuthStore } from "../stores/auth";
import type { NotificationSettings } from "../api/notifications";
import type { ErrorReportingSettings, ErrorReport } from "../api/errorReports";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
type Family = "notifications" | "reports";
type State = { form: NotificationSettings; settings: ErrorReportingSettings; savedSettings: NotificationSettings | ErrorReportingSettings | null;
  busy: string; settingsLoaded: boolean; loaded: boolean; settingsConfirmed: boolean; loadError: string; hasUnsavedChanges: boolean;
  notice: string; noticeError: boolean; reports: ErrorReport[]; canSend: boolean; smtpReady: boolean;
  loadSettings(): Promise<void>; loadAll(): Promise<void>; save(): Promise<void>; test(channel: "email" | "webhook"): Promise<void>; sendNow(): Promise<void> };
const notification = (): NotificationSettings => ({ smtpHost: "smtp.example.test", smtpPort: 587, smtpUserName: "mail-user", smtpPassword: "", hasSmtpPassword: true, fromEmail: "panel@example.test", toEmail: "owner@example.test", useSsl: true, webhookUrl: "https://example.test/hooks/one" });
const reporting = (): ErrorReportingSettings => ({ enabled: true, collectorUrl: "https://example.test/reports", consentGrantedAtUtc: "2026-10-01T00:00:00Z", batchSize: 20, retentionDays: 30, lastSentAtUtc: null, lastError: "" });
const report = (): ErrorReport => ({ id: 1, fingerprint: "mock", createdAtUtc: "2026-10-03T00:00:00Z", lastSeenAtUtc: "2026-10-03T00:00:00Z", panelVersion: "0.8.76", osVersion: "Windows", exceptionType: "System.Exception", message: "Masked fixture", stackTrace: "", routePattern: "/api/fixture", correlationId: "mock", occurrenceCount: 1, sentAtUtc: null });
function response(config: InternalAxiosRequestConfig, data: unknown): AxiosResponse { return { config, data: { success: true, data }, headers: {}, status: 200, statusText: "OK" }; }
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
const originalAdapter = apiClient.defaults.adapter;
let app: App | undefined, calls: InternalAxiosRequestConfig[], read: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>, write: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
let notifications: NotificationSettings, settings: ErrorReportingSettings, reports: ErrorReport[];
async function flush() { for (let i = 0; i < 40; i++) await nextTick(); }
function unmount() { app?.unmount(); app = undefined; }
async function mount(family: Family) {
  const pinia = createPinia(), auth = useAuthStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "admin", userName: "admin", email: "a@example.test", role: "Administrator", preferredLanguage: "en", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } };
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...(family === "notifications" ? NotificationsView : ErrorReportsView), render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return { state, auth };
}
const mutations = () => calls.filter(config => config.method !== "get");
const load = (state: State, family: Family) => family === "notifications" ? state.loadSettings() : state.loadAll();
const send = (state: State, family: Family) => family === "notifications" ? state.test("email") : state.sendNow();
const value = (state: State, family: Family) => family === "notifications" ? state.form.toEmail : state.settings.collectorUrl;
function edit(state: State, family: Family) { if (family === "notifications") state.form.toEmail = "new@example.test"; else state.settings.collectorUrl = "https://other.example.test/reports"; }
beforeEach(() => {
  invalidateSessionRequests(); calls = []; notifications = notification(); settings = reporting(); reports = [report()];
  vi.stubGlobal("window", { location: { pathname: "/panel", hostname: "localhost" } });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "mock-token" } }), { status: 200 })));
  read = async config => response(config, config.url === "/notify/settings" ? { ...notifications } : config.url === "/error-reports/settings" ? { ...settings } : reports.map(item => ({ ...item })));
  write = async config => {
    if (config.method === "put") {
      const submitted = JSON.parse(String(config.data));
      if (config.url === "/notify/settings") { notifications = { ...submitted, smtpPassword: "", hasSmtpPassword: !!submitted.smtpPassword || submitted.hasSmtpPassword }; return response(config, { ...notifications }); }
      settings = { ...submitted, consentGrantedAtUtc: submitted.enabled ? "2026-10-01T00:00:00Z" : null }; return response(config, { ...settings });
    }
    return response(config, config.url === "/notify/test" ? { channel: JSON.parse(String(config.data)).channel } : { sentCount: 1 });
  };
  apiClient.defaults.adapter = async config => { calls.push(config); return config.method === "get" ? read(config) : write(config); };
});
afterEach(() => { unmount(); apiClient.defaults.adapter = originalAdapter; invalidateSessionRequests(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

for (const family of ["notifications", "reports"] as const) {
  it(`${family}: saves a measured settings snapshot and permits a normal saved-target send`, async () => {
    const { state } = await mount(family); await state.save(); expect(state.noticeError).toBe(false); await send(state, family);
    expect(mutations()).toHaveLength(2); expect(state.noticeError).toBe(false);
  });
  it.each(["unmount", "session", "actor"])(`${family}: ignores a settings read after %s replacement`, async change => {
    const pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig; const originalRead = read;
    read = config => config.url?.endsWith("/settings") ? (request = config, pending.promise) : originalRead(config);
    const { state, auth } = await mount(family); if (change === "unmount") unmount(); else if (change === "session") invalidateSessionRequests(); else auth.session!.user!.id = "other";
    pending.resolve(response(request, family === "notifications" ? notification() : reporting())); await flush();
    expect(family === "notifications" ? state.settingsLoaded : state.loaded).toBe(false); expect(state.notice).toBe("");
  });
  it.each(["false-envelope", "null", "bad-field"])(`${family}: does not accept unmeasured settings %s`, async kind => {
    const originalRead = read; read = async config => { if (!config.url?.endsWith("/settings")) return originalRead(config); const data = family === "notifications" ? { ...notification(), smtpPort: "587" } : { ...reporting(), enabled: "true" };
      return kind === "false-envelope" ? { ...response(config, family === "notifications" ? notification() : reporting()), data: { success: false, data: family === "notifications" ? notification() : reporting() } } : response(config, kind === "null" ? null : data); };
    const { state } = await mount(family); expect(family === "notifications" ? state.settingsLoaded : state.loaded).toBe(false); expect(state.loadError).not.toBe(""); expect(state.notice).toBe("");
  });
  it(`${family}: preserves edited drafts when an outstanding refresh completes`, async () => {
    const { state } = await mount(family), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig; const originalRead = read;
    read = config => config.url?.endsWith("/settings") ? (request = config, pending.promise) : originalRead(config); const loading = load(state, family); await flush(); edit(state, family); const draft = value(state, family);
    pending.resolve(response(request, family === "notifications" ? notification() : reporting())); await loading; expect(value(state, family)).toBe(draft);
  });
  it.each(["refresh", "retry"])(`${family}: preserves a draft already dirty before %s starts`, async operation => {
    const { state } = await mount(family); edit(state, family); const draft = value(state, family), originalRead = read;
    if (family === "notifications") state.form.smtpPassword = "unsaved-secret";
    if (operation === "retry") {
      read = async () => { throw new Error("fixture offline"); }; await load(state, family);
      expect(state.loadError).not.toBe(""); expect(value(state, family)).toBe(draft); read = originalRead;
    }
    await load(state, family);
    expect(value(state, family)).toBe(draft); expect(state.hasUnsavedChanges).toBe(true); expect(state.settingsConfirmed).toBe(true); expect(state.loadError).toBe("");
    if (family === "notifications") expect(state.form.smtpPassword).toBe("unsaved-secret");
    await state.save(); expect(state.hasUnsavedChanges).toBe(false); expect(state.noticeError).toBe(false);
  });
  it(`${family}: blocks writes while a refresh remains pending`, async () => {
    const { state } = await mount(family), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig; const originalRead = read;
    read = config => config.url?.endsWith("/settings") ? (request = config, pending.promise) : originalRead(config); const loading = load(state, family); await flush(); await state.save(); await send(state, family);
    expect(mutations()).toHaveLength(0); pending.resolve(response(request, family === "notifications" ? notification() : reporting())); await loading;
  });
  it(`${family}: reserves save against another save, send and refresh`, async () => {
    const { state } = await mount(family), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig; const reads = calls.length;
    write = config => (request = config, pending.promise); const saving = state.save(); await flush(); const second = state.save(), sending = send(state, family), loading = load(state, family);
    expect(mutations()).toHaveLength(1); expect(calls.filter(config => config.method === "get")).toHaveLength(reads);
    pending.resolve(response(request, family === "notifications" ? notification() : reporting())); await Promise.all([saving, second, sending, loading]);
  });
  it(`${family}: does not overwrite a draft edited during save`, async () => {
    const { state } = await mount(family), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
    write = config => (request = config, pending.promise); const saving = state.save(); await flush(); edit(state, family); const draft = value(state, family);
    pending.resolve(response(request, family === "notifications" ? notification() : reporting())); await saving;
    expect(value(state, family)).toBe(draft); expect(JSON.parse(String(request.data))[family === "notifications" ? "toEmail" : "collectorUrl"]).not.toBe(draft);
  });
  it.each(["unmount", "session"])(`${family}: does not publish a save after %s`, async change => {
    const { state } = await mount(family), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
    write = config => (request = config, pending.promise); const saving = state.save(); await flush(); if (change === "unmount") unmount(); else invalidateSessionRequests();
    pending.resolve(response(request, family === "notifications" ? notification() : reporting())); await saving; expect(state.notice).toBe("");
  });
  it(`${family}: uses saved settings and refuses to test an unsaved target draft`, async () => {
    const { state } = await mount(family); edit(state, family); await send(state, family); expect(mutations()).toHaveLength(0);
  });
  it(`${family}: preserves new drafts and does not attribute an older send result to them`, async () => {
    const { state } = await mount(family), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
    write = config => (request = config, pending.promise); const sending = send(state, family); await flush(); edit(state, family); const draft = value(state, family);
    pending.resolve(response(request, family === "notifications" ? { channel: "email" } : { sentCount: 1 })); await sending; expect(value(state, family)).toBe(draft); expect(state.notice).toBe("");
  });
  it(`${family}: a send completed after unmount starts no new reads or notice`, async () => {
    const { state } = await mount(family), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
    write = config => (request = config, pending.promise); const sending = send(state, family); await flush(); const count = calls.length; unmount();
    pending.resolve(response(request, family === "notifications" ? { channel: "email" } : { sentCount: 1 })); await sending; expect(calls).toHaveLength(count); expect(state.notice).toBe("");
  });
  it(`${family}: invalidates old measured settings after a failed refresh`, async () => {
    const { state } = await mount(family); const previous = value(state, family); read = async () => { throw new Error("fixture read failed"); }; await load(state, family); await state.save(); await send(state, family);
    expect(value(state, family)).toBe(previous); expect(mutations()).toHaveLength(0); expect(state.loadError).not.toBe("");
  });
  it(`${family}: rejects a HTTP200 false send envelope`, async () => {
    const { state } = await mount(family); write = async config => ({ ...response(config, family === "notifications" ? { channel: "email" } : { sentCount: 1 }), data: { success: false, data: family === "notifications" ? { channel: "email" } : { sentCount: 1 } } });
    await send(state, family); expect(state.noticeError).toBe(true);
  });
  it(`${family}: does not call a mismatched saved target successful`, async () => {
    const { state } = await mount(family); write = async config => response(config, family === "notifications" ? { ...notification(), toEmail: "unexpected@example.test" } : { ...reporting(), collectorUrl: "https://unexpected.example.test" });
    await state.save(); expect(state.noticeError).toBe(true);
  });
}
it("notifications: a new SMTP target retains the same password draft while the old save finishes", async () => {
  const { state } = await mount("notifications"); state.form.smtpPassword = "new-secret"; const pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = config => (request = config, pending.promise); const saving = state.save(); await flush(); state.form.smtpHost = "different.example.test";
  pending.resolve(response(request, notification())); await saving; expect(state.form.smtpHost).toBe("different.example.test"); expect(state.form.smtpPassword).toBe("new-secret");
});
it("notifications: verifies the channel returned by a test", async () => {
  const { state } = await mount("notifications"); write = async config => response(config, { channel: "webhook" }); await state.test("email"); expect(state.noticeError).toBe(true);
});
it("reports: permits sending older pending records beyond the recent fifty-row list", async () => {
  reports = Array.from({ length: 50 }, (_, index) => ({ ...report(), id: index + 1, sentAtUtc: "2026-10-03T00:00:00Z" }));
  const { state } = await mount("reports"); expect(state.canSend).toBe(true); await state.sendNow(); expect(mutations()).toHaveLength(1); expect(state.noticeError).toBe(false);
});
it("reports: an unsaved consent toggle cannot enable delivery", async () => {
  settings = { ...reporting(), enabled: false, consentGrantedAtUtc: null }; const { state } = await mount("reports"); state.settings.enabled = true; await state.sendNow(); expect(mutations()).toHaveLength(0);
});
it.each([-1, 1.5, "1", undefined])("reports: rejects an unmeasured sentCount %s", async sentCount => {
  const { state } = await mount("reports"); write = async config => response(config, { sentCount }); await state.sendNow(); expect(state.noticeError).toBe(true);
});
it("reports: keeps measured delivery visible alongside a failed refresh, without reopening writes", async () => {
  const { state } = await mount("reports"); read = async () => { throw new Error("refresh failed"); }; await state.sendNow();
  expect(state.loadError).not.toBe(""); expect(state.notice).toBe("panel.errorReports.sentResult: 1"); expect(state.settingsConfirmed).toBe(false); expect(state.reports).toHaveLength(1);
});
it("reports: does not convert a malformed list into a measured empty list", async () => {
  const originalRead = read; read = config => config.url === "/error-reports" ? Promise.resolve(response(config, {})) : originalRead(config);
  const { state } = await mount("reports"); expect(state.loaded).toBe(false); expect(state.loadError).not.toBe("");
});
