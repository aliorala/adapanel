import { afterEach, expect, it, vi } from "vitest";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import UpdateView from "./UpdateView.vue";
import FirewallView from "./FirewallView.vue";
import { checkForUpdate, applyUpdate, type UpdateStatus } from "../api/update";
import { getPortStatus, type PortStatus } from "../api/firewall";

vi.mock("../api/update", () => ({ checkForUpdate: vi.fn(), applyUpdate: vi.fn(), installPreparedUpdate: vi.fn() }));
vi.mock("../api/firewall", () => ({ getPortStatus: vi.fn(), getFirewallRules: vi.fn().mockResolvedValue([]), allowPort: vi.fn(), blockPort: vi.fn() }));
vi.mock("../stores/auth", () => ({ extractApiMessage: () => "failed" }));
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
  app = renderer.createApp({ ...view, render: () => h("section") } as Component);
  app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: T }).setupState;
  await nextTick(); await nextTick(); await nextTick();
  return state;
}

it("ignores a late version check after downloading and prevents duplicate downloads", async () => {
  const old = { isPackageReady: false, downloadUrl: "https://example.test/update", packageSha256: "test" } as UpdateStatus;
  const ready = { ...old, isPackageReady: true };
  vi.mocked(checkForUpdate).mockResolvedValueOnce(old);
  const state = await mount<{ status: UpdateStatus; refresh: () => Promise<void>; downloadUpdate: () => Promise<void> }>(UpdateView);
  let finishCheck!: (v: UpdateStatus) => void;
  vi.mocked(checkForUpdate).mockImplementationOnce(() => new Promise(resolve => { finishCheck = resolve; }));
  const checking = state.refresh();
  let finishDownload!: (v: UpdateStatus) => void;
  vi.mocked(applyUpdate).mockImplementationOnce(() => new Promise(resolve => { finishDownload = resolve; }));
  const downloading = state.downloadUpdate();
  await state.downloadUpdate();
  expect(applyUpdate).toHaveBeenCalledTimes(1);
  finishDownload(ready); await downloading;
  finishCheck(old); await checking;
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
