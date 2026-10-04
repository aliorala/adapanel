import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { type AxiosResponse, type InternalAxiosRequestConfig } from "axios";
import { apiClient, invalidateSessionRequests } from "./client";
import { enableRedisExtension, getGlobalPhpSettings, getPhpVersions, getSitePhpSettings, installPhpVersion, setSitePhpVersion, updateGlobalPhpSettings, updateSitePhpSettings } from "./php";
import { UserFacingError } from "../utils/apiError";

const site = "Örnek site.test", component = "php-8.3";
const settings = (siteName = site) => ({ siteName, memoryLimit: "256M", maxExecutionTime: 120, uploadMaxFilesize: "64M", postMaxSize: "64M", displayErrors: false, openBasedirEnabled: true, configured: true, redisEnabled: false });
const payload = () => ({ memoryLimit: "512M", maxExecutionTime: 0, uploadMaxFilesize: "128M", postMaxSize: "128M", displayErrors: false, openBasedirEnabled: true });
const version = () => ({ componentId: component, displayName: "PHP 8.3", version: "8.3.1", installDir: "C:/fixture/php", executablePath: "C:/fixture/php/php-cgi.exe", status: "Installed", isFastCgiRegistered: true, installedAtUtc: "0001-01-01T00:00:00" });
const installed = () => ({ componentId: component, displayName: "PHP 8.3", version: "", versionMeasured: false, status: "Installed", installDir: "", serviceName: "", generatedPassword: "", message: "php.install.success" });
const order = (extra: Record<string, unknown> = {}) => ({ measured: true, handlerFound: true, precedesCatchAll: true, handlerIndex: 0, catchAllIndex: 1, blockingHandlerName: "", insertedAtFront: true, ordered: true, shadowedByCatchAll: false, missing: false, unverified: false, noticeKey: "", ...extra });
const assignment = () => ({ siteName: site, componentId: component, displayName: "PHP 8.3", version: "", executablePath: "C:/fixture/php/php-cgi.exe", handlerName: "AdaPanel.Php.php-8.3", isFastCgiRegistered: true, handlerOrder: order(), handlerOrderNoticeKey: "" });
const envelope = (data: unknown, success = true) => ({ success, data, message: "php.install.success", code: 200 });
const response = (config: InternalAxiosRequestConfig, data: unknown): AxiosResponse => ({ config, status: 200, statusText: "OK", headers: {}, data });
const originalAdapter = apiClient.defaults.adapter;
let body: unknown, calls: InternalAxiosRequestConfig[];
beforeEach(() => {
  invalidateSessionRequests(); calls = []; body = null;
  vi.stubGlobal("window", { location: { pathname: "/panel", hostname: "localhost" } });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify(envelope({ requestToken: "fixture-only" })), { status: 200 })));
  apiClient.defaults.adapter = async config => { calls.push(config); return response(config, body); };
});
afterEach(() => { apiClient.defaults.adapter = originalAdapter; invalidateSessionRequests(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

const endpoints = [
  { name: "versions", run: () => getPhpVersions(), data: () => [version()] },
  { name: "install", run: () => installPhpVersion(component), data: installed },
  { name: "assignment", run: () => setSitePhpVersion(site, component), data: assignment },
  { name: "site settings read", run: () => getSitePhpSettings(site), data: settings },
  { name: "site settings write", run: () => updateSitePhpSettings(site, payload()), data: settings },
  { name: "global settings read", run: () => getGlobalPhpSettings(), data: () => settings("global") },
  { name: "global settings write", run: () => updateGlobalPhpSettings(payload()), data: () => settings("global") },
  { name: "Redis", run: () => enableRedisExtension(), data: () => ({ enabled: true, message: "already", fileName: "" }) }
];
it.each(endpoints)("rejects a failed HTTP-200 envelope for $name", async endpoint => {
  body = envelope(endpoint.data(), false); await expect(endpoint.run()).rejects.toBeInstanceOf(UserFacingError);
  expect(calls).toHaveLength(1);
});
it.each([
  ["null row", [null]],
  ["non-array", {}],
  ["missing component identity", [{ ...version(), componentId: "" }]],
  ["string FastCGI flag", [{ ...version(), isFastCgiRegistered: "false" }]],
  ["missing status", [{ ...version(), status: undefined }]],
  ["conflicting duplicate identity", [version(), { ...version(), componentId: "PHP-8.3", status: "Failed" }]]
])("does not publish %s as a measured PHP inventory", async (_name, rows) => {
  body = envelope(rows); await expect(getPhpVersions()).rejects.toBeInstanceOf(UserFacingError);
});
it("accepts an empty measured inventory and clones valid Pending/Failed rows without inventing a version", async () => {
  body = envelope([]); await expect(getPhpVersions()).resolves.toEqual([]);
  const rows = [{ ...version(), version: "", status: "Pending", isFastCgiRegistered: false }, { ...version(), componentId: "php-8.2", status: "Failed", isFastCgiRegistered: false }];
  body = envelope(rows); const result = await getPhpVersions();
  expect(result).toEqual(rows); result[0].displayName = "local edit"; expect(rows[0].displayName).toBe("PHP 8.3");
});
it.each(["Failed", "Pending", "Skipped", "", undefined])("does not announce install success for status %s", async status => {
  body = envelope({ ...installed(), status }); await expect(installPhpVersion(component)).rejects.toBeInstanceOf(UserFacingError);
  expect(calls).toHaveLength(1);
});
it("rejects a successful install result for another component", async () => {
  body = envelope({ ...installed(), componentId: "php-8.2" }); await expect(installPhpVersion(component)).rejects.toBeInstanceOf(UserFacingError);
});
it("retains the string install contract with unknown version metadata and an actual Installed outcome", async () => {
  body = envelope(installed()); await expect(installPhpVersion(component)).resolves.toBe("php.install.success");
  expect(calls[0].url).toBe("/php/install"); expect(JSON.parse(calls[0].data)).toEqual({ componentId: component });
});
it.each([
  { siteName: "another.test" }, { componentId: "php-8.2" }, { isFastCgiRegistered: "false" }, { handlerName: null }
])("rejects an unmeasured or foreign assignment %j", async change => {
  body = envelope({ ...assignment(), ...change }); await expect(setSitePhpVersion(site, component)).rejects.toBeInstanceOf(UserFacingError);
});
it.each([
  { measurement: order({ measured: false, handlerFound: false, precedesCatchAll: false, handlerIndex: -1, catchAllIndex: -1 }), expected: "aspnet.handler_order_unverified" },
  { measurement: order({ handlerFound: false, precedesCatchAll: false, handlerIndex: -1 }), expected: "aspnet.handler_missing" },
  { measurement: order({ precedesCatchAll: false, handlerIndex: 2, catchAllIndex: 0, blockingHandlerName: "StaticFile" }), expected: "aspnet.handler_shadowed" }
])("derives the genuine handler warning $expected even if a stale summary claims success", async ({ measurement, expected }) => {
  const value = { ...assignment(), handlerOrder: measurement }; body = envelope(value);
  const result = await setSitePhpVersion(site, component) as unknown as Record<string, any>;
  expect(result.handlerOrderNoticeKey).toBe(expected); expect(result.handlerOrder.noticeKey).toBe(expected);
  expect(result.handlerOrder.ordered).toBe(false);
  expect(value.handlerOrderNoticeKey).toBe(""); expect(measurement.ordered).toBe(true);
});
it("treats legacy missing handler measurement as unknown rather than a successful order", async () => {
  body = envelope({ ...assignment(), handlerOrder: undefined, handlerOrderNoticeKey: undefined });
  const result = await setSitePhpVersion(site, component) as unknown as Record<string, any>;
  expect(result.handlerOrderNoticeKey).toBe("aspnet.handler_order_unverified"); expect(result.handlerOrder.measured).toBe(false);
});
it("preserves a genuinely ordered assignment and canonical IIS site casing", async () => {
  const value = { ...assignment(), siteName: site.toUpperCase() }; body = envelope(value);
  const result = await setSitePhpVersion(site, component) as unknown as Record<string, any>;
  expect(result.handlerOrderNoticeKey).toBe(""); expect(result.handlerOrder.ordered).toBe(true);
  expect(calls[0].url).toBe(`/sites/${encodeURIComponent(site)}/php`);
  expect(JSON.parse(calls[0].data)).toEqual({ componentId: component });
  result.handlerOrder.blockingHandlerName = "local"; expect(value.handlerOrder.blockingHandlerName).toBe("");
});
it("rejects malformed handler measurement booleans", async () => {
  body = envelope({ ...assignment(), handlerOrder: order({ measured: "false" }) });
  await expect(setSitePhpVersion(site, component)).rejects.toBeInstanceOf(UserFacingError);
});

const settingsCalls = [
  { name: "site GET", run: () => getSitePhpSettings(site), expected: site },
  { name: "site POST", run: () => updateSitePhpSettings(site, payload()), expected: site },
  { name: "global GET", run: () => getGlobalPhpSettings(), expected: "global" },
  { name: "global POST", run: () => updateGlobalPhpSettings(payload()), expected: "global" }
];
it.each(settingsCalls)("rejects another target's $name settings", async endpoint => {
  body = envelope(settings(endpoint.expected === site ? "other.test" : site)); await expect(endpoint.run()).rejects.toBeInstanceOf(UserFacingError);
});
it.each([
  { configured: "false" }, { displayErrors: "false" }, { openBasedirEnabled: "false" }, { redisEnabled: "false" },
  { maxExecutionTime: "120" }, { maxExecutionTime: Infinity }, { memoryLimit: null }
])("does not hydrate or save malformed settings %j", async change => {
  body = envelope({ ...settings(), ...change }); await expect(getSitePhpSettings(site)).rejects.toBeInstanceOf(UserFacingError);
});
it.each(settingsCalls)("accepts genuine default/empty values and isolates the $name result", async endpoint => {
  const value = { ...settings(endpoint.expected), memoryLimit: "", uploadMaxFilesize: "", postMaxSize: "", maxExecutionTime: 0, configured: false };
  body = envelope(value); const result = await endpoint.run();
  expect(result).toEqual(value); result.memoryLimit = "1024M"; expect(value.memoryLimit).toBe("");
});
it("does not invent isolation or Redis evidence when an older settings response omits those fields", async () => {
  const { openBasedirEnabled: _isolation, redisEnabled: _redis, ...value } = settings();
  body = envelope(value); const result = await getSitePhpSettings(site);
  expect(result.openBasedirEnabled).toBeUndefined(); expect(result.redisEnabled).toBeUndefined();
});
it.each([{ enabled: false, message: "installed", fileName: "php_redis.dll" }, { enabled: "true", message: "installed", fileName: "php_redis.dll" }, { enabled: true, message: "unknown", fileName: "" }])("does not announce unconfirmed Redis activation %j", async value => {
  body = envelope(value); await expect(enableRedisExtension()).rejects.toBeInstanceOf(UserFacingError);
});
it.each([{ enabled: true, message: "already", fileName: "" }, { enabled: true, message: "installed", fileName: "php_redis.dll" }])("preserves actual Redis activation %j", async value => {
  body = envelope(value); const result = await enableRedisExtension(); expect(result).toEqual(value);
  result.message = "local"; expect(value.message).not.toBe("local");
});
it.each([false, true])("keeps the client session cancellation boundary on a delayed response (POST=%s)", async post => {
  let release!: () => void; const wait = new Promise<void>(resolve => { release = resolve; });
  apiClient.defaults.adapter = async config => { calls.push(config); await wait; return response(config, envelope(post ? assignment() : [version()])); };
  const pending = (post ? setSitePhpVersion(site, component) : getPhpVersions()).catch(error => error);
  await vi.waitFor(() => expect(calls).toHaveLength(1)); invalidateSessionRequests(); release();
  await expect(pending).resolves.toHaveProperty("code", "ERR_CANCELED");
});
