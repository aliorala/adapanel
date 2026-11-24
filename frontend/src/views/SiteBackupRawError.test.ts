import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import type { AxiosResponse, InternalAxiosRequestConfig } from "axios";
import SiteBackupView from "./SiteBackupView.vue";
import { apiClient, invalidateSessionRequests } from "../api/client";
import type { BackupRecord } from "../api/backup";
import { useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";

vi.mock("../router", () => ({ default: { currentRoute: { value: { query: {} } }, push: vi.fn() } }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key, locale: { value: "en" } }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: async (options: { action?: () => Promise<unknown> }) => { await options.action?.(); return true; } }) }));
vi.mock("../components/PromptDialog.vue", () => ({ usePrompt: () => ({ prompt: async () => "" }) }));

const record = (): BackupRecord => ({ id: "one", name: "alpha.zip", type: "site", source: "alpha.test", path: "/backups/alpha.zip", format: "zip", createdAtUtc: "2026-10-04T12:00:00Z", size: 100,
  status: "created", targetType: "local", remoteUri: "", encrypted: false, warnings: [], preservedPath: "", chainId: "", chainIndex: 0, backupMode: "full", baseBackupId: "", subscriptionUserId: "", itemCount: 1, localMissing: false, remoteMissing: false, remoteVerifiedUtc: null });
function response(config: InternalAxiosRequestConfig, data: unknown): AxiosResponse { return { config, data: { success: true, data }, headers: {}, status: 200, statusText: "OK" }; }
async function flush() { for (let i = 0; i < 70; i++) await nextTick(); }
type State = { notice: string; noticeError: boolean; restore(backup: BackupRecord): Promise<void> };
const originalAdapter = apiClient.defaults.adapter;
let app: App | undefined;
let restoreError: unknown;

beforeEach(() => {
  invalidateSessionRequests(); restoreError = undefined;
  vi.stubGlobal("window", { location: { pathname: "/panel" }, setTimeout, clearTimeout, setInterval, clearInterval, addEventListener: vi.fn(), removeEventListener: vi.fn() });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture-token" } }), { status: 200 })));
  apiClient.defaults.adapter = async config => {
    if (config.url === "/backup/restore" && restoreError) throw restoreError;
    const data = config.url === "/backup" ? [record()]
      : config.url === "/backup/encryption" ? { enabled: false, passwordUsable: false, algorithm: "AES", updatedAtUtc: "" }
      : config.url?.includes("running") ? { running: [], interrupted: [], serverTimeUtc: "2026-10-04T12:00:00Z" } : {};
    return response(config, data);
  };
});
afterEach(() => { app?.unmount(); app = undefined; apiClient.defaults.adapter = originalAdapter; invalidateSessionRequests(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

async function mount() {
  const pinia = createPinia(), auth = useAuthStore(pinia), sites = useSitesStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "admin", role: "Administrator", userName: "admin", email: "admin@example.test", preferredLanguage: "en", isActive: true, isTwoFactorEnabled: false, lastLoginAtUtc: null } };
  sites.selectedSiteName = "alpha.test"; sites.sites = [{ name: "alpha.test" }] as never;
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...SiteBackupView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); state.notice = ""; return state;
}

it("never shows the text of an unexpected JavaScript error to the user; it shows the generic message and logs the error", async () => {
  const logged = vi.spyOn(console, "error").mockImplementation(() => undefined);
  const state = await mount();
  restoreError = new TypeError("Cannot read properties of undefined (reading 'secretInternalField')");

  await state.restore(record());

  expect(state.notice).toBe("common.unexpected_error");
  expect(state.notice).not.toContain("secretInternalField");
  expect(state.noticeError).toBe(true);
  expect(logged).toHaveBeenCalled();
});
