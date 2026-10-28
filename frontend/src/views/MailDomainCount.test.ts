import { afterEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { createRenderer, createSSRApp, h, nextTick, ref, ssrContextKey, type App, type Component } from "vue";
import { renderToString } from "@vue/server-renderer";
import MailView from "./MailView.vue";
import { getMailWorkspace, type MailWorkspace } from "../api/mail";
import { useSitesStore } from "../stores/sites";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key, locale: ref("en") }) }));
vi.mock("../api/mail", async original => ({
  ...await original<typeof import("../api/mail")>(), getMailWorkspace: vi.fn(),
  getMailPublicBaseUrl: vi.fn(async () => ""),
}));
let app: App | undefined;
afterEach(() => { app?.unmount(); app = undefined; vi.clearAllMocks(); });

async function render(domains: MailWorkspace["domains"]) {
  vi.mocked(getMailWorkspace).mockResolvedValue({ installed: true, configured: true,
    selectedDomain: "", domains, accounts: [], accountsMeasured: false, aliases: [], lists: [] });
  const pinia = createPinia();
  // Avoid unrelated site discovery/network access; this screen receives its own fixtures only.
  useSitesStore(pinia).sites = [{ name: "travianpanel.com", bindings: [] }] as never;
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}),
    insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...MailView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: Record<string, unknown> }).setupState;
  for (let i = 0; i < 30; i++) await nextTick();
  // Render the actual compiled template with the already loaded state. Vitest compiles
  // SFC templates for SSR, so a bare custom client renderer cannot test their markup.
  const html = await renderToString(createSSRApp({ ...MailView, setup: () => state }).use(pinia));
  return [...html.matchAll(/<div class="data-row maildomain-row"[^>]*>(.*?)<\/div>/gs)]
    .map(match => match[1]!.replace(/<[^>]+>/g, " "));
}

const domain = { name: "travianpanel.com", active: true, accountCount: 1,
  accountCountMeasured: true, maxSizeMB: 0, allocatedMB: 0 };

it("renders the actual measured mailbox count on the general list without opening a domain", async () => {
  const rows = await render([domain, { ...domain, name: "empty.test", accountCount: 0 }]);
  expect(rows.find(row => row.includes("travianpanel.com"))).toMatch(/travianpanel\.com\s+1\s/);
  expect(rows.find(row => row.includes("empty.test"))).toMatch(/empty\.test\s+0\s/);
  expect(getMailWorkspace).toHaveBeenCalledExactlyOnceWith(undefined);
});

it.each([false, undefined])("does not present unmeasured or legacy counts as zero (%s)", async measured => {
  const rows = await render([{ ...domain, accountCount: 0, accountCountMeasured: measured }]);
  const row = rows.find(item => item.includes("travianpanel.com"));
  expect(row).toContain("panel.mail.mailboxCountUnmeasured");
  expect(row).not.toMatch(/travianpanel\.com\s+0\s/);
});

it.each([-1, NaN, 1.5])("does not render an invalid count as measured: %s", async count => {
  const rows = await render([{ ...domain, accountCount: count }]);
  expect(rows.find(item => item.includes("travianpanel.com"))).toContain("panel.mail.mailboxCountUnmeasured");
});
