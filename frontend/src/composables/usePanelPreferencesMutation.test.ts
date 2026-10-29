import { expect, it, vi } from "vitest";
import { dnsSyncHasFailure, usePanelPreferencesMutation } from "./usePanelPreferencesMutation";
import type { PanelPreferences, ServerAddressDnsSync } from "../api/panelSettings";

const preferences = { serverAddress: "176.43.253.97" } as PanelPreferences;
const result = (): ServerAddressDnsSync => ({ oldAddress: "84.51.22.239", newAddress: "176.43.253.97", pending: false, zones: [{ siteName: "example", zoneName: "example.test", changedRecords: 3, succeeded: true, error: "" }], error: "" });
function deferred<T>() { let resolve!: (value: T) => void, reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
function harness() {
  let actor = "admin:epoch1", allowed = true;
  const apply = vi.fn(async () => {}), failure = vi.fn(), before = vi.fn();
  return { ...usePanelPreferencesMutation({ actor: () => actor, allowed: () => allowed, before, apply, failure }), apply, failure, before,
    changeActor: () => { actor = "admin:epoch2"; }, disable: () => { allowed = false; } };
}

it("reports complete, partial, interrupted and top-level failure results honestly", () => {
  expect(dnsSyncHasFailure(undefined)).toBe(false);
  expect(dnsSyncHasFailure(result())).toBe(false);
  expect(dnsSyncHasFailure({ ...result(), pending: true })).toBe(true);
  expect(dnsSyncHasFailure({ ...result(), error: "DNS service unavailable" })).toBe(true);
  expect(dnsSyncHasFailure({ ...result(), zones: [{ ...result().zones[0]!, succeeded: false, error: "zone refused" }] })).toBe(true);
});

it("serializes save and retry and applies the returned DNS result", async () => {
  const state = harness(), pending = deferred<PanelPreferences>(), save = vi.fn(() => pending.promise), retry = vi.fn(async () => preferences);
  const running = state.run(save);
  expect(state.busy.value).toBe(true);
  await state.run(retry);
  expect(retry).not.toHaveBeenCalled();
  const updated = { ...preferences, serverAddressDnsSync: result() };
  pending.resolve(updated); await running;
  expect(state.apply).toHaveBeenCalledWith(updated, expect.any(Function));
  expect(state.busy.value).toBe(false);
  await state.run(retry);
  expect(retry).toHaveBeenCalledOnce();
});

it("shows write errors and allows a subsequent retry", async () => {
  const state = harness(), error = new Error("request failed");
  await state.run(async () => { throw error; });
  expect(state.failure).toHaveBeenCalledWith(error);
  expect(state.busy.value).toBe(false);
  await state.run(async () => preferences);
  expect(state.apply).toHaveBeenCalledOnce();
});

it.each(["actor", "unmount"])("discards late success and failure on %s change", async reason => {
  for (const reject of [false, true]) {
    const state = harness(), pending = deferred<PanelPreferences>(), running = state.run(() => pending.promise);
    if (reason === "actor") state.changeActor(); else state.disable();
    if (reject) pending.reject(new Error("late")); else pending.resolve(preferences);
    await running;
    expect(state.apply).not.toHaveBeenCalled(); expect(state.failure).not.toHaveBeenCalled();
  }
});

it("does not write without a loaded administrator session", async () => {
  const state = harness(), write = vi.fn(async () => preferences); state.disable();
  await state.run(write);
  expect(write).not.toHaveBeenCalled(); expect(state.before).not.toHaveBeenCalled();
});
