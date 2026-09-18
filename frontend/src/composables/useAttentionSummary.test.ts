import { afterEach, expect, it, vi } from "vitest";
import { effectScope } from "vue";
import { emptyAttentionSummary, type AttentionSummary } from "../api/attention";
import { useAttentionSummary } from "./useAttentionSummary";

function deferred() {
  let resolve!: (value: AttentionSummary) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<AttentionSummary>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
const scopes: ReturnType<typeof effectScope>[] = [];
function create(fetcher: (signal: AbortSignal) => Promise<AttentionSummary>) {
  const scope = effectScope(); scopes.push(scope);
  return scope.run(() => useAttentionSummary(() => "read failed", fetcher))!;
}
afterEach(() => { scopes.splice(0).forEach(scope => scope.stop()); });

it("joins drawer opening and repeated background polls to one in-flight request", async () => {
  const request = deferred(); const fetcher = vi.fn(() => request.promise); const state = create(fetcher);
  const background = state.refresh(); const foreground = state.refresh(false); const poll = state.refresh();
  expect(fetcher).toHaveBeenCalledTimes(1);
  expect(state.loading.value).toBe(true);
  request.resolve({ ...emptyAttentionSummary(), checkedAtUtc: "fresh" });
  await Promise.all([background, foreground, poll]);
  expect(state.summary.value?.checkedAtUtc).toBe("fresh");
  expect(state.loading.value).toBe(false);
});
it("shows a joined foreground failure, keeps the previous result, and permits a retry", async () => {
  const request = deferred(); const fetcher = vi.fn().mockResolvedValueOnce(emptyAttentionSummary()).mockReturnValueOnce(request.promise).mockResolvedValueOnce(emptyAttentionSummary());
  const state = create(fetcher); await state.refresh();
  const background = state.refresh(); const foreground = state.refresh(false);
  request.reject(new Error("offline")); await Promise.all([background, foreground]);
  expect(state.error.value).toBe("read failed"); expect(state.summary.value).not.toBeNull();
  await state.refresh(false); expect(state.error.value).toBe(""); expect(fetcher).toHaveBeenCalledTimes(3);
});
it("aborts a previous session and ignores late results without clearing the next session's loading state", async () => {
  const old = deferred(); const next = deferred(); const signals: AbortSignal[] = [];
  const state = create(signal => { signals.push(signal); return signals.length === 1 ? old.promise : next.promise; });
  const oldRun = state.refresh(false); state.reset(); const newRun = state.refresh(false);
  expect(signals[0].aborted).toBe(true);
  old.resolve({ ...emptyAttentionSummary(), checkedAtUtc: "old account" }); await oldRun;
  expect(state.summary.value).toBeNull(); expect(state.loading.value).toBe(true);
  next.resolve({ ...emptyAttentionSummary(), checkedAtUtc: "new account" }); await newRun;
  expect(state.summary.value?.checkedAtUtc).toBe("new account");
});
it("aborts outstanding work when the view is disposed", async () => {
  const request = deferred(); let signal!: AbortSignal;
  const state = create(value => { signal = value; return request.promise; });
  const run = state.refresh(false); scopes[0].stop();
  expect(signal.aborted).toBe(true);
  request.reject(new Error("aborted")); await run;
  expect(state.error.value).toBe(""); expect(state.loading.value).toBe(false);
});
