import { afterEach, expect, it, vi } from "vitest";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import ClusterView from "./ClusterView.vue";
import type { ClusterNodeStatus } from "../api/cluster";
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../api/cluster", async original => ({ ...await original<object>(), getClusterOverview: vi.fn(async () => ({ nodes: [], totalSites: 0, reachableCount: 0, unreachableCount: 0, generatedAtUtc: "2026-10-04T00:00:00Z" })) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: vi.fn() }) }));
let app: App | undefined;
afterEach(() => { app?.unmount(); app = undefined; });
async function mount() {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...ClusterView, render: () => h("section") } as Component); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: { cpuPercent(row: ClusterNodeStatus): number | null; memoryPercent(row: ClusterNodeStatus): number | null; enDoluDisk(row: ClusterNodeStatus): number | null } }).setupState;
  await nextTick(); return state;
}
function row(measured?: boolean, percent = 0): ClusterNodeStatus {
  return { reachable: true, info: { overview: { cpuUsagePercent: percent, memoryUsagePercent: percent, cpuMeasured: measured, memoryMeasured: measured, disksMeasured: measured,
    disks: [{ name: "C:", usagePercent: percent, measured }] } } } as unknown as ClusterNodeStatus;
}
it.each([undefined, false])("does not turn legacy or unmeasured remote disk placeholders into zero (%s)", async measured => {
  const state = await mount(); expect(state.enDoluDisk(row(measured))).toBeNull();
});
it.each([undefined, false])("does not turn unknown remote CPU/RAM into zero (%s)", async measured => {
  const state = await mount(); expect(state.cpuPercent(row(measured))).toBeNull(); expect(state.memoryPercent(row(measured))).toBeNull();
});
it("keeps the aggregate disk unknown when one disk measurement is missing", async () => {
  const state = await mount(), value = row(true, 10); value.info!.overview.disks.push({ name: "D:", usagePercent: 0, measured: false } as never);
  expect(state.enDoluDisk(value)).toBeNull();
});
it("preserves a genuinely measured zero for all remote metrics", async () => {
  const state = await mount(); expect(state.cpuPercent(row(true))).toBe(0); expect(state.memoryPercent(row(true))).toBe(0); expect(state.enDoluDisk(row(true))).toBe(0);
});
