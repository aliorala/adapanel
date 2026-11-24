import { describe, expect, it } from "vitest";
import type { CacheMetrics } from "../api/cache";
import { redisClientsState, redisReadDelta } from "./redisUsage";

const metric = (changes: Partial<CacheMetrics> = {}): CacheMetrics => ({ version: "7", usedMemory: "1M", keys: 10, connectedClients: 2, hits: 20, misses: 5, uptimeSeconds: 100, runId: "process-a", measuredAtUtc: "2026-10-30T08:00:00Z", ...changes });
describe("Redis usage evidence", () => {
  it("needs two samples, counts hits and misses, and allows a measured zero", () => {
    expect(redisReadDelta(null, metric())).toBeNull();
    expect(redisReadDelta(metric(), metric({ hits: 23, misses: 7, uptimeSeconds: 102, measuredAtUtc: "2026-10-30T08:01:00Z" }))).toBe(5);
    expect(redisReadDelta(metric(), metric({ measuredAtUtc: "2026-10-30T08:01:00Z" }))).toBe(0);
  });
  it.each([{ runId: "process-b" }, { uptimeSeconds: 99 }, { hits: 19 }, { misses: 4 }, { runId: undefined }, { runId: null }, { measuredAtUtc: undefined }, { measuredAtUtc: "invalid" }, { hits: Number.NaN }, { misses: -1 }, { hits: Number.MAX_SAFE_INTEGER + 1 }])("resets incomparable samples: %j", changes => {
    expect(redisReadDelta(metric(), metric({ measuredAtUtc: "2026-10-30T08:01:00Z", ...changes }))).toBeNull();
  });
  it("rejects repeated or older timestamps and an overflowing delta", () => {
    expect(redisReadDelta(metric(), metric())).toBeNull();
    expect(redisReadDelta(metric(), metric({ measuredAtUtc: "2026-10-29T08:00:00Z" }))).toBeNull();
    expect(redisReadDelta(metric({ hits: 0, misses: 0 }), metric({ hits: Number.MAX_SAFE_INTEGER, misses: 1, measuredAtUtc: "2026-10-30T08:01:00Z" }))).toBeNull();
  });
  it("preserves a small read delta when cumulative counters approach the safe integer limit", () => {
    const previous = metric({ hits: 0, misses: Number.MAX_SAFE_INTEGER });
    const current = metric({ hits: 2, misses: Number.MAX_SAFE_INTEGER, measuredAtUtc: "2026-10-30T08:01:00Z" });
    expect(redisReadDelta(previous, current)).toBe(2);
  });
  it("does not infer reads from stored keys or connections", () => {
    expect(redisReadDelta(metric(), metric({ keys: 200, connectedClients: 15, measuredAtUtc: "2026-10-30T08:01:00Z" }))).toBe(0);
  });
  it("distinguishes an unknown client list from a measured empty list", () => {
    expect(redisClientsState(undefined)).toBe("unknown");
    expect(redisClientsState(null)).toBe("unknown");
    expect(redisClientsState([])).toBe("empty");
    expect(redisClientsState([{ name: "", databaseIndex: 0, idleSeconds: 5 }])).toBe("measured");
  });
});
