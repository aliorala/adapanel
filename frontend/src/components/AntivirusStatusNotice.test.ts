import { describe, expect, it, vi } from "vitest";
import { createSSRApp, h } from "vue";
import { renderToString } from "vue/server-renderer";
import AntivirusStatusNotice from "./AntivirusStatusNotice.vue";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));

async function render(installed: boolean | null): Promise<string> {
  return renderToString(createSSRApp({ render: () => h(AntivirusStatusNotice, { installed }) }));
}

describe("AntivirusStatusNotice", () => {
  it("shows the no-protection alert when antivirus is not installed", async () => {
    const html = await render(false);
    expect(html).toContain("panel.antivirus.noProtection");
    expect(html).toContain('role="alert"');
  });

  it("shows nothing when antivirus is installed", async () => {
    expect(await render(true)).not.toContain("panel.antivirus.noProtection");
  });

  it("claims nothing while the state is unknown", async () => {
    expect(await render(null)).not.toContain("panel.antivirus.noProtection");
  });
});
