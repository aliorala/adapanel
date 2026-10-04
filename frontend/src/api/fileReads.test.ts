import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { apiClient, invalidateSessionRequests } from "./client";
import { getFileWorkspace, type FileWorkspace } from "./files";
import { UserFacingError } from "../utils/apiError";

const path = "sites/a.test/config.txt", root = "sites/a.test";
const file = (): FileWorkspace => ({ path, parentPath: root, displayName: "config.txt", physicalPath: "", isDirectory: false, content: "measured", contentLoaded: true, contentUnavailableReasonKey: "", entries: [] });
const folder = (): FileWorkspace => ({ ...file(), path: root, parentPath: "sites", displayName: "a.test", isDirectory: true, content: "", contentLoaded: false, entries: [{ name: "config.txt", path, isDirectory: false, size: 1, lastModifiedUtc: null, extension: ".txt" }] });
let body: unknown;
const original = apiClient.defaults.adapter;
beforeEach(() => { invalidateSessionRequests(); vi.stubGlobal("window", { location: { pathname: "/panel" } }); apiClient.defaults.adapter = async config => ({ config, status: 200, statusText: "OK", headers: {}, data: body }); });
afterEach(() => { apiClient.defaults.adapter = original; invalidateSessionRequests(); vi.unstubAllGlobals(); });

it.each(["failed-envelope", "missing-success", "missing-data", "wrong-path", "missing-kind", "missing-content", "missing-entries", "nonboolean-loaded"])("read rejects %s instead of opening a different or unmeasured file", async kind => {
  const data = file();
  if (kind === "wrong-path") data.path = "sites/b.test/config.txt";
  if (kind === "missing-kind") delete (data as Partial<FileWorkspace>).isDirectory;
  if (kind === "missing-content") delete (data as Partial<FileWorkspace>).content;
  if (kind === "missing-entries") delete (data as Partial<FileWorkspace>).entries;
  if (kind === "nonboolean-loaded") data.contentLoaded = "yes" as never;
  body = { ...(kind === "missing-success" ? {} : { success: kind !== "failed-envelope" }), data: kind === "missing-data" ? null : data };
  await expect(getFileWorkspace(path)).rejects.toEqual(new UserFacingError("panel.files.resultUnconfirmed"));
});
it.each(["foreign-path", "mismatched-name", "missing-kind", "invalid-size", "duplicate-path", "null-row"])("directory read rejects %s before exposing file actions", async kind => {
  const data = folder();
  if (kind === "foreign-path") data.entries[0]!.path = "sites/b.test/config.txt";
  if (kind === "mismatched-name") data.entries[0]!.name = "different.txt";
  if (kind === "missing-kind") delete (data.entries[0] as Partial<typeof data.entries[0]>).isDirectory;
  if (kind === "invalid-size") data.entries[0]!.size = -1;
  if (kind === "duplicate-path") data.entries.push({ ...data.entries[0]!, path: path.toUpperCase() });
  if (kind === "null-row") data.entries = [null as never];
  body = { success: true, data }; await expect(getFileWorkspace(root)).rejects.toEqual(new UserFacingError("panel.files.resultUnconfirmed"));
});
it.each([false, undefined])("preserves download-only metadata when contentLoaded is %s", async loaded => {
  const data = { ...file(), content: "", contentLoaded: loaded, contentUnavailableReasonKey: loaded === false ? "files.content_not_editable" : "" };
  body = { success: true, data }; await expect(getFileWorkspace(path)).resolves.toEqual(data);
});
it("accepts the requested Windows path identity and a measured empty text file", async () => {
  const data = { ...file(), path: path.toUpperCase(), content: "" }; body = { success: true, data };
  await expect(getFileWorkspace(path)).resolves.toEqual(data);
});
it.each(["", "sites"])("accepts the existing %j root listing response to an empty root request", async resultPath => {
  const data = { ...folder(), path: resultPath, entries: resultPath ? [{ ...folder().entries[0]!, name: "a.test", path: root, isDirectory: true }] : [{ ...folder().entries[0]!, name: "sites", path: "sites", isDirectory: true }] };
  body = { success: true, data }; await expect(getFileWorkspace()).resolves.toEqual(data);
});
