import type { CacheMetrics } from "../api/cache";

// Redis counters accumulate within one process; a restart invalidates the comparison.
export function redisReadDelta(previous: CacheMetrics | null, current: CacheMetrics): number | null {
  if (!previous || !previous.runId?.trim() || !current.runId?.trim() || previous.runId !== current.runId) return null;
  const beforeTime = Date.parse(previous.measuredAtUtc ?? ""), afterTime = Date.parse(current.measuredAtUtc ?? "");
  const counters = [previous.hits, previous.misses, current.hits, current.misses, previous.uptimeSeconds, current.uptimeSeconds];
  if (!Number.isFinite(beforeTime) || !Number.isFinite(afterTime) || afterTime <= beforeTime
    || counters.some(value => !Number.isSafeInteger(value) || value < 0)
    || current.uptimeSeconds < previous.uptimeSeconds
    || current.hits < previous.hits || current.misses < previous.misses) return null;
  const delta = (current.hits - previous.hits) + (current.misses - previous.misses);
  return Number.isSafeInteger(delta) ? delta : null;
}

export function redisClientsState(clients: CacheMetrics["clients"]): "unknown" | "empty" | "measured" {
  return clients == null ? "unknown" : clients.length === 0 ? "empty" : "measured";
}
