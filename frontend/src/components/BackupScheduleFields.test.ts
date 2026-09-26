import { afterEach, expect, it, vi } from "vitest";
import { createRenderer, h, reactive, ssrContextKey, type App, type Component } from "vue";
import BackupScheduleFields from "./BackupScheduleFields.vue";
import { bosProgram, programOzeti } from "../api/schedule";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
let app: App | undefined;
afterEach(() => app?.unmount());

it("enables a second time, accepts user times and disables it without changing the first time", () => {
  const plan = reactive({ ...bosProgram(), mode: "daily", hour: 3, minute: 15 });
  const renderer = createRenderer<object, object>({
    createElement: () => ({}), createText: () => ({}), createComment: () => ({}),
    insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {},
    parentNode: () => null, nextSibling: () => null
  });
  app = renderer.createApp({ ...BackupScheduleFields, render: () => h("section") } as Component, { plan });
  app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: { secondEnabled: boolean; secondTimeText: string } }).setupState;
  expect(state.secondEnabled).toBe(false);
  state.secondEnabled = true;
  expect(state.secondTimeText).toBe("15:15");
  state.secondTimeText = "22:45";
  expect([plan.secondHour, plan.secondMinute]).toEqual([22, 45]);
  state.secondTimeText = "";
  state.secondTimeText = "25:70";
  expect([plan.secondHour, plan.secondMinute]).toEqual([22, 45]);
  state.secondEnabled = false;
  expect(plan.secondHour).toBeNull();
  expect([plan.hour, plan.minute]).toEqual([3, 15]);
});

it("summarizes both daily times and collapses identical times", () => {
  const t = vi.fn((key: string, _args?: Record<string, unknown>) => key);
  const plan = { ...bosProgram(), mode: "daily", secondHour: 15, secondMinute: 30 };
  programOzeti(plan, t);
  expect(t).toHaveBeenLastCalledWith("panel.schedule.summaryTwiceDaily", { time: "03:00", secondTime: "15:30" });
  programOzeti({ ...plan, secondHour: 3, secondMinute: 0 }, t);
  expect(t).toHaveBeenLastCalledWith("panel.schedule.summaryDaily", { time: "03:00" });
  programOzeti({ ...plan, secondHour: null }, t);
  expect(t).toHaveBeenLastCalledWith("panel.schedule.summaryDaily", { time: "03:00" });
});
