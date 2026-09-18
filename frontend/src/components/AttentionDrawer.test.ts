import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createSSRApp } from "vue";
import { renderToString } from "vue/server-renderer";
import AttentionDrawer from "./AttentionDrawer.vue";
import { emptyAttentionSummary } from "../api/attention";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../composables/useSubscriptionGate", () => ({ useSubscriptionGate: () => ({ writeGate: () => ({}) }) }));
vi.mock("../stores/auth", () => ({ extractApiMessage: () => "failed" }));
beforeEach(() => vi.stubGlobal("document", {
  activeElement: null, addEventListener() {}, removeEventListener() {},
  documentElement: { dataset: {}, classList: { toggle() {} } }
}));
afterEach(() => vi.unstubAllGlobals());

it.each([
  { failedSources: ["ssl"], loadError: "", loading: false },
  { failedSources: [], loadError: "offline", loading: false },
  { failedSources: [], loadError: "", loading: true }
])("does not claim all-clear with incomplete, failed or pending data: %j", async ({ failedSources, loadError, loading }) => {
  const html = await renderToString(createSSRApp(AttentionDrawer, {
    open: true, summary: { ...emptyAttentionSummary(), failedSources }, loading, loadError
  }));
  expect(html).not.toContain("panel.attention.emptyTitle");
});
it("claims all-clear only after a successful complete scan", async () => {
  const html = await renderToString(createSSRApp(AttentionDrawer, { open: true, summary: emptyAttentionSummary(), loading: false }));
  expect(html).toContain("panel.attention.emptyTitle");
});
