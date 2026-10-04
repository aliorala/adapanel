import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { AxiosError, type AxiosResponse, type InternalAxiosRequestConfig } from "axios";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component, type Ref } from "vue";
import SiteBackupView from "./SiteBackupView.vue";
import { useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
import { apiClient, invalidateSessionRequests } from "../api/client";
import { backupSite, type BackupRecord } from "../api/backup";

vi.mock("../router", () => ({ default: { push: vi.fn() } }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key, locale: { value: "en" } }) }));
type State = { availableDatabases: { engine: string; name: string }[]; selectedDbKeys: string[]; canReadDatabases: boolean;
  databasesLoad: { message: Ref<string> }; listLoad: { message: Ref<string> }; encryptionPassword: string; notice: string; noticeError: boolean;
  createResult: BackupRecord | null; createBackup(): Promise<void>; loadDatabases(): Promise<void>; refresh(): Promise<boolean> };
const record = (): BackupRecord => ({ id: "created-one", name: "alpha.zip", type: "site", source: "alpha.test", path: "", format: "zip", createdAtUtc: "2026-10-04T12:00:00Z", size: 100,
  status: "created", targetType: "local", remoteUri: "", encrypted: false, warnings: [], preservedPath: "", chainId: "", chainIndex: 0, backupMode: "full", baseBackupId: "", subscriptionUserId: "", itemCount: 1, localMissing: false, remoteMissing: false, remoteVerifiedUtc: null });
