import { afterEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import UpdateView from "./UpdateView.vue";
import FirewallView from "./FirewallView.vue";
import { checkForUpdate, applyUpdate, type UpdateStatus } from "../api/update";
import { getPortStatus, type PortStatus } from "../api/firewall";

vi.mock("../api/update", () => ({ checkForUpdate: vi.fn(), applyUpdate: vi.fn(), installPreparedUpdate: vi.fn() }));
vi.mock("../api/firewall", () => ({ getPortStatus: vi.fn(), getFirewallRules: vi.fn().mockResolvedValue([]), allowPort: vi.fn(), blockPort: vi.fn() }));
vi.mock("../stores/auth", async original => ({ ...await original<object>(), extractApiMessage: () => "failed" }));
vi.mock("../components/ConfirmDialog.vue", () => ({ default: {}, useConfirm: () => ({ confirm: vi.fn().mockResolvedValue(false) }) }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));

let app: App | undefined;
afterEach(() => { app?.unmount(); app = undefined; vi.clearAllMocks(); });
async function mount<T>(view: Component): Promise<T> {
  const renderer = createRenderer<object, object>({
    createElement: () => ({}), createText: () => ({}), createComment: () => ({}),
    insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {},
    parentNode: () => null, nextSibling: () => null
  });
  app = renderer.createApp({ ...view, render: () => h("section") } as Component).use(createPinia());
  app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: T }).setupState;
  await nextTick(); await nextTick(); await nextTick();
  return state;
}

it("keeps a deferred version check from overlapping a download and prevents duplicate downloads", async () => {
  const old: UpdateStatus = { currentVersion: "0.8.75", latestVersion: "0.8.76", isUpdateAvailable: true, isCheckFailed: false, lastAttempt: null,
    isPackageReady: false, isInstallScheduled: false, installScheduledAtUtc: null, packagePath: "", downloadUrl: "https://example.test/update", packageSha256: "a".repeat(64),
    notes: "", githubRepo: "fixture/panel", checkedAtUtc: "2026-10-04T00:00:00Z" };
  const ready = { ...old, isPackageReady: true, preparedVersion: old.latestVersion, preparedPackageSha256: old.packageSha256 };
  vi.mocked(checkForUpdate).mockResolvedValueOnce(old);
  const state = await mount<{ status: UpdateStatus; refresh: () => Promise<void>; downloadUpdate: () => Promise<void> }>(UpdateView);
  let finishCheck!: (v: UpdateStatus) => void;
  vi.mocked(checkForUpdate).mockImplementationOnce(() => new Promise(resolve => { finishCheck = resolve; }));
  const checking = state.refresh();
  let finishDownload!: (v: UpdateStatus) => void;
  vi.mocked(applyUpdate).mockImplementationOnce(() => new Promise(resolve => { finishDownload = resolve; }));
  // An outstanding GET now closes the write gate rather than allowing a write
  // to race it. Keep the read genuinely pending while exercising the handler.
  await state.downloadUpdate();
  expect(applyUpdate).not.toHaveBeenCalled();
  finishCheck(old); await checking;
  const downloading = state.downloadUpdate();
  await state.downloadUpdate();
  await state.refresh();
  expect(applyUpdate).toHaveBeenCalledTimes(1);
  expect(checkForUpdate).toHaveBeenCalledTimes(2);
  finishDownload(ready); await downloading;
  expect(state.status.isPackageReady).toBe(true);
});

it("does not call unknown firewall or listener measurements healthy or offer automatic port opening", async () => {
  const unknown = { port: 80, required: true, listening: true, firewallAllowed: true, firewallMeasured: false, listeningMeasured: false } as PortStatus;
  vi.mocked(getPortStatus).mockResolvedValue([unknown]);
  const state = await mount<{ requiredOpenCount: number; missingRequired: PortStatus[]; rowStatus: (p: PortStatus) => string; listenLabel: (p: PortStatus) => string }>(FirewallView);
  expect(state.requiredOpenCount).toBe(0);
  expect(state.missingRequired).toEqual([]);
  expect(state.rowStatus(unknown)).toBe("warn");
  expect(state.listenLabel(unknown)).toBe("panel.security.unmeasured");
});
