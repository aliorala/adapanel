import { afterEach, expect, it, vi } from "vitest";
import { createRenderer, h, ssrContextKey, type App, type Component } from "vue";
import DashboardOverviewView from "./DashboardOverviewView.vue";
import { emptyAttentionSummary, type AttentionItem } from "../api/attention";

vi.mock("../api/system", () => ({ getServerIpAddresses: vi.fn(async () => []) }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
let app: App | undefined;
function mount(items: AttentionItem[]) {
  const renderer = createRenderer<object, object>({
    createElement: () => ({}), createText: () => ({}), createComment: () => ({}),
    insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {},
    parentNode: () => null, nextSibling: () => null
  });
  app = renderer.createApp({ ...DashboardOverviewView, render: () => h("section") } as Component, {
    overview: null, loading: false, sitesCount: 0, totalBindingCount: 0, ftpUserCount: 0, databaseCount: 0,
    securityStatusLabel: "", securePath: "panel", auditItems: [], allowedSections: [], panelUpdate: null,
    attention: { ...emptyAttentionSummary(), items }
  });
  app.provide(ssrContextKey, {});
  return (app.mount({}).$ as unknown as { setupState: { pendingUpdateCount: number; pendingUpdateTarget: string } }).setupState;
}
const item = (id: string, section: string, affectedCount?: number): AttentionItem => ({
  id, section, affectedCount, severity: "warning", titleKey: "test", descriptionKey: "", detail: "",
  actionEndpoint: "", actionMethod: "", actionPayload: {}
});
afterEach(() => { app?.unmount(); app = undefined; });

it("counts the components represented by a grouped finding, keeping its software destination", () => {
  const state = mount([item("component-updates", "software", 6)]);
  expect(state.pendingUpdateCount).toBe(6);
  expect(state.pendingUpdateTarget).toBe("software");
});
it("adds the panel update and ignores unrelated warnings", () => {
  const state = mount([item("component-updates", "software", 2), item("panel-update", "updates"), item("disk", "monitor", 3)]);
  expect(state.pendingUpdateCount).toBe(3);
  expect(state.pendingUpdateTarget).toBe("attention");
});
it("has no pending update when the resolved component finding disappears", () => {
  expect(mount([item("disk", "monitor")]).pendingUpdateCount).toBe(0);
});
