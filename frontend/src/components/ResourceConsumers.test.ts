import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createRenderer, h, nextTick, ref, ssrContextKey, type App, type Component } from "vue";
import ResourceConsumers from "./ResourceConsumers.vue";
import { getResourceConsumers, type ConsumerSource, type ResourceConsumerSnapshot } from "../api/consumers";

vi.mock("../api/consumers", () => ({ getResourceConsumers: vi.fn() }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));

// Vue's real component lifecycle and watchers, with an in-memory rendering host.
type Node = { parent: Node | null; children: Node[]; props: Record<string, unknown> };
const node = (): Node => ({ parent: null, children: [], props: {} });
const renderer = createRenderer<Node, Node>({
  createElement: node, createText: node, createComment: node,
  setText() {}, setElementText() {},
  parentNode: n => n.parent,
  nextSibling: n => n.parent?.children[n.parent.children.indexOf(n) + 1] ?? null,
  patchProp: (n, key, _old, value) => { n.props[key] = value; },
  insert(n, parent, anchor) {
    n.parent = parent;
    const index = anchor ? parent.children.indexOf(anchor) : -1;
    if (index < 0) parent.children.push(n); else parent.children.splice(index, 0, n);
  },
  remove(n) { if (n.parent) n.parent.children.splice(n.parent.children.indexOf(n), 1); }
});
const snapshot = (source: string): ResourceConsumerSnapshot => ({
  source, displayName: source, installed: true, available: true, statusCode: "", capturedAtUtc: new Date().toISOString(),
  totalCount: 0, consumers: []
});
let app: App | undefined;
const pending: { resolve: (value: ResourceConsumerSnapshot) => void; reject: (error: Error) => void }[] = [];
async function flush() { await Promise.resolve(); await Promise.resolve(); await nextTick(); }

beforeEach(() => {
  vi.useFakeTimers();
  vi.stubGlobal("window", { setInterval, clearInterval, setTimeout, clearTimeout });
  vi.stubGlobal("document", { hidden: false, addEventListener: vi.fn(), removeEventListener: vi.fn() });
  pending.length = 0;
  vi.mocked(getResourceConsumers).mockReset().mockImplementation(() =>
    new Promise((resolve, reject) => pending.push({ resolve, reject })));
});
afterEach(() => { app?.unmount(); app = undefined; vi.useRealTimers(); vi.unstubAllGlobals(); });

it.each(["rejection", "late success"])("keeps the new source request locked after old request %s", async outcome => {
  const source = ref<ConsumerSource>("iis");
  const root = node();
  // Vitest's Node transform emits an SSR template; these tests exercise setup/lifecycle.
  const component = { ...ResourceConsumers, render: () => h("section") } as Component;
  app = renderer.createApp({ render: () => h(component, { source: source.value, intervalMs: 3000 }) });
  app.provide(ssrContextKey, {});
  app.mount(root);
  expect(pending).toHaveLength(1);
  const oldSignal = vi.mocked(getResourceConsumers).mock.calls[0][2];
  source.value = "mariadb";
  await nextTick();
  expect(oldSignal?.aborted).toBe(true);
  expect(pending).toHaveLength(3); // New database request plus IIS correlation.
  if (outcome === "rejection") pending[0].reject(new Error("aborted"));
  else pending[0].resolve(snapshot("iis"));
  await flush();
  await vi.advanceTimersByTimeAsync(3000);
  expect(pending).toHaveLength(3);

  pending[1].resolve(snapshot("mariadb"));
  pending[2].resolve(snapshot("iis"));
  await flush();
  await vi.advanceTimersByTimeAsync(3000);
  expect(pending).toHaveLength(4); // Polling resumes after the current request finishes.
  app.unmount(); app = undefined;
  expect(vi.mocked(getResourceConsumers).mock.calls[3][2]?.aborted).toBe(true);
  await vi.advanceTimersByTimeAsync(6000);
  expect(pending).toHaveLength(4);
});
