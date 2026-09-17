import { afterEach, expect, it, vi } from "vitest";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import SeoView from "./SeoView.vue";
import { getSeoOverview, getSeoLogReport, startSeoScan, type SeoOverview } from "../api/seo";

vi.mock("../api/seo", () => ({ getSeoOverview: vi.fn(), getSeoLogReport: vi.fn(), startSeoScan: vi.fn(), cancelSeoScan: vi.fn(), analyzeSeoLogs: vi.fn(), getSeoReport: vi.fn() }));
vi.mock("../stores/sites", () => ({ useSitesStore: () => ({ selectedSiteName: "first.example", sites: [{name:"first.example"}] }) }));
vi.mock("../stores/auth", () => ({ extractApiMessage: () => "failed" }));
vi.mock("../composables/useSubscriptionGate", () => ({ useSubscriptionGate: () => ({ writeGate: () => ({}) }) }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));

let app: App | undefined;
type State = { siteName: string; overview: SeoOverview; notice: string; scan: () => Promise<void> };
const overview = (host: string, running = false) => ({ host, latest: null, state: { state: running ? "running" : "idle" } } as SeoOverview);
function mount() {
  vi.mocked(getSeoLogReport).mockResolvedValue(null);
  const renderer = createRenderer<object, object>({
    createElement: () => ({}), createText: () => ({}), createComment: () => ({}),
    insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {},
    parentNode: () => null, nextSibling: () => null
  });
  app = renderer.createApp({ ...SeoView, render: () => h("section") } as Component);
  app.provide(ssrContextKey, {});
  return (app.mount({}).$ as unknown as { setupState: State }).setupState;
}
afterEach(() => { app?.unmount(); app = undefined; vi.resetAllMocks(); vi.useRealTimers(); });

it("ignores a previous site's response after changing sites", async () => {
  let oldResponse!: (value: SeoOverview) => void;
  vi.mocked(getSeoOverview).mockImplementationOnce(() => new Promise(resolve => { oldResponse = resolve; }));
  vi.mocked(getSeoOverview).mockResolvedValueOnce(overview("second.example"));
  const state = mount(); await nextTick();
  state.siteName = "second.example"; await nextTick(); await nextTick();
  oldResponse(overview("first.example", true)); await nextTick(); await nextTick();
  expect(state.overview.host).toBe("second.example");
  expect(getSeoLogReport).toHaveBeenCalledExactlyOnceWith("second.example");
});

it("removes the queued notice after a scan finishes", async () => {
  vi.useFakeTimers();
  vi.mocked(getSeoOverview).mockResolvedValueOnce(overview("first.example"))
    .mockResolvedValueOnce(overview("first.example", true)).mockResolvedValue(overview("first.example"));
  const state = mount(); await nextTick(); await nextTick(); await nextTick();
  await state.scan();
  expect(startSeoScan).toHaveBeenCalledTimes(1);
  expect(state.notice).toBe("panel.seo.scanQueued");
  await vi.advanceTimersByTimeAsync(3000);
  expect(state.notice).toBe("");
});

it("does not claim a fast scan is queued after the server already finished it", async () => {
  vi.mocked(getSeoOverview).mockResolvedValue(overview("first.example"));
  const state = mount(); await nextTick(); await nextTick(); await nextTick();
  await state.scan();
  expect(state.notice).toBe("");
});
