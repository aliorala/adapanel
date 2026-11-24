import { afterEach, expect, it, vi } from "vitest";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import AutomationView from "./AutomationView.vue";
import { getAutomation, runAutomationBackup, runAutomationSsl, type AutomationStatus } from "../api/automation";
import { getBackupRunningJobs } from "../api/backup";

vi.mock("../api/automation", () => ({ getAutomation: vi.fn(), runAutomationBackup: vi.fn(), runAutomationSsl: vi.fn(), updateAutomation: vi.fn(), testRemoteBackup: vi.fn() }));
vi.mock("../api/backup", () => ({ getBackupRunningJobs: vi.fn() }));
vi.mock("../stores/auth", () => ({ extractApiMessage: () => "failed", useAuthStore: () => ({ session: { isServerOwner: false } }) }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));

let app: App | undefined;
type State = {
  backupRunning: boolean; busy: boolean; notice: string; noticeError: boolean;
  status: AutomationStatus | null;
  runBackupNow: () => Promise<void>; runSslNow: () => Promise<void>;
};
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
  for (let i = 0; i < 32; i++) await nextTick();
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

const operations = [
  { run: "runBackupNow", api: runAutomationBackup, statusField: "lastBackupStatus", detailsField: "lastBackupDetails", successKey: "panel.automation.backupStarted", details: "1 site, 2 veritabanı yedeklendi, 0 eski yedek silindi.", translatedDetails: "panel.automation.result.backup" },
  { run: "runSslNow", api: runAutomationSsl, statusField: "lastSslStatus", detailsField: "lastSslDetails", successKey: "panel.automation.sslStarted", details: "1 sertifika yenilendi.", translatedDetails: "panel.automation.result.ssl" }
] as const;

for (const operation of operations) {
  it.each(["completed", "failed", "completed_with_errors", "", "unknown", "running"])(
    `${operation.run} reports the measured HTTP 200 outcome %j`, async outcome => {
      vi.mocked(getBackupRunningJobs).mockResolvedValue(jobs());
      const details = outcome === "completed" ? operation.details : "FTP: inventory unavailable";
      const result = { ...status, [operation.statusField]: outcome, [operation.detailsField]: details };
      vi.mocked(operation.api).mockResolvedValue(result);
      const state = await mount();

      await state[operation.run]();

      expect(state.status).toEqual(result);
      expect(state.busy).toBe(false);
      expect(state.backupRunning).toBe(false);
      expect(state.noticeError).toBe(outcome !== "completed");
      expect(state.notice).toContain(outcome === "completed" ? operation.translatedDetails : details);
      if (outcome === "completed") expect(state.notice).toContain(operation.successKey);
      else {
        expect(state.notice).not.toContain(operation.successKey);
        expect(state.notice).toContain(outcome === "failed" || outcome === "completed_with_errors"
          ? `panel.automation.statusValue.${outcome}` : "panel.automation.resultUnconfirmed");
      }
    }
  );

  it(`${operation.run} accepts a confirmed completed result without details`, async () => {
    vi.mocked(getBackupRunningJobs).mockResolvedValue(jobs());
    vi.mocked(operation.api).mockResolvedValue({ ...status, [operation.statusField]: "completed", [operation.detailsField]: "" });
    const state = await mount();
    await state[operation.run]();
    expect(state.noticeError).toBe(false);
    expect(state.notice).toBe(operation.successKey);
  });

  it(`${operation.run} preserves API rejection as an error and releases busy state`, async () => {
    vi.mocked(getBackupRunningJobs).mockResolvedValue(jobs());
    vi.mocked(operation.api).mockRejectedValue(new Error("network request rejected"));
    const state = await mount();
    await state[operation.run]();
    expect(state.notice).toBe("failed");
    expect(state.noticeError).toBe(true);
    expect(state.busy).toBe(false);
    expect(state.backupRunning).toBe(false);
    expect(state.status).toEqual(status);
  });
}
