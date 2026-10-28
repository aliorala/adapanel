import mailSource from "./MailView.vue?raw";
import { compile } from "@vue/compiler-dom";
import * as Vue from "vue";
import { afterEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import MailView from "./MailView.vue";
import { getMailWorkspace, openWebmail } from "../api/mail";
import { useSitesStore } from "../stores/sites";
import { useSubscriptionStore } from "../stores/subscription";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key, locale: Vue.ref("en") }) }));
vi.mock("../api/mail", async original => ({
  ...await original<typeof import("../api/mail")>(), getMailWorkspace: vi.fn(), openWebmail: vi.fn(),
  getMailPublicBaseUrl: vi.fn(async () => ""), getMailClientSettings: vi.fn(async () => null),
  getOutboundLimit: vi.fn(async () => null),
}));

type Node = { tag: string; text: string; props: Record<string, unknown>; children: Node[]; parent?: Node };
const node = (tag = "", text = ""): Node => ({ tag, text, props: {}, children: [] });
const template = mailSource.slice(mailSource.indexOf("<template>") + "<template>".length, mailSource.lastIndexOf("</template>"));
// Compile the actual SFC template for this client renderer; Vite's test transform supplies SSR markup only.
const compiledRender = new Function("Vue", compile(template, { mode: "function", isCustomElement: tag => /^[A-Z]/.test(tag) }).code)({
  ...Vue, vModelText: {}, vModelCheckbox: {}, vModelSelect: {},
});
function render(_context: unknown, cache: unknown[]) {
  return compiledRender((Vue.getCurrentInstance() as unknown as { setupState: unknown }).setupState, cache);
}
const apps: Vue.App[] = [];
afterEach(() => { apps.splice(0).forEach(app => app.unmount()); vi.clearAllMocks(); vi.unstubAllGlobals(); });
const settle = async () => { for (let i = 0; i < 30; i++) await Vue.nextTick(); };
const descendants = (root: Node): Node[] => [root, ...root.children.flatMap(descendants)];
const content = (root: Node): string => root.text + root.children.map(content).join("");

async function mount() {
  vi.mocked(getMailWorkspace).mockResolvedValue({ installed: true, configured: true, selectedDomain: "travianpanel.com",
    domains: [{ name: "travianpanel.com", accountCount: 1, maxSizeMB: 0, active: true, allocatedMB: 0 }],
    accounts: [{ address: "info@travianpanel.com", maxSizeMB: 1024, currentSizeMB: 0, active: true }], aliases: [], lists: [] });
  const pinia = createPinia();
  useSitesStore(pinia).sites = [{ name: "travianpanel.com", bindings: [] }] as never;
  const renderer = Vue.createRenderer<Node, Node>({
    createElement: tag => node(tag), createText: text => node("text", text), createComment: () => node(),
    insert(child, parent, anchor) { child.parent = parent; const index = anchor ? parent.children.indexOf(anchor) : -1;
      if (index < 0) parent.children.push(child); else parent.children.splice(index, 0, child); },
    remove(child) { const children = child.parent?.children; if (children) children.splice(children.indexOf(child), 1); },
    setText: (element, text) => { element.text = text; }, setElementText: (element, text) => { element.text = text; },
    patchProp: (element, key, _previous, value) => { element.props[key] = value; },
    parentNode: element => element.parent ?? null, nextSibling: () => null,
  });
  const app = renderer.createApp({ ...MailView, render } as Vue.Component).use(pinia);
  app.provide(Vue.ssrContextKey, {});
  apps.push(app);
  const root = node();
  const state = (app.mount(root).$ as unknown as { setupState: Record<string, unknown> }).setupState;
  await settle();
  const row = descendants(root).find(element => element.props.class === "data-row mail-row")!;
  const button = descendants(row).find(element => element.tag === "button" && content(element).includes("panel.mail.webmailOpen"))!;
  return { root, state, button, subscription: useSubscriptionStore(pinia) };
}

function popup() {
  const tab = { opener: {}, closed: false, document: { write: vi.fn(), close: vi.fn(), open: vi.fn(), body: {} }, location: { replace: vi.fn() } };
  const open = vi.fn(() => tab);
  vi.stubGlobal("window", { open, location: { origin: "https://panel.test", hostname: "panel.test" } });
  return { tab, open };
}

it("renders a mailbox action, opens the existing endpoint once, and keeps the general action working across domain changes", async () => {
  const { root, state, button } = await mount();
  expect(button).toBeDefined();
  expect(button.props.disabled).toBe(false);
  let resolve!: (value: { url: string }) => void;
  vi.mocked(openWebmail).mockReturnValueOnce(new Promise(done => { resolve = done; }) as ReturnType<typeof openWebmail>);
  const { tab, open } = popup();
  const click = button.props.onClick as () => Promise<void>;
  const opening = click();
  expect(open).toHaveBeenCalledWith("", "_blank");
  await click();
  expect(openWebmail).toHaveBeenCalledExactlyOnceWith();
  state.selectedDomain = "other.test";
  resolve({ url: "https://webmail.travianpanel.com/" });
  await opening;
  expect(tab.location.replace).toHaveBeenCalledExactlyOnceWith("https://webmail.travianpanel.com/");
  const general = descendants(root).find(element => element.tag === "button" && element.props.class === "compact" && content(element).includes("panel.mail.webmailOpen"))!;
  vi.mocked(openWebmail).mockResolvedValue({ url: "https://webmail.travianpanel.com/" } as never);
  await (general.props.onClick as () => Promise<void>)();
  expect(openWebmail).toHaveBeenCalledTimes(2);
});

it("blocks the row action during a write or a suspended subscription", async () => {
  const { state, button, subscription } = await mount();
  const { open } = popup();
  state.busy = true;
  await settle();
  expect(button.props.disabled).toBe(true);
  await (button.props.onClick as () => Promise<void>)();
  state.busy = false;
  subscription.usage = { isSuspended: true } as never;
  await settle();
  expect(button.props.disabled).toBe(true);
  expect(button.props["aria-disabled"]).toBe("true");
  await (button.props.onClick as () => Promise<void>)();
  expect(open).not.toHaveBeenCalled();
  expect(openWebmail).not.toHaveBeenCalled();
});

it.each(["blocked", "invalid", "failed"])("reports a %s opening without navigating to an invalid page", async failure => {
  const { state, button } = await mount();
  const { tab, open } = popup();
  if (failure === "blocked") open.mockReturnValue(null as never);
  else if (failure === "failed") vi.mocked(openWebmail).mockRejectedValueOnce(new Error("unavailable"));
  else vi.mocked(openWebmail).mockResolvedValueOnce({ url: "javascript:alert(1)" } as never);
  await (button.props.onClick as () => Promise<void>)();
  expect(tab.location.replace).not.toHaveBeenCalled();
  expect(state.noticeError).toBe(true);
  expect(state.webmailOpening).toBe(false);
  if (failure === "blocked") expect(openWebmail).not.toHaveBeenCalled();
  else expect(tab.document.write).toHaveBeenLastCalledWith(expect.stringContaining("panel.mail.webmailFailedTitle"));
});
