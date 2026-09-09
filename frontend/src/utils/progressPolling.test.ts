import { afterEach, describe, expect, it, vi } from "vitest";
import { pollProgress } from "./progressPolling";
afterEach(() => vi.useRealTimers());
describe("installation connectivity", () => {
  it("pauses after three connection failures and preserves the last running status", async () => {
    vi.useFakeTimers();
    const receive = vi.fn();
    const connection = vi.fn();
    const fetch = vi.fn().mockResolvedValueOnce({ running: true }).mockRejectedValue(new Error("offline"));
    const task = pollProgress({ signal: new AbortController().signal, fetch, receive, connection, complete: () => false });
    await vi.runAllTimersAsync();
    expect(await task).toBe("paused");
    expect(receive).toHaveBeenCalledExactlyOnceWith({ running: true });
    expect(fetch).toHaveBeenCalledTimes(4);
    expect(connection).toHaveBeenLastCalledWith(3);
  });
  it("recovers from a transient error and stops at completion", async () => {
    vi.useFakeTimers();
    const receive = vi.fn();
    const connection = vi.fn();
    const task = pollProgress({ signal: new AbortController().signal, fetch: vi.fn().mockRejectedValueOnce(new Error("restart")).mockResolvedValue({ completed: true }), receive, connection, complete: (value) => value.completed });
    await vi.runAllTimersAsync();
    expect(await task).toBe("complete");
    expect(connection).toHaveBeenLastCalledWith(0);
  });
  it("aborts a pending delay without a further request", async () => {
    vi.useFakeTimers();
    const controller = new AbortController();
    const fetch = vi.fn().mockResolvedValue({ running: true });
    const task = pollProgress({ signal: controller.signal, fetch, receive: vi.fn(), connection: vi.fn(), complete: () => false });
    await vi.advanceTimersByTimeAsync(1);
    controller.abort();
    expect(await task).toBe("aborted");
    expect(fetch).toHaveBeenCalledOnce();
    expect(vi.getTimerCount()).toBe(0);
  });
});
