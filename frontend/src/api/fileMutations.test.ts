import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { apiClient, invalidateSessionRequests } from "./client";
import { deletePath, saveFile, type FileWorkspace } from "./files";
import { UserFacingError } from "../utils/apiError";

const path = "sites/a.test/config.txt", parent = "sites/a.test";
const file = (content = "new content"): FileWorkspace & { contentLoaded?: boolean } => ({ path, parentPath: parent, displayName: "config.txt", physicalPath: "", isDirectory: false, content, contentLoaded: true, entries: [] });
const folder = (): FileWorkspace => ({ ...file(""), path: parent, parentPath: "sites", displayName: "a.test", isDirectory: true });
let body: unknown;
const original = apiClient.defaults.adapter;
beforeEach(() => {
  invalidateSessionRequests(); vi.stubGlobal("window", { location: { pathname: "/panel" } });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture" } }), { status: 200 })));
  apiClient.defaults.adapter = async config => ({ config, status: 200, statusText: "OK", headers: {}, data: body });
});
afterEach(() => { apiClient.defaults.adapter = original; invalidateSessionRequests(); vi.unstubAllGlobals(); });

it.each(["failed-envelope", "missing-success", "different-file", "directory", "missing-content", "different-content"])("save rejects %s rather than allowing the editor to discard its unsaved draft", async kind => {
  const data = file();
  if (kind === "different-file") data.path = "sites/b.test/config.txt";
  if (kind === "directory") data.isDirectory = true;
  if (kind === "missing-content") delete (data as Partial<FileWorkspace>).content;
  if (kind === "different-content") data.content = "old content";
  body = { ...(kind === "missing-success" ? {} : { success: kind !== "failed-envelope" }), data };
  await expect(saveFile(path, "new content")).rejects.toEqual(new UserFacingError("panel.files.resultUnconfirmed"));
});
it.each(["", "Türkçe içerik\r\nsecond line"])("accepts a measured saved file, including empty and Unicode content: %j", async content => {
  const data = file(content); data.path = path.toUpperCase(); body = { success: true, data };
  await expect(saveFile(path, content)).resolves.toMatchObject({ content });
});
it.each([false, undefined])("does not confirm a saved file when contentLoaded is %s", async loaded => {
  const data = file(); data.contentLoaded = loaded; body = { success: true, data };
  await expect(saveFile(path, "new content")).rejects.toEqual(new UserFacingError("panel.files.resultUnconfirmed"));
});

it.each(["failed-envelope", "missing-workspace", "different-parent", "not-directory", "missing-entries", "still-present", "malformed-entry"])("delete rejects %s rather than reporting the selected file deleted", async kind => {
  const data = folder();
  if (kind === "different-parent") data.path = "sites/b.test";
  if (kind === "not-directory") data.isDirectory = false;
  if (kind === "missing-entries") delete (data as Partial<FileWorkspace>).entries;
  if (kind === "still-present") data.entries = [{ name: "config.txt", path: path.toUpperCase(), isDirectory: false, size: 1, lastModifiedUtc: null, extension: ".txt" }];
  if (kind === "malformed-entry") data.entries = [null as never];
  body = { success: kind !== "failed-envelope", data: kind === "missing-workspace" ? null : data };
  await expect(deletePath(path)).rejects.toEqual(new UserFacingError("panel.files.resultUnconfirmed"));
});
it.each([false, true])("accepts a deletion whose measured parent excludes the target (other entries=%s)", async other => {
  const data = folder();
  if (other) data.entries = [{ name: "other.txt", path: `${parent}/other.txt`, isDirectory: false, size: 2, lastModifiedUtc: null, extension: ".txt" }];
  body = { success: true, data }; await expect(deletePath(path)).resolves.toEqual(data);
});
