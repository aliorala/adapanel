import { afterEach, expect, it, vi } from "vitest";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import MigrationView from "./MigrationView.vue";
import { migrateSiteStream } from "../api/migration";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../api/migration", async original => ({
  ...await original<object>(), migrateSiteStream: vi.fn(),
  getMigrationRunningJobs: vi.fn().mockResolvedValue({ running: [], interrupted: [] })
}));
let app: App | undefined;
afterEach(() => { app?.unmount(); vi.clearAllMocks(); });

it("ends unresolved step indicators when the server reports a failed completion", async () => {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...MigrationView, render: () => h("section") } as Component);
  app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: {
    form: { siteName: string }; run(): Promise<void>; busy: boolean; noticeError: boolean;
    liveSteps: { step: string; status: string }[];
  } }).setupState;
  await nextTick();
  state.form.siteName = "a.test";
  vi.mocked(migrateSiteStream).mockImplementation(async (_payload, onEvent) => {
    onEvent({ phase: "plan", steps: ["site", "files", "database"] });
    onEvent({ phase: "result", step: "site", success: true });
    onEvent({ phase: "start", step: "files" });
    onEvent({ phase: "done", overallSuccess: false });
  });
  await state.run();
  expect(state.busy).toBe(false);
  expect(state.noticeError).toBe(true);
  expect(state.liveSteps.map(step => step.status)).toEqual(["ok", "unknown", "unknown"]);
});
