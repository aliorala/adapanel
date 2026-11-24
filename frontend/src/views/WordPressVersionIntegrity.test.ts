import { describe, expect, it, vi } from "vitest";
import { createSSRApp } from "vue";
import { renderToString } from "@vue/server-renderer";
import WordPressToolkitView from "./WordPressToolkitView.vue";
import toolkitSource from "./AppToolkitView.vue?raw";

vi.mock("../components/WordPressUpdates.vue", () => ({ default: { render: () => null } }));

async function renderVersion(versionMeasured: boolean, versionComparisonMeasured: boolean, updateAvailable = false) {
  const app = createSSRApp({ ...WordPressToolkitView, setup: () => ({
    t: (key: string) => key, siteName: "fixture.test", loading: false, notice: "", writeGate: () => ({}), busy: false,
    wp: { isWordPress: true, siteName: "fixture.test", version: versionMeasured ? "6.9" : "", latestVersion: "6.9",
      versionMeasured, versionComparisonMeasured, updateAvailable, plugins: [], themes: [], security: {}, path: "fixture", maintenanceMode: false },
  }) });
  return renderToString(app);
}

describe("WordPress measured version display", () => {
  it("shows unknown rather than green up-to-date when installed version cannot be measured", async () => {
    const html = await renderVersion(false, false);
    expect(html).toContain("panel.software.installedVersionUnknown");
    expect(html).not.toContain("panel.wp.upToDate");
  });
  it("does not claim up-to-date if the latest version could not be compared", async () => {
    expect(await renderVersion(true, false)).not.toContain("panel.wp.upToDate");
  });
  it("keeps valid current and outdated states working", async () => {
    expect(await renderVersion(true, true)).toContain("panel.wp.upToDate");
    const html = await renderVersion(true, true, true);
    expect(html).toContain("panel.wp.updateAvailable");
    expect(html).not.toContain("panel.wp.upToDate");
  });
  it("also guards the toolkit twin and links only started clones", () => {
    const source = toolkitSource;
    expect(source).toContain('v-else-if="wp.versionComparisonMeasured"');
    expect(source).toContain('v-if="!wp.versionMeasured"');
    expect(source).toContain('v-if="wpCloneResult.siteStarted"');
    expect(source).toContain("!klon.sonuc.databaseIsolated");
  });
});
