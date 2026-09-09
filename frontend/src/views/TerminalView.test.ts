import { afterEach, expect, it, vi } from "vitest";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import TerminalView from "./TerminalView.vue";
import { executeTerminalCommand, type TerminalCommandResult } from "../api/terminal";

vi.mock("../api/terminal", () => ({ executeTerminalCommand: vi.fn() }));
vi.mock("../stores/auth", () => ({ extractApiMessage: () => "terminal.failed" }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));

type State = {
  command: string; workingDirectory: string; running: boolean; notice: string;
  history: { command: string; workingDirectory: string; error: string }[];
  execute: () => Promise<void>;
};
let app: App | undefined;
function mount() {
  // Exercise the actual SFC setup with an in-memory host; Node's SFC template is SSR-only.
  const renderer = createRenderer<object, object>({
    createElement: () => ({}), createText: () => ({}), createComment: () => ({}),
    insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {},
    parentNode: () => null, nextSibling: () => null
  });
  app = renderer.createApp({ ...TerminalView, render: () => h("section") } as Component);
  app.provide(ssrContextKey, {});
  const vm = app.mount({});
  return (vm.$ as unknown as { setupState: State }).setupState;
}
afterEach(() => { app?.unmount(); app = undefined; vi.clearAllMocks(); });
const result: TerminalCommandResult = {
  exitCode: 0, standardOutput: "done", standardError: "", workingDirectory: "C:/data",
  durationMilliseconds: 20, timedOut: false
};

it("preserves the next command and directory typed while the previous command runs", async () => {
  let finish!: (value: TerminalCommandResult) => void;
  vi.mocked(executeTerminalCommand).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
  const state = mount();
  state.command = "  Get-Date  "; state.workingDirectory = "%ProgramData%/AdaPanel";
  const operation = state.execute();
  state.command = "Get-Service"; state.workingDirectory = "C:/next";
  await state.execute(); // Keyboard submission must not start a duplicate command.
  expect(executeTerminalCommand).toHaveBeenCalledExactlyOnceWith("Get-Date", "%ProgramData%/AdaPanel", 30);
  finish(result); await operation; await nextTick();
  expect(state.command).toBe("Get-Service");
  expect(state.workingDirectory).toBe("C:/next");
  expect(state.history[0].command).toBe("Get-Date");
  expect(state.history[0].workingDirectory).toBe("C:/data");
  expect(state.running).toBe(false);
});

it("clears an unchanged submitted command and adopts the resolved directory", async () => {
  vi.mocked(executeTerminalCommand).mockResolvedValueOnce(result);
  const state = mount();
  await state.execute();
  expect(state.command).toBe("");
  expect(state.workingDirectory).toBe("C:/data");
});

it.each(["", "Original diagnostic"])("always reports a timeout alongside stderr %s", async stderr => {
  vi.mocked(executeTerminalCommand).mockResolvedValueOnce({ ...result, standardError: stderr, timedOut: true, exitCode: -1 });
  const state = mount();
  await state.execute();
  expect(state.history[0].error).toContain("panel.terminal.timedOut");
  if (stderr) expect(state.history[0].error).toContain(stderr);
});

it("keeps the command available after an API error", async () => {
  vi.mocked(executeTerminalCommand).mockRejectedValueOnce(new Error("offline"));
  const state = mount();
  await state.execute();
  expect(state.command).toBe("Get-ChildItem");
  expect(state.notice).toBe("terminal.failed");
  expect(state.running).toBe(false);
  expect(state.history).toHaveLength(0);
});
