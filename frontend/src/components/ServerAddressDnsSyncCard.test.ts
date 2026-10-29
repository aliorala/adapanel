import { afterEach, expect, it, vi } from "vitest";
import { createRenderer, createSSRApp, h, nextTick, ref, ssrContextKey, type App, type Component } from "vue";
import { renderToString } from "vue/server-renderer";
import ServerAddressDnsSyncCard from "./ServerAddressDnsSyncCard.vue";
import type { ServerAddressDnsSync } from "../api/panelSettings";
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
const result = (): ServerAddressDnsSync => ({ oldAddress: "84.51.22.239", newAddress: "176.43.253.97", pending: false, zones: [{ siteName: "example", zoneName: "example.test", changedRecords: 2, succeeded: true, error: "" }], error: "" });
const render = (value?: ServerAddressDnsSync, busy = false) => renderToString(createSSRApp(ServerAddressDnsSyncCard, { result: value, currentAddress: "176.43.253.97", busy }));
let app: App | undefined;
afterEach(() => { app?.unmount(); app = undefined; vi.useRealTimers(); });

async function mount(value = result(), version = 0) {
  const state = ref(value), feedback = ref(version);
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  // Vitest's Node transform supplies ssrRender only; exercise the real setup
  // lifecycle here and use the SSR tests above for template visibility.
  const componentType = { ...ServerAddressDnsSyncCard, render: () => h("section") } as Component;
  app = renderer.createApp({ render: () => h(componentType, { result: state.value, currentAddress: "176.43.253.97", busy: false, feedbackVersion: feedback.value }) });
  app.provide(ssrContextKey, {});
  const root = app.mount({});
  const component = () => (root.$ as unknown as { subTree: { component: { setupState: { visible: boolean } } } }).subTree.component;
  const visible = () => component().setupState.visible;
  await nextTick();
  return { state, feedback, visible };
}

it("does not replay stored success or show an old-address form when settings opens", async () => {
  expect(await render(result())).not.toContain("dns-address-sync");
  expect(await render()).not.toContain("dnsSync.oldAddress");
  const view = await mount(result(), 4);
  expect(view.visible()).toBe(false);
});
it("shows a newly completed operation once and hides it after fifteen seconds", async () => {
  vi.useFakeTimers();
  const view = await mount();
  view.feedback.value++; await nextTick(); await nextTick();
  expect(view.visible()).toBe(true);
  await vi.advanceTimersByTimeAsync(14_999); expect(view.visible()).toBe(true);
  await vi.advanceTimersByTimeAsync(1); expect(view.visible()).toBe(false);
  view.state.value = result(); await nextTick();
  expect(view.visible()).toBe(false);
});
it("resets the timer for a new operation and clears it on unmount", async () => {
  vi.useFakeTimers(); const view = await mount();
  view.feedback.value++; await nextTick();
  await vi.advanceTimersByTimeAsync(10_000);
  view.feedback.value++; await nextTick();
  await vi.advanceTimersByTimeAsync(5_000); expect(view.visible()).toBe(true);
  app!.unmount(); app = undefined; expect(vi.getTimerCount()).toBe(0);
});
it("keeps partial or pending migrations visible and offers retry", async () => {
  const partial = { ...result(), zones: [{ ...result().zones[0]!, succeeded: false, error: "DNS unavailable" }] };
  const html = await render(partial);
  expect(html).toContain("dnsSync.partial"); expect(html).toContain("DNS unavailable");
  expect(html).toContain("dnsSync.oldAddress"); expect(html).not.toContain("dnsSync.success");
  const pending = await render({ ...result(), pending: true }, true);
  expect(pending).toContain("dnsSync.pending"); expect(pending).toContain('aria-busy="true"'); expect(pending).toContain("disabled");
  vi.useFakeTimers(); const view = await mount(partial);
  view.feedback.value++; await nextTick();
  await vi.advanceTimersByTimeAsync(60_000); expect(view.visible()).toBe(true);
});
it("does not hide a new failure when an earlier success timer expires", async () => {
  vi.useFakeTimers(); const view = await mount();
  view.feedback.value++; await nextTick();
  view.state.value = { ...result(), pending: true }; await nextTick();
  await vi.advanceTimersByTimeAsync(15_000); expect(view.visible()).toBe(true);
});
it("keeps skipped zones and failed FTP visible without claiming success", async () => {
  const skipped = await render({ ...result(), zones: [{ ...result().zones[0]!, succeeded: false, skipped: true }] });
  expect(skipped).toContain("dnsSync.zoneSkipped"); expect(skipped).not.toContain("dnsSync.zoneSuccess");
  const failed = await render({ ...result(), ftp: { succeeded: false, skipped: false, changed: false, error: "Readback failed", currentAddress: "176.43.253.97" } });
  expect(failed).toContain("dnsSync.ftpFailed"); expect(failed).toContain("Readback failed"); expect(failed).not.toContain("dnsSync.success");
});
