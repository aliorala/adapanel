import { afterEach, expect, it, vi } from "vitest";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import AutomationView from "./AutomationView.vue";
import { getAutomation, runAutomationBackup, type AutomationStatus } from "../api/automation";
import { getBackupRunningJobs } from "../api/backup";

vi.mock("../api/automation", () => ({ getAutomation: vi.fn(), runAutomationBackup: vi.fn(), runAutomationSsl: vi.fn(), updateAutomation: vi.fn(), testRemoteBackup: vi.fn() }));
vi.mock("../api/backup", () => ({ getBackupRunningJobs: vi.fn() }));
vi.mock("../stores/auth", () => ({ extractApiMessage: () => "failed" }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));

let app: App | undefined;
type State = { backupRunning: boolean; runBackupNow: () => Promise<void> };
const status = { settings: {} } as AutomationStatus;
const jobs = (title?: string) => ({ running: title ? [{ kind: "backup", title, startedAtUtc: "2026-09-18T00:00:00Z" }] : [], interrupted: [], serverTimeUtc: "2026-09-18T00:01:00Z" });
async function mount() {
  vi.mocked(getAutomation).mockResolvedValue(status);
  const renderer = createRenderer<object, object>({
    createElement: () => ({}), createText: () => ({}), createComment: () => ({}),
    insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {},
    parentNode: () => null, nextSibling: () => null
  });
  app = renderer.createApp({ ...AutomationView, render: () => h("section") } as Component);
  app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState;
  await nextTick(); await nextTick(); await nextTick();
  return state;
}
afterEach(() => { app?.unmount(); app = undefined; vi.resetAllMocks(); vi.useRealTimers(); });

it("resumes the running indicator after navigation and prevents duplicate submission", async () => {
  vi.useFakeTimers();
  vi.mocked(getBackupRunningJobs).mockResolvedValueOnce(jobs("automation")).mockResolvedValue(jobs());
  const state = await mount();
  expect(state.backupRunning).toBe(true);
  await state.runBackupNow();
  expect(runAutomationBackup).not.toHaveBeenCalled();
  await vi.advanceTimersByTimeAsync(15000);
  expect(state.backupRunning).toBe(false);
});

it("shows a newly started job immediately and clears it when the request fails", async () => {
  vi.mocked(getBackupRunningJobs).mockResolvedValue(jobs());
  let reject!: (error: Error) => void;
  vi.mocked(runAutomationBackup).mockImplementation(() => new Promise((_, fail) => { reject = fail; }));
  const state = await mount();
  const request = state.runBackupNow();
  expect(state.backupRunning).toBe(true);
  await state.runBackupNow();
  expect(runAutomationBackup).toHaveBeenCalledTimes(1);
  reject(new Error("connection lost")); await request;
  expect(state.backupRunning).toBe(false);
});

it("does not mistake another site's backup for an automation job", async () => {
  vi.mocked(getBackupRunningJobs).mockResolvedValue(jobs("example.com"));
  expect((await mount()).backupRunning).toBe(false);
});
