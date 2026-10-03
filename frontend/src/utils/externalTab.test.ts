import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { openAsyncReachable } from "./externalTab";
import { extractApiMessage } from "../stores/auth";

let popup: { opener: unknown; closed: boolean; location: { replace: ReturnType<typeof vi.fn> }; close: ReturnType<typeof vi.fn> };
beforeEach(() => {
  popup = { opener: {}, closed: false, location: { replace: vi.fn() }, close: vi.fn() };
  vi.stubGlobal("window", { open: vi.fn(() => popup), location: { origin: "https://panel.test", hostname: "panel.test" } });
});
afterEach(() => vi.unstubAllGlobals());

it("opens synchronously, detaches the opener and waits for the resolved safe link", async () => {
  let finish!: (url: string) => void;
  const resolve = vi.fn(() => new Promise<string>(yes => { finish = yes; }));
  const opening = openAsyncReachable(resolve);
  expect(window.open).toHaveBeenCalledOnce();
  expect(popup.opener).toBeNull();
  expect(popup.location.replace).not.toHaveBeenCalled();
  finish("http://127.0.0.1:8896/tool");
  await opening;
  expect(popup.location.replace).toHaveBeenCalledWith("http://panel.test:8896/tool");
});

it("reports a blocked popup without creating an SSO token or provisioning tools", async () => {
  vi.mocked(window.open).mockReturnValue(null);
  const resolve = vi.fn();
  const error = await openAsyncReachable(resolve).catch(error => error);
  expect(extractApiMessage(error)).toBe("panel.common.popupBlocked");
  expect(resolve).not.toHaveBeenCalled();
});

it.each(["javascript:alert(1)", "data:text/html,hello", ""])("rejects unsafe or empty links: %s", async url => {
  await expect(openAsyncReachable(async () => url)).rejects.toMatchObject({ messageKey: "panel.common.invalidLink" });
  expect(popup.location.replace).not.toHaveBeenCalled();
  expect(popup.close).toHaveBeenCalledOnce();
});

it("does not report success when the user closes the reserved tab", async () => {
  await expect(openAsyncReachable(async () => { popup.closed = true; return "https://node.test/panel"; }))
    .rejects.toMatchObject({ messageKey: "panel.common.popupClosed" });
  expect(popup.location.replace).not.toHaveBeenCalled();
});

it("closes the blank tab and preserves an API failure", async () => {
  const failure = new Error("server unavailable");
  await expect(openAsyncReachable(async () => { throw failure; })).rejects.toBe(failure);
  expect(popup.close).toHaveBeenCalledOnce();
});
