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

const handoffToken = "a".repeat(64);
function popup() {
  const input = { type: "", name: "", value: "" };
  const form = { method: "", action: "", appendChild: vi.fn(), submit: vi.fn() };
  const createElement = vi.fn((tag: string) => tag === "form" ? form : input);
  const tab = { opener: {}, closed: false, document: { write: vi.fn(), close: vi.fn(), open: vi.fn(), createElement, body: { appendChild: vi.fn() } }, location: { replace: vi.fn() } };
  const open = vi.fn(() => tab);
  vi.stubGlobal("window", { open, location: { origin: "https://panel.test", hostname: "panel.test" } });
  return { tab, open, form, input };
}

it("renders the mailbox action and delivers its captured address and opaque handoff using a native POST once", async () => {
  const { button } = await mount();
  expect(button).toBeDefined();
  expect(button.props.disabled).toBe(false);
  let resolve!: (value: { url: string; handoffToken: string }) => void;
  vi.mocked(openWebmail).mockReturnValueOnce(new Promise(done => { resolve = done; }) as ReturnType<typeof openWebmail>);
  const { tab, open, form, input } = popup();
  const click = button.props.onClick as () => Promise<void>;
  const opening = click();
  expect(open).toHaveBeenCalledWith("", "_blank");
  await click();
  expect(openWebmail).toHaveBeenCalledExactlyOnceWith({ domain: "travianpanel.com", address: "info@travianpanel.com" });
  resolve({ url: "https://webmail.travianpanel.com/", handoffToken });
  await opening;
  expect(tab.opener).toBeNull();
  expect(tab.document.write).toHaveBeenCalledWith(expect.stringContaining('<meta name="referrer" content="strict-origin">'));
  expect(form.method).toBe("POST");
  expect(form.action).toBe("https://panel.test/api/v1/mail/webmail/handoff");
  expect(input).toEqual({ type: "hidden", name: "adapanel_sso", value: handoffToken });
  expect(form.appendChild).toHaveBeenCalledExactlyOnceWith(input);
  expect(tab.document.body.appendChild).toHaveBeenCalledExactlyOnceWith(form);
  expect(form.submit).toHaveBeenCalledExactlyOnceWith();
  expect(tab.document.write.mock.calls.flat().join("")).not.toContain(handoffToken);
  expect(tab.location.replace).not.toHaveBeenCalled();
});

it("opens the selected domain login without requesting a mailbox handoff from the general action", async () => {
  const { root } = await mount();
  const { tab, form } = popup();
  const general = descendants(root).find(element => element.tag === "button" && element.props.class === "compact" && content(element).includes("panel.mail.webmailOpen"))!;
  vi.mocked(openWebmail).mockResolvedValue({ url: "https://webmail.travianpanel.com/" } as never);
  await (general.props.onClick as () => Promise<void>)();
  expect(openWebmail).toHaveBeenCalledExactlyOnceWith({ domain: "travianpanel.com" });
  expect(tab.location.replace).toHaveBeenCalledExactlyOnceWith("https://webmail.travianpanel.com/");
  expect(form.submit).not.toHaveBeenCalled();
});

it.each(["other.test", "travianpanel.com"])("rejects an issued handoff after a domain change (ending at %s)", async ending => {
  const { state, button } = await mount();
  const { tab, form } = popup();
  let resolve!: (value: { url: string; handoffToken: string }) => void;
  vi.mocked(openWebmail).mockReturnValueOnce(new Promise(done => { resolve = done; }));
  const opening = (button.props.onClick as () => Promise<void>)();
  state.selectedDomain = "other.test";
  state.selectedDomain = ending;
  resolve({ url: "https://webmail.travianpanel.com/", handoffToken });
  await opening;
  expect(form.submit).not.toHaveBeenCalled();
  expect(tab.location.replace).not.toHaveBeenCalled();
  expect(state.noticeError).toBe(true);
});

it.each([
  { url: "https://webmail.adapanel.host/", handoffToken },
  { url: "http://webmail.travianpanel.com/", handoffToken },
  { url: "/", handoffToken },
  { url: "https://webmail.travianpanel.com.evil.test/", handoffToken },
  { url: "https://user@webmail.travianpanel.com/", handoffToken },
  { url: "https://webmail.travianpanel.com/?token=secret", handoffToken },
  { url: "https://webmail.travianpanel.com/" },
  { url: "https://webmail.travianpanel.com/", handoffToken: "<unsafe>" },
])("rejects an unsafe target or absent/invalid mailbox handoff: %j", async result => {
  const { state, button } = await mount();
  const { form, tab } = popup();
  vi.mocked(openWebmail).mockResolvedValueOnce(result);
  await (button.props.onClick as () => Promise<void>)();
  expect(form.submit).not.toHaveBeenCalled();
  expect(tab.location.replace).not.toHaveBeenCalled();
  expect(state.noticeError).toBe(true);
});

it("rejects a mailbox outside the selected domain before requesting a handoff", async () => {
  const { state } = await mount();
  const { open } = popup();
  await (state.openWebmail as (address: string) => Promise<void>)("info@other.test");
  expect(openWebmail).not.toHaveBeenCalled();
  expect(open).not.toHaveBeenCalled();
  expect(state.noticeError).toBe(true);
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