const owned = () => [{ engine: "postgres", name: "Foo" }, { engine: "postgresql", name: "foo" }];
function response(config: InternalAxiosRequestConfig, data: unknown): AxiosResponse { return { config, data: { success: true, data }, headers: {}, status: 200, statusText: "OK" }; }
function deferred<T>() { let resolve!: (value: T) => void, reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
const originalAdapter = apiClient.defaults.adapter;
let app: App | undefined, calls: InternalAxiosRequestConfig[];
let read: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>, write: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
async function flush() { for (let i = 0; i < 45; i++) await nextTick(); }
function unmount() { app?.unmount(); app = undefined; }
function renderMarkup(state: State) {
  const output: unknown[] = [];
  const render = (SiteBackupView as unknown as { ssrRender: (...args: unknown[]) => void }).ssrRender;
  render(state, (part: unknown) => output.push(part), app!._instance, {}, {}, state, {}, {});
  return output.flat(Infinity).join("").replace(/<!--[\s\S]*?-->/g, "");
}
async function mount(role = "Customer", sections?: string[]) {
  const pinia = createPinia(), auth = useAuthStore(pinia); auth.session = { isAuthenticated: true, requiresSetupWizard: false,
    isSubUser: sections !== undefined, allowedSections: sections,
    user: { id: "actor", userName: "fixture", email: "fixture@example.test", role, preferredLanguage: "en", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } };
  useSitesStore(pinia).selectedSiteName = "alpha.test";
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...SiteBackupView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return { state, auth };
}
beforeEach(() => {
  calls = []; invalidateSessionRequests();
  vi.stubGlobal("window", { location: { pathname: "/panel" }, setTimeout, clearTimeout, setInterval, clearInterval });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture-token" } }), { status: 200 })));
  read = async config => response(config, config.url === "/backup" ? [] : config.url === "/my-databases" ? owned() : config.url === "/db/engines" ? [{ engine: "mysql", displayName: "MySQL", installed: true }] : config.url === "/db" ? { databases: [{ name: "admin_db" }] } : { running: [], interrupted: [], serverTimeUtc: "2026-10-04T12:00:00Z" });
  write = async config => response(config, record());
  apiClient.defaults.adapter = async config => { calls.push(config); return config.method === "get" ? read(config) : write(config); };
});
afterEach(() => { unmount(); apiClient.defaults.adapter = originalAdapter; invalidateSessionRequests(); vi.unstubAllGlobals(); });
for (const role of ["Customer", "Reseller"]) {
  it(`${role} reads owned database choices without calling administrator inventory`, async () => {
    const { state } = await mount(role);
    expect(state.availableDatabases).toEqual(owned()); expect(calls.some(config => config.url === "/db/engines" || config.url === "/db")).toBe(false);
    expect(state.databasesLoad.message.value).toBe("");
  });
  it(`${role} sub-user without database section access can back up files without a forbidden inventory call`, async () => {
    const { state } = await mount(role, ["sites", "backup"]);
    expect(state.canReadDatabases).toBe(false); expect(calls.some(config => ["/db", "/db/engines", "/my-databases"].includes(config.url ?? ""))).toBe(false);
    await state.createBackup(); expect(calls.filter(config => config.method === "post")).toHaveLength(1);
  });
}
it("preserves the administrator's complete database workspace choices", async () => {
  const { state } = await mount("Administrator"); expect(state.availableDatabases).toEqual([{ engine: "mysql", name: "admin_db" }]); expect(calls.some(config => config.url === "/my-databases")).toBe(false);
});
it("supports a permitted sub-user and preserves selected PostgreSQL identities in the submitted backup", async () => {
  const { state } = await mount("Customer", ["sites", "DATABASE", "backup"]); expect(state.availableDatabases).toEqual(owned());
  state.selectedDbKeys = ["postgres:Foo", "postgresql:foo"]; await state.createBackup();
  const submitted = JSON.parse(String(calls.find(config => config.method === "post")!.data));
  expect(submitted.databases).toEqual([{ engine: "postgres", databaseName: "Foo" }, { engine: "postgresql", databaseName: "foo" }]);
});
it("shows a scoped inventory failure and allows an explicit successful retry", async () => {
  const normalRead = read; read = config => config.url === "/my-databases" ? Promise.reject(new Error("owned inventory unavailable")) : normalRead(config);
  const { state } = await mount(); expect(state.availableDatabases).toEqual([]); expect(state.databasesLoad.message.value).not.toBe("");
  read = normalRead; await state.loadDatabases(); expect(state.availableDatabases).toEqual(owned()); expect(state.databasesLoad.message.value).toBe("");
});
it("does not republish scoped inventory after actor change", async () => {
  const pending = deferred<AxiosResponse>(), normalRead = read; let request!: InternalAxiosRequestConfig;
  read = config => config.url === "/my-databases" ? (request = config, pending.promise) : normalRead(config);
  const { state, auth } = await mount(); auth.session!.user!.id = "different"; await flush();
  if (request) pending.resolve(response(request, owned())); await flush(); expect(state.availableDatabases).toEqual([]);
});
for (const status of ["created_with_errors", "created_remote_failed"]) it(`reports measured partial outcome ${status} with the original warning`, async () => {
  const { state } = await mount(); write = async config => response(config, { ...record(), status, warnings: [{ messageKey: "backup.database_dump_empty", target: "postgresql/Foo", detail: "" }] });
  await state.createBackup(); expect(state.noticeError).toBe(true);
  expect(state.createResult?.warnings).toEqual([{ messageKey: "backup.database_dump_empty", target: "postgresql/Foo", detail: "" }]);
});
it.each(["created_with_errors", "created_remote_failed"])("does not turn an explicit partial status into green when warnings are empty: %s", async status => {
  const { state } = await mount(); write = async config => response(config, { ...record(), status }); await state.createBackup(); expect(state.noticeError).toBe(true);
});
it.each(["created", "created_remote_synced"])("keeps a complete measured outcome successful: %s", async status => {
  const { state } = await mount(); write = async config => response(config, { ...record(), status }); await state.createBackup(); expect(state.noticeError).toBe(false); expect(state.notice).toBe("panel.backup.created");
});
it("retains the partial backup warning when refreshing the list also fails", async () => {
  const { state } = await mount(), normalRead = read;
  write = async config => response(config, { ...record(), status: "created_with_errors", warnings: [{ messageKey: "backup.database_dump_empty", target: "postgresql/Foo", detail: "" }] });
  read = config => config.url === "/backup" ? Promise.reject(new Error("list unavailable")) : normalRead(config);
  await state.createBackup(); expect(state.noticeError).toBe(true); expect(state.createResult?.warnings[0]?.messageKey).toBe("backup.database_dump_empty"); expect(state.listLoad.message.value).not.toBe(""); expect(state.notice).toContain(state.listLoad.message.value);
});
it("preserves a newly edited encryption password when an earlier backup completes on the same site", async () => {
  const { state } = await mount(), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = config => (request = config, pending.promise); state.encryptionPassword = "first-secret"; const creating = state.createBackup(); await flush(); state.encryptionPassword = "next-secret";
  pending.resolve(response(request, record())); await creating; expect(state.encryptionPassword).toBe("next-secret"); expect(JSON.parse(String(request.data)).encryptionPassword).toBe("first-secret");
});
it.each(["failed-envelope", "missing-record", "wrong-source", "wrong-type", "unknown-status", "invalid-warnings"])("rejects an unmeasured create response before presenting success: %s", async kind => {
  write = async config => {
    const value = { ...record(), ...(kind === "wrong-source" ? { source: "other.test" } : kind === "wrong-type" ? { type: "panel" } : kind === "unknown-status" ? { status: "running" } : kind === "invalid-warnings" ? { warnings: [null] } : {}) };
    const result = response(config, kind === "missing-record" ? null : value); if (kind === "failed-envelope") result.data.success = false; return result;
  };
  await expect(backupSite("alpha.test")).rejects.toThrow();
});
it("keeps the original authorization error without inventing an empty result", async () => {
  write = async config => { throw new AxiosError("forbidden", "ERR_BAD_REQUEST", config, null, { ...response(config, null), status: 403, data: { success: false, message: "auth.site_forbidden" } }); };
  await expect(backupSite("alpha.test")).rejects.toMatchObject({ response: { status: 403 } });
});
it.each([undefined, Number.NaN, -1, Number.POSITIVE_INFINITY])("rejects an unmeasured archive size: %s", async size => {
  write = async config => response(config, { ...record(), size });
  await expect(backupSite("alpha.test")).rejects.toThrow();
});
it("ignores a completed backup after the same actor receives a new session", async () => {
  const { state, auth } = await mount(), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = config => (request = config, pending.promise); state.encryptionPassword = "session-secret";
  const creating = state.createBackup(); await flush();
  auth.session = { ...auth.session!, user: { ...auth.session!.user! } }; await flush();
  const readsBeforeResult = calls.filter(config => config.url === "/backup").length;
  pending.resolve(response(request, record())); await creating;
  expect(state.createResult).toBeNull(); expect(state.notice).toBe(""); expect(state.encryptionPassword).toBe("session-secret");
  expect(calls.filter(config => config.url === "/backup")).toHaveLength(readsBeforeResult);
});
it("renders scoped database choices using their exact PostgreSQL names", async () => {
  const { state } = await mount(); const html = renderMarkup(state);
  expect(html).toContain('value="postgres:Foo"'); expect(html).toContain('value="postgresql:foo"');
  expect(html).not.toContain("admin_db"); expect(html).not.toContain("panel.backup.databasePermissionHint");
});
it("renders partial backup details once in the result panel with an honest notice", async () => {
  const { state } = await mount();
  write = async config => response(config, { ...record(), status: "created_with_errors", warnings: [{ messageKey: "backup.database_dump_empty", target: "postgresql/Foo", detail: "Original dump failure" }] });
  await state.createBackup(); const html = renderMarkup(state);
  expect(html).toContain('class="form-surface create-result"'); expect(html).toContain("panel.backup.status.created_with_errors");
  expect(html.match(/backup.database_dump_empty/g)).toHaveLength(1); expect(html).toContain("postgresql/Foo"); expect(html).toContain("Original dump failure");
  expect(state.notice).toBe("panel.backup.createdWithErrors"); expect(html).toMatch(/role="alert"[^>]*>panel.backup.createdWithErrors/);
});
it("renders an initial list failure with retry instead of an empty or endless loading state", async () => {
  const normalRead = read; read = config => config.url === "/backup" ? Promise.reject(new Error("list unavailable")) : normalRead(config);
  const { state } = await mount(); const html = renderMarkup(state);
  expect(html).toContain("section-load-error"); expect(html).toContain("panel.common.retry"); expect(html).toMatch(/class="meta"[^>]*>—<\/span>/);
  expect(html).not.toContain("panel.backup.empty"); expect(html).not.toContain("panel.common.loading");
  read = normalRead; await state.refresh(); const recovered = renderMarkup(state);
  expect(recovered).not.toContain("section-load-error"); expect(recovered).toMatch(/class="meta"[^>]*>0<\/span>/);
});
