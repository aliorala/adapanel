import { afterEach, beforeEach, expect, it, vi } from "vitest";
import * as Vue from "vue";
import { createRenderer, nextTick, ssrContextKey, type App, type RenderFunction } from "vue";
import { parse, compileScript, compileTemplate } from "@vue/compiler-sfc";
import DnsTemplateView from "./DnsTemplateView.vue";
import templateSource from "./DnsTemplateView.vue?raw";
import { getDnsTemplate, updateDnsTemplate, type DnsTemplate } from "../api/dns";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: vi.fn(async () => true) }) }));
vi.mock("../components/FieldHint.vue", () => ({ default: { props: ["label", "text"], render: () => null } }));
vi.mock("../api/dns", async original => ({ ...await original<object>(), getDnsTemplate: vi.fn(), updateDnsTemplate: vi.fn() }));

// Vitest imports SFCs in SSR mode. Compile this same source's client template so
// its real v-model directive executes instead of mounting a renderless SSR SFC.
const filename = "DnsTemplateView.vue";
const descriptor = parse(templateSource, { filename }).descriptor;
const clientTemplate = compileTemplate({ source: descriptor.template!.content, filename,
  id: "dns-txt-input", compilerOptions: { bindingMetadata: compileScript(descriptor, { id: "dns-txt-input" }).bindings } });
if (clientTemplate.errors.length) throw new Error(String(clientTemplate.errors));
const clientCode = clientTemplate.code.replace(/import\s*\{([^}]+)\}\s*from\s*["']vue["']/g,
  (_line, names: string) => `const {${names.replace(/\bas\b/g, ":")}} = Vue;`).replace("export function render", "function render");
const clientRender = new Function("Vue", clientCode + "; return render;")(Vue) as RenderFunction;

// Run the actual compiled template and Vue v-model input listeners. Assigning setup
// state directly would miss the template's former .trim modifier.
class Element {
  children: Element[] = []; parent: Element | null = null;
  props: Record<string, unknown> = {}; value = ""; selected = false; checked = false;
  style: Record<string, unknown> = {};
  listeners: Record<string, ((event: { target: Element }) => void)[]> = {};
  constructor(readonly tagName = "DIV") {}
  getRootNode() { return document; }
  get options() { return this.children.filter(child => child.tagName === "OPTION"); }
  addEventListener(event: string, handler: (event: { target: Element }) => void) { (this.listeners[event] ??= []).push(handler); }
  removeEventListener() {}
  input(value: string) { this.value = value; for (const listener of this.listeners.input ?? []) listener({ target: this }); }
}
type State = { save(): Promise<void>; noticeError: boolean; records: { type: string; value: string }[] };
class FixtureDocument {
  activeElement = null;
  addEventListener() {}
  removeEventListener() {}
}
let app: App | undefined;
const template = (type: string): DnsTemplate => ({ records: [{ type, host: "_proof", value: "previous", ttl: 3600 }], secondaryDnsServers: [], ipMappings: [] });
const all = (node: Element): Element[] => [node, ...node.children.flatMap(all)];
async function flush() { for (let i = 0; i < 16; i++) await nextTick(); }
async function mount(type = "TXT") {
  vi.mocked(getDnsTemplate).mockResolvedValue(template(type));
  const renderer = createRenderer<Element, Element>({
    createElement: tag => new Element(tag.toUpperCase()), createText: () => new Element("#TEXT"), createComment: () => new Element("#COMMENT"),
    insert(child, parent, anchor) {
      if (child.parent) child.parent.children.splice(child.parent.children.indexOf(child), 1);
      child.parent = parent;
      const index = anchor ? parent.children.indexOf(anchor) : -1;
      if (index < 0) parent.children.push(child); else parent.children.splice(index, 0, child);
    },
    remove(child) { child.parent?.children.splice(child.parent.children.indexOf(child), 1); child.parent = null; },
    setText() {}, setElementText() {}, parentNode: node => node.parent, nextSibling: node => node.parent?.children[node.parent.children.indexOf(node) + 1] ?? null,
    patchProp(node, key, _old, value) {
      node.props[key] = value;
      if (key === "value") { node.value = String(value ?? ""); Object.assign(node, { _value: value }); }
    }
  });
  app = renderer.createApp({ ...DnsTemplateView, render: clientRender }); app.provide(ssrContextKey, {});
  const root = new Element();
  const state = (app.mount(root).$ as unknown as { setupState: State }).setupState;
  await flush();
  const input = all(root).find(node => node.tagName === "INPUT" && node.props.placeholder === "{ip}");
  expect(input).toBeDefined();
  return { state, input: input! };
}
beforeEach(() => {
  vi.stubGlobal("Document", FixtureDocument);
  vi.stubGlobal("document", new FixtureDocument());
  vi.mocked(updateDnsTemplate).mockImplementation(async (records, secondaryDnsServers = [], ipMappings) => ({ records, secondaryDnsServers, ipMappings }));
});
afterEach(() => { app?.unmount(); app = undefined; vi.clearAllMocks(); vi.unstubAllGlobals(); });

it.each(["", " ", "  MiXeD+/=;  ", "  {domain}  "])("keeps TXT input characters through v-model and template save: %j", async value => {
  const { state, input } = await mount();
  input.input(value); await flush(); await state.save();
  expect(updateDnsTemplate).toHaveBeenCalledExactlyOnceWith([expect.objectContaining({ type: "TXT", value })], [], [], true);
  expect(state.records[0]?.value).toBe(value);
});

it.each(["A", "CNAME"])("keeps trimming other template %s values in the submitted snapshot", async type => {
  const { state, input } = await mount(type);
  input.input("  example.test  "); await flush(); await state.save();
  expect(updateDnsTemplate).toHaveBeenCalledExactlyOnceWith([expect.objectContaining({ type, value: "example.test" })], [], [], true);
});

it("keeps the full TXT draft and reports an unsuccessful save instead of showing success", async () => {
  const { state, input } = await mount();
  const value = "  unsaved+/=  "; input.input(value); await flush();
  vi.mocked(updateDnsTemplate).mockRejectedValueOnce(new Error("dns.fixture_failed"));
  await state.save();
  expect(state.noticeError).toBe(true); expect(state.records[0]?.value).toBe(value);
});
