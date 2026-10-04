import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { AxiosError, type InternalAxiosRequestConfig } from "axios";
import { apiClient, invalidateSessionRequests } from "./client";
import { createMyDatabase, deleteMyDatabase, getMyDatabases } from "./myDatabases";
import { UserFacingError } from "../utils/apiError";

const originalAdapter = apiClient.defaults.adapter;
const draft = { engine: "mysql", name: "Tenant_DB", userName: "tenant_user", password: "FixturePassword!123" };
const routes = [
  { name: "list", run: () => getMyDatabases() },
  { name: "create", run: () => createMyDatabase(draft) },
  { name: "delete", run: () => deleteMyDatabase("postgres", "Foo/Bar") }
];
let body: unknown, calls: InternalAxiosRequestConfig[];
beforeEach(() => {
  invalidateSessionRequests(); calls = []; body = { success: true, data: [] };
  vi.stubGlobal("window", { location: { pathname: "/panel" } });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture-token" } }), { status: 200 })));
  apiClient.defaults.adapter = async config => { calls.push(config); return { config, data: body, headers: {}, status: 200, statusText: "OK" }; };
});
afterEach(() => { apiClient.defaults.adapter = originalAdapter; invalidateSessionRequests(); vi.unstubAllGlobals(); });

const invalid = [
  { name: "failed envelope containing an otherwise valid list", body: { success: false, data: [{ engine: "mysql", name: "Tenant_DB" }] } },
  { name: "missing success flag", body: { data: [] } },
  { name: "null envelope", body: null },
  { name: "null list", body: { success: true, data: null } },
  { name: "object instead of a list", body: { success: true, data: {} } },
  { name: "missing engine", body: { success: true, data: [{ name: "Tenant_DB" }] } },
  { name: "blank database identity", body: { success: true, data: [{ engine: "mysql", name: "  " }] } },
  { name: "partially valid list", body: { success: true, data: [{ engine: "mysql", name: "Tenant_DB" }, null] } }
];
for (const route of routes) {
  for (const sample of invalid) it(`${route.name} rejects ${sample.name}`, async () => {
    body = sample.body;
    await expect(route.run()).rejects.toEqual(new UserFacingError("panel.myDb.resultUnconfirmed"));
    expect(calls).toHaveLength(1);
  });
  it(`${route.name} accepts a measured empty list`, async () => {
    await expect(route.run()).resolves.toEqual([]);
  });
  it(`${route.name} preserves database case and engine aliases in a measured list`, async () => {
    const databases = [{ engine: "postgres", name: "Foo" }, { engine: "postgresql", name: "foo" }, { engine: "sqlserver", name: "MixedCase" }];
    body = { success: true, data: databases };
    await expect(route.run()).resolves.toEqual(databases);
  });
}
it("keeps the creation payload and delete target contract unchanged", async () => {
  await createMyDatabase(draft); await deleteMyDatabase("postgres", "Foo/Bar");
  expect(calls[0]!.url).toBe("/my-databases"); expect(JSON.parse(String(calls[0]!.data))).toEqual(draft);
  expect(calls[1]!.url).toBe("/my-databases/Foo%2FBar"); expect(calls[1]!.params).toEqual({ engine: "postgres" });
});
it("preserves the original HTTP failure for the caller's translated error", async () => {
  let failure!: AxiosError;
  apiClient.defaults.adapter = async config => { failure = new AxiosError("quota", "ERR_BAD_REQUEST", config, null, { config, status: 403, statusText: "Forbidden", headers: {}, data: { success: false, message: "database.quota_exceeded" } }); throw failure; };
  await expect(createMyDatabase(draft)).rejects.toMatchObject({ response: { status: 403, data: { message: "database.quota_exceeded" } } });
});
