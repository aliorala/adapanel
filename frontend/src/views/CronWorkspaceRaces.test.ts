import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import CronView from "./CronView.vue";
import { createScheduledTask, deleteScheduledTask, getScheduledTasks, getScheduledTaskOutput, getScheduledTaskStatus, runScheduledTask, stopScheduledTask, type ScheduledTask, type ScheduledTaskOutput } from "../api/cron";

const confirm = vi.hoisted(() => vi.fn());
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm }) }));
vi.mock("../api/cron", () => ({ createScheduledTask: vi.fn(), deleteScheduledTask: vi.fn(), getScheduledTasks: vi.fn(), getScheduledTaskOutput: vi.fn(), getScheduledTaskStatus: vi.fn(), runScheduledTask: vi.fn(), addPythonToMachinePath: vi.fn(), getTaskEnvironment: vi.fn(), setScheduledTaskEnabled: vi.fn(), stopScheduledTask: vi.fn() }));
const task = (name: string): ScheduledTask => ({ name, taskType: "url", url: "https://example.test/cron", scheduleType: "hourly", state: "Ready", command: "", arguments: "", workingDirectory: "", nextRunTimeUtc: null, lastRunTimeUtc: null, lastTaskResult: null, capturesOutput: true, interval: 1, repetitionInterval: "", scheduleUnknown: false, timeOfDay: "00:00", phpVersion: "", runAs: "system", timeLimitHours: 0 });
const output = (content: string): ScheduledTaskOutput => ({ name: "daily", content, available: true, truncated: false, writtenAtUtc: null });
type State = { form: { name: string; taskType: string; url: string }; editingName: string; formOpen: boolean; tasks: ScheduledTask[]; notice: string; refresh(): Promise<boolean>; outputs: Record<string, ScheduledTaskOutput>; runStates: Record<string, { phase: string; resultCode: number | null }>; editTask(value: ScheduledTask): void; saveTask(): Promise<void>; resetForm(): void; runTask(name: string): Promise<void>; removeTask(name: string): Promise<void>; stopRun(value: ScheduledTask): Promise<void>; closeRunPanel(name: string): void; showOutput(name: string): Promise<void>; closeOutput(name: string): void };
let app: App | undefined;
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
async function flush() { for (let i = 0; i < 12; i++) await nextTick(); }
beforeEach(() => {
  confirm.mockResolvedValue(true);
  vi.useFakeTimers();
  vi.stubGlobal("window", { setInterval, clearInterval, scrollTo: vi.fn() });
  vi.mocked(getScheduledTasks).mockResolvedValue([]);
  vi.mocked(createScheduledTask).mockResolvedValue(task("daily"));
  vi.mocked(deleteScheduledTask).mockResolvedValue();
  vi.mocked(runScheduledTask).mockResolvedValue();
  vi.mocked(getScheduledTaskOutput).mockResolvedValue(output("current"));
  vi.mocked(getScheduledTaskStatus).mockResolvedValue({ name: "daily", state: "Running", isRunning: true, lastTaskResult: null, lastRunTimeUtc: null });
});
afterEach(() => { app?.unmount(); app = undefined; vi.clearAllTimers(); vi.useRealTimers(); vi.unstubAllGlobals(); vi.resetAllMocks(); });
async function mount() {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia);
  app = renderer.createApp({ ...CronView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return state;
}
it("does not delete a saved task when its name is edited during the request", async () => {
  const state = await mount(), pending = deferred<ScheduledTask>(); state.editTask(task("daily"));
  vi.mocked(createScheduledTask).mockReturnValueOnce(pending.promise);
  const save = state.saveTask(); state.form.name = "next-draft"; pending.resolve(task("daily")); await save;
  expect(deleteScheduledTask).not.toHaveBeenCalled(); expect(state.form.name).toBe("next-draft"); expect(state.formOpen).toBe(true);
});
it("completes a rename using the submitted name while preserving another task's draft", async () => {
  const state = await mount(), pending = deferred<ScheduledTask>(); state.editTask(task("daily")); state.form.name = "renamed";
  vi.mocked(createScheduledTask).mockReturnValueOnce(pending.promise);
  const save = state.saveTask(); state.editTask(task("another")); pending.resolve(task("renamed")); await save;
  expect(deleteScheduledTask).toHaveBeenCalledExactlyOnceWith("daily"); expect(state.editingName).toBe("another"); expect(state.form.name).toBe("another"); expect(state.formOpen).toBe(true);
});
it("sends only one save while the first is pending", async () => {
  const state = await mount(), pending = deferred<ScheduledTask>(); state.editTask(task("daily")); vi.mocked(createScheduledTask).mockReturnValue(pending.promise);
  const first = state.saveTask(), second = state.saveTask(); pending.resolve(task("daily")); await Promise.all([first, second]); expect(createScheduledTask).toHaveBeenCalledOnce();
});
it("keeps customer forms as URL tasks after saving and resetting", async () => {
  const state = await mount(); state.editTask(task("daily")); await state.saveTask(); expect(state.formOpen).toBe(false); expect(state.form.taskType).toBe("url"); state.resetForm(); expect(state.form.taskType).toBe("url");
});
it("does not recreate polling when the start response arrives after unmount", async () => {
  const state = await mount(), pending = deferred<void>(); vi.mocked(runScheduledTask).mockReturnValueOnce(pending.promise);
  const run = state.runTask("daily"); app!.unmount(); app = undefined; pending.resolve(); await run;
  expect(vi.getTimerCount()).toBe(0); await vi.advanceTimersByTimeAsync(2500); expect(getScheduledTaskStatus).not.toHaveBeenCalled();
});
it("does not let a previous run's status finish the new run", async () => {
  const state = await mount(), pending = deferred<Awaited<ReturnType<typeof getScheduledTaskStatus>>>();
  vi.mocked(getScheduledTaskStatus).mockReturnValueOnce(pending.promise); await state.runTask("daily"); await vi.advanceTimersByTimeAsync(2500);
  await state.runTask("daily"); pending.resolve({ name: "daily", state: "Ready", isRunning: false, lastTaskResult: 7, lastRunTimeUtc: null }); await flush();
  expect(state.runStates.daily.phase).toBe("starting"); expect(getScheduledTaskOutput).not.toHaveBeenCalled();
  await vi.advanceTimersByTimeAsync(2500); expect(state.runStates.daily.phase).toBe("running");
});
it("does not restart tracking after closing a pending run panel", async () => {
  const state = await mount(), pending = deferred<void>(); vi.mocked(runScheduledTask).mockReturnValueOnce(pending.promise);
  const run = state.runTask("daily"); state.closeRunPanel("daily"); pending.resolve(); await run;
  expect(vi.getTimerCount()).toBe(1); expect(state.runStates.daily).toBeUndefined();
});
it("ignores an old output after the output panel is closed and reopened", async () => {
  const state = await mount(), pending = deferred<ScheduledTaskOutput>(); vi.mocked(getScheduledTaskOutput).mockReturnValueOnce(pending.promise);
  const first = state.showOutput("daily"); state.closeOutput("daily"); await state.showOutput("daily"); pending.resolve(output("old")); await first; expect(state.outputs.daily.content).toBe("current");
});
it("shows a completed run and its actual output", async () => {
  const state = await mount(); vi.mocked(getScheduledTaskStatus).mockResolvedValueOnce({ name: "daily", state: "Ready", isRunning: false, lastTaskResult: 0, lastRunTimeUtc: null });
  await state.runTask("daily"); await vi.advanceTimersByTimeAsync(2500); expect(state.runStates.daily).toMatchObject({ phase: "done", resultCode: 0 }); expect(state.outputs.daily.content).toBe("current");
});
it("does not delete the original task when saving the renamed task fails", async () => {
  const state = await mount(); state.editTask(task("daily")); state.form.name = "renamed"; vi.mocked(createScheduledTask).mockRejectedValueOnce(new Error("fixture save failed"));
  await state.saveTask(); expect(deleteScheduledTask).not.toHaveBeenCalled(); expect(state.form.name).toBe("renamed"); expect(state.formOpen).toBe(true);
});
it("does not delete a task after changing only its name's letter case", async () => {
  const state = await mount(); state.editTask(task("daily")); state.form.name = "Daily"; vi.mocked(createScheduledTask).mockResolvedValueOnce(task("Daily"));
  await state.saveTask(); expect(createScheduledTask).toHaveBeenCalledWith(expect.objectContaining({ name: "Daily" })); expect(deleteScheduledTask).not.toHaveBeenCalled();
});
it("uses the new saved identity for a second rename of the same draft", async () => {
  const state = await mount(), pending = deferred<ScheduledTask>(); state.editTask(task("daily")); state.form.name = "renamed";
  vi.mocked(createScheduledTask).mockReturnValueOnce(pending.promise);
  const first = state.saveTask(); state.form.name = "renamed-again"; pending.resolve(task("renamed")); await first;
  expect(state.editingName).toBe("renamed"); expect(state.form.name).toBe("renamed-again"); await state.saveTask();
  expect(vi.mocked(deleteScheduledTask).mock.calls).toEqual([["daily"], ["renamed"]]);
});
it("treats a legacy Unicode name as distinct from a new ASCII name", async () => {
  const state = await mount(); state.editTask(task("K")); state.form.name = "K"; await state.saveTask(); expect(deleteScheduledTask).toHaveBeenCalledExactlyOnceWith("K");
});
it("does not hide a failed list refresh behind a successful save notice", async () => {
  const state = await mount(); state.editTask(task("daily")); vi.mocked(getScheduledTasks).mockRejectedValueOnce(new Error("fixture read failed"));
  await state.saveTask(); expect(state.notice).toBe("common.unexpected_error");
});
it("does not apply an older task list after a newer refresh", async () => {
  const state = await mount(), pending = deferred<ScheduledTask[]>(); vi.mocked(getScheduledTasks).mockReturnValueOnce(pending.promise).mockResolvedValueOnce([task("newer")]);
  const first = state.refresh(); await state.refresh(); pending.resolve([task("older")]); await first; expect(state.tasks.map(item => item.name)).toEqual(["newer"]);
});
it("does not dispatch duplicate start requests for a task", async () => {
  const state = await mount(), pending = deferred<void>(); vi.mocked(runScheduledTask).mockReturnValueOnce(pending.promise);
  const first = state.runTask("daily"), second = state.runTask("daily"); pending.resolve(); await Promise.all([first, second]); expect(runScheduledTask).toHaveBeenCalledOnce();
});
it("does not restore output into an unmounted screen", async () => {
  const state = await mount(), pending = deferred<ScheduledTaskOutput>(); vi.mocked(getScheduledTaskOutput).mockReturnValueOnce(pending.promise);
  const read = state.showOutput("daily"); app!.unmount(); app = undefined; pending.resolve(output("late")); await read; expect(state.outputs.daily).toBeUndefined();
});
it("does not delete a task from a confirmation whose screen has closed", async () => {
  const state = await mount(); await state.removeTask("daily"); const action = confirm.mock.calls[0][0].action;
  app!.unmount(); app = undefined; await action(); expect(deleteScheduledTask).not.toHaveBeenCalled();
});
it("does not stop a task from a confirmation whose screen has closed", async () => {
  const state = await mount(); await state.stopRun(task("daily")); const action = confirm.mock.calls[0][0].action;
  app!.unmount(); app = undefined; await action(); expect(stopScheduledTask).not.toHaveBeenCalled();
});
it("still deletes a confirmed task from the current screen", async () => {
  const state = await mount(); await state.removeTask("daily"); await confirm.mock.calls[0][0].action(); expect(deleteScheduledTask).toHaveBeenCalledExactlyOnceWith("daily");
});
