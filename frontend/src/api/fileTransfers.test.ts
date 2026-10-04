import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { apiClient, invalidateSessionRequests } from "./client";
import { copyEntries, moveEntries, renameEntry, uploadFile, type FileWorkspace } from "./files";
import { UserFacingError } from "../utils/apiError";

const source = "sites/a.test/source.txt", destination = "sites/a.test/target";
const workspace = (): FileWorkspace => ({ path: destination, parentPath: "sites/a.test", displayName: "target", physicalPath: "", isDirectory: true, content: "", entries: [] });
const original = apiClient.defaults.adapter;
let body: unknown;
beforeEach(() => {
  invalidateSessionRequests(); vi.stubGlobal("window", { location: { pathname: "/panel" } });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture" } }), { status: 200 })));
  apiClient.defaults.adapter = async config => ({ config, status: 200, statusText: "OK", headers: {}, data: body });
});
afterEach(() => { apiClient.defaults.adapter = original; invalidateSessionRequests(); vi.unstubAllGlobals(); });
const routes = [
  { name: "move", key: "moved", count: "movedCount", run: () => moveEntries([source], destination) },
  { name: "copy", key: "copied", count: "copiedCount", run: () => copyEntries([source], destination) },
  { name: "rename", key: "moved", count: "movedCount", run: () => renameEntry(source, "renamed.txt") }
];
for (const route of routes) {
  const target = route.name === "rename" ? "sites/a.test/renamed.txt" : `${destination}/source.txt`;
  const result = () => ({ [route.count]: 1, failedCount: 0, items: [{ sourcePath: source, targetPath: target, [route.key]: true, errorKey: "" }], workspace: { ...workspace(), path: route.name === "rename" ? "sites/a.test" : destination } });
  it.each(["failed-envelope", "empty-items", "other-source", "other-target", "missing-flag", "wrong-count", "missing-failure-count", "wrong-workspace"])(`${route.name} rejects %s instead of claiming all requested items succeeded`, async kind => {
    const data = result();
    if (kind === "empty-items") { data.items = []; data[route.count] = 0; }
    if (kind === "other-source") data.items[0]!.sourcePath = "sites/b.test/source.txt";
    if (kind === "other-target") data.items[0]!.targetPath = "sites/b.test/target/source.txt";
    if (kind === "missing-flag") delete (data.items[0] as Record<string, unknown>)[route.key];
    if (kind === "wrong-count") data[route.count] = 4;
    if (kind === "missing-failure-count") delete (data as Record<string, unknown>).failedCount;
    if (kind === "wrong-workspace") data.workspace.path = "sites/b.test";
    body = { success: kind !== "failed-envelope", data };
    await expect(route.run()).rejects.toEqual(new UserFacingError("panel.files.resultUnconfirmed"));
  });
  it(`${route.name} preserves a measured per-item failure with no published target`, async () => {
    const data = result(); data[route.count] = 0; data.failedCount = 1; data.items[0]![route.key] = false; data.items[0]!.targetPath = ""; data.items[0]!.errorKey = "files.not_found";
    body = { success: true, data }; await expect(route.run()).resolves.toMatchObject({ failedCount: 1, items: [{ errorKey: "files.not_found" }] });
  });
  it(`${route.name} accepts the measured case-insensitive Windows path identity`, async () => {
    const data = result(); data.items[0]!.sourcePath = source.toUpperCase(); data.items[0]!.targetPath = target.toUpperCase();
    body = { success: true, data }; await expect(route.run()).resolves.toMatchObject({ [route.count]: 1 });
  });
}
it("preserves a successful but incomplete copy warning", async () => {
  body = { success: true, data: { copiedCount: 1, failedCount: 0, items: [{ sourcePath: source, targetPath: `${destination}/source.txt`, copied: true, errorKey: "files.copy_skipped_links" }], workspace: workspace() } };
  await expect(copyEntries([source], destination)).resolves.toMatchObject({ copiedCount: 1, items: [{ errorKey: "files.copy_skipped_links" }] });
});
it.each(["failed-envelope", "missing-path", "different-file"])("upload rejects %s instead of incrementing the success count", async kind => {
  body = { success: kind !== "failed-envelope", data: kind === "missing-path" ? {} : { path: kind === "different-file" ? `${destination}/other.txt` : `${destination}/source.txt` } };
  await expect(uploadFile(destination, new File(["fixture"], "source.txt"))).rejects.toEqual(new UserFacingError("panel.files.resultUnconfirmed"));
});
it("accepts an upload only when the response identifies the submitted file", async () => {
  body = { success: true, data: { path: `${destination}/source.txt` } };
  await expect(uploadFile(destination, new File(["fixture"], "source.txt"))).resolves.toEqual({ path: `${destination}/source.txt` });
});
