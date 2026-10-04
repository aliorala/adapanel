import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { apiClient, invalidateSessionRequests } from "./client";
import { createDirectory, createFile, updatePermissions, type FileWorkspace } from "./files";
import { UserFacingError } from "../utils/apiError";

const path = "sites/a.test/özel dosya.txt";
const workspace = (directory = false, content = ""): FileWorkspace => ({ path, parentPath: "sites/a.test", displayName: "özel dosya.txt", physicalPath: "", isDirectory: directory, content, contentLoaded: !directory, entries: [] });
const original = apiClient.defaults.adapter;
let body: unknown, sent: unknown;
beforeEach(() => {
  invalidateSessionRequests(); vi.stubGlobal("window", { location: { pathname: "/panel" } });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture" } }), { status: 200 })));
  apiClient.defaults.adapter = async config => { sent = JSON.parse(String(config.data)); return { config, status: 200, statusText: "OK", headers: {}, data: body }; };
});
afterEach(() => { apiClient.defaults.adapter = original; invalidateSessionRequests(); vi.unstubAllGlobals(); });

const routes = [
  { name: "file creation", run: () => createFile(path), data: () => workspace() },
  { name: "directory creation", run: () => createDirectory(path), data: () => workspace(true) },
  { name: "permissions", run: () => updatePermissions(path, "IIS_IUSRS", "M"), data: () => workspace(true) }
];
for (const route of routes) {
  it.each(["failed-envelope", "missing-success", "wrong-target", "missing-entries"])(`${route.name} rejects %s before confirming an operation`, async kind => {
    const data = route.data();
    if (kind === "wrong-target") data.path = "sites/b.test/private";
    if (kind === "missing-entries") delete (data as Partial<FileWorkspace>).entries;
    body = { success: kind === "missing-success" ? undefined : kind !== "failed-envelope", data };
    await expect(route.run()).rejects.toEqual(new UserFacingError("panel.files.resultUnconfirmed"));
  });
}
it.each(["directory", "unloaded", "different-content"])("does not confirm file creation with %s output", async kind => {
  const data = workspace(kind === "directory", kind === "different-content" ? "other" : "");
  if (kind === "unloaded") data.contentLoaded = false;
  body = { success: true, data };
  await expect(createFile(path)).rejects.toEqual(new UserFacingError("panel.files.resultUnconfirmed"));
});
it("does not confirm a directory when the returned target is a file", async () => {
  body = { success: true, data: workspace() };
  await expect(createDirectory(path)).rejects.toEqual(new UserFacingError("panel.files.resultUnconfirmed"));
});
it.each(["", "Türkçe içerik\r\n"])("preserves create-new and exact Unicode/empty file contents %j", async content => {
  const data = workspace(false, content); data.path = path.toUpperCase(); body = { success: true, data };
  await expect(createFile(path, content)).resolves.toEqual(data);
  expect(sent).toEqual({ path, content, failIfExists: true });
});
it("accepts a measured empty Unicode-named directory", async () => {
  const data = workspace(true); body = { success: true, data };
  await expect(createDirectory(path)).resolves.toEqual(data); expect(sent).toEqual({ path });
});
it.each([false, true])("accepts permissions on the confirmed file/directory target (%s)", async directory => {
  const data = { ...workspace(directory), permissionsVerified: true, permissionsDetail: "Processed 1 entry" }; body = { success: true, data };
  await expect(updatePermissions(path, "IIS_IUSRS", "R")).resolves.toEqual(data);
  expect(sent).toEqual({ path, principal: "IIS_IUSRS", rights: "R" });
});
it.each([undefined, "true"])("does not invent a verified permission outcome from %j metadata", async verified => {
  body = { success: true, data: { ...workspace(true), permissionsVerified: verified } };
  if (verified === undefined) await expect(updatePermissions(path, "IIS_IUSRS", "M")).resolves.toMatchObject({ permissionsVerified: null, permissionsDetail: "" });
  else await expect(updatePermissions(path, "IIS_IUSRS", "M")).rejects.toEqual(new UserFacingError("panel.files.resultUnconfirmed"));
});
it("rejects non-text permission details", async () => {
  body = { success: true, data: { ...workspace(true), permissionsVerified: true, permissionsDetail: 123 } };
  await expect(updatePermissions(path, "IIS_IUSRS", "M")).rejects.toEqual(new UserFacingError("panel.files.resultUnconfirmed"));
});
