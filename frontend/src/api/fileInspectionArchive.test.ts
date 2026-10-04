import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { apiClient, invalidateSessionRequests } from "./client";
import { compressPath, extractPath, getDirectorySize, searchFiles, type FileEntry, type FileWorkspace } from "./files";
import { UserFacingError } from "../utils/apiError";

const root = "sites/a.test", directory = `${root}/folder`, archive = `${root}/folder.zip`;
const row = (path = `${directory}/özel.txt`): FileEntry => ({ name: path.split("/").pop()!, path, isDirectory: false, size: 4, extension: ".txt", lastModifiedUtc: null });
const workspace = (path = root): FileWorkspace => ({ path, parentPath: "sites", displayName: path, physicalPath: "", content: "", isDirectory: true, entries: [row(`${path}/folder.zip`)] });
const size = () => ({ path: directory, totalBytes: 20, fileCount: 2, directoryCount: 0, truncated: false, truncatedReasonKey: "" });
const search = () => ({ path: directory, query: "özel", items: [row()], scannedFiles: 1, truncated: false, truncatedReasonKey: "" });
let body: unknown;
const original = apiClient.defaults.adapter;
beforeEach(() => {
  invalidateSessionRequests(); vi.stubGlobal("window", { location: { pathname: "/panel" } });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture" } }), { status: 200 })));
  apiClient.defaults.adapter = async config => ({ config, status: 200, statusText: "OK", headers: {}, data: body });
});
afterEach(() => { apiClient.defaults.adapter = original; invalidateSessionRequests(); vi.unstubAllGlobals(); });
it.each(["failed-envelope", "wrong-path", "negative-bytes", "nonfinite-bytes", "fractional-count", "missing-truncated", "missing-reason"])("rejects an unmeasured directory size: %s", async kind => {
  const data: Record<string, unknown> = size();
  if (kind === "wrong-path") data.path = "sites/b.test/folder";
  if (kind === "negative-bytes") data.totalBytes = -1;
  if (kind === "nonfinite-bytes") data.totalBytes = Infinity;
  if (kind === "fractional-count") data.fileCount = 1.5;
  if (kind === "missing-truncated") delete data.truncated;
  if (kind === "missing-reason") { data.truncated = true; delete data.truncatedReasonKey; }
  body = { success: kind !== "failed-envelope", data };
  await expect(getDirectorySize(directory)).rejects.toEqual(new UserFacingError("panel.files.resultUnconfirmed"));
});
it.each([false, true])("keeps measured zero and partial directory sizes distinct (%s)", async truncated => {
  const data = { ...size(), totalBytes: 0, fileCount: 0, truncated, truncatedReasonKey: truncated ? "files.size_truncated_entries" : "" };
  body = { success: true, data }; await expect(getDirectorySize(directory)).resolves.toEqual(data);
});
it.each(["compress", "extract"])("rejects a failed or different workspace from %s", async action => {
  body = { success: false, data: workspace() };
  const run = () => action === "compress" ? compressPath(directory) : extractPath(archive);
  await expect(run()).rejects.toEqual(new UserFacingError("panel.files.resultUnconfirmed"));
  body = { success: true, data: workspace("sites/b.test") };
  await expect(run()).rejects.toEqual(new UserFacingError("panel.files.resultUnconfirmed"));
});
it("does not confirm compression when its output ZIP is absent", async () => {
  body = { success: true, data: { ...workspace(), entries: [] } };
  await expect(compressPath(directory)).rejects.toEqual(new UserFacingError("panel.files.resultUnconfirmed"));
});
it.each(["file-workspace", "foreign-entry"])("does not publish %s as an extracted directory", async kind => {
  body = { success: true, data: { ...workspace(), isDirectory: kind !== "file-workspace", entries: kind === "foreign-entry" ? [row("sites/b.test/private")] : [] } };
  await expect(extractPath(archive)).rejects.toEqual(new UserFacingError("panel.files.resultUnconfirmed"));
});
it("accepts default and explicit archive destinations with the backend workspace contract", async () => {
  body = { success: true, data: workspace() }; await expect(compressPath(directory)).resolves.toMatchObject({ path: root }); await expect(extractPath(archive)).resolves.toMatchObject({ path: root });
  body = { success: true, data: workspace(`${root}/dest`) }; await expect(compressPath(directory, `${root}/dest/folder.zip`)).resolves.toMatchObject({ path: `${root}/dest` }); await expect(extractPath(archive, `${root}/dest`)).resolves.toMatchObject({ path: `${root}/dest` });
});
it("does not authorize compression overwrite by default and preserves explicit destination consent", async () => {
  const requests: unknown[] = [];
  apiClient.defaults.adapter = async config => {
    requests.push(JSON.parse(String(config.data)));
    return { config, status: 200, statusText: "OK", headers: {}, data: { success: true, data: workspace() } };
  };
  await compressPath(directory);
  // The third parameter is an explicit user decision; it is never inferred from a response.
  await compressPath(directory, archive, true);
  expect(requests).toEqual([
    { path: directory, destinationPath: "", overwrite: false },
    { path: directory, destinationPath: archive, overwrite: true }
  ]);
});
it.each(["failed-envelope", "legacy-array", "wrong-scope", "wrong-query", "foreign-hit", "traversal-hit", "duplicate-hit", "missing-truncated", "invalid-count"])("rejects an unconfirmed search result: %s", async kind => {
  const data = search();
  if (kind === "wrong-scope") data.path = "sites/b.test";
  if (kind === "wrong-query") data.query = "other";
  if (kind === "foreign-hit") data.items = [row("sites/a.test-sibling/private.txt")];
  if (kind === "traversal-hit") data.items = [row(`${directory}/../secret.txt`)];
  if (kind === "duplicate-hit") data.items.push(row());
  if (kind === "missing-truncated") delete (data as Partial<typeof data>).truncated;
  if (kind === "invalid-count") data.scannedFiles = -1;
  body = { success: kind !== "failed-envelope", data: kind === "legacy-array" ? [] : data };
  await expect(searchFiles(directory, "özel")).rejects.toEqual(new UserFacingError("panel.files.resultUnconfirmed"));
});
it.each([false, true])("preserves an empty measured search with its completeness flag (%s)", async truncated => {
  const data = { ...search(), items: [], scannedFiles: 0, truncated, truncatedReasonKey: truncated ? "files.search_truncated_files" : "" };
  body = { success: true, data }; await expect(searchFiles(directory, " özel ")).resolves.toEqual(data);
});
it("accepts nested Unicode search hits and canonical Windows path casing", async () => {
  const data = { ...search(), path: directory.toUpperCase(), items: [row(`${directory}/alt/özel.txt`)] }; body = { success: true, data };
  await expect(searchFiles(directory, "özel")).resolves.toEqual(data);
});
