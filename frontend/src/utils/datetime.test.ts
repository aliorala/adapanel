import { afterEach, expect, it, vi } from "vitest";
import { formatDateTime, parseUtcTimestamp } from "./datetime";

afterEach(() => vi.unstubAllEnvs());
it("shows SQLite UTC timestamps at the same local time as explicit UTC timestamps", () => {
  vi.stubEnv("TZ", "Europe/Istanbul");
  expect(parseUtcTimestamp("2026-09-17T17:49:37").getTime()).toBe(Date.parse("2026-09-17T17:49:37Z"));
  expect(formatDateTime("2026-09-17T17:49:37")).toBe(formatDateTime("2026-09-17T17:49:37Z"));
});
it("preserves explicit offsets and .NET subsecond precision", () => {
  expect(parseUtcTimestamp("2026-09-17T20:49:37+03:00").getTime()).toBe(Date.parse("2026-09-17T17:49:37Z"));
  expect(parseUtcTimestamp("2026-09-17T17:49:37.1234567").getTime()).toBe(Date.parse("2026-09-17T17:49:37.123Z"));
});
it("retains the existing empty and malformed timestamp behavior", () => {
  expect(formatDateTime(null, "—")).toBe("—");
  expect(formatDateTime("not-a-date")).toBe("not-a-date");
});
