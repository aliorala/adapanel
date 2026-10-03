import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import CacheView from "./CacheView.vue";
import { getCacheStatus, startMemcached, startRedis, stopMemcached, stopRedis, type CacheRuntime, type CacheStatus } from "../api/cache";
import { useSoftwareStore } from "../stores/software";
import { invalidateSessionRequests } from "../api/client";
import { useSubscriptionStore } from "../stores/subscription";
import type { SubscriptionUsage } from "../api/subscriptions";

const confirm = vi.hoisted(() => vi.fn());
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm }) }));
vi.mock("../api/cache", async original => ({ ...await original<object>(), getCacheStatus: vi.fn(), startMemcached: vi.fn(), startRedis: vi.fn(), stopMemcached: vi.fn(), stopRedis: vi.fn() }));
let app: App | undefined;
beforeEach(() => {
  vi.mocked(getCacheStatus).mockResolvedValue(status());
  confirm.mockImplementation(async options => { await options.action?.(); return true; });
});
afterEach(() => { app?.unmount(); app = undefined; vi.restoreAllMocks(); vi.resetAllMocks(); });
function runtime(componentId = "redis", state = "Stopped", isInstalled = true): CacheRuntime {
  return { componentId, displayName: componentId, serviceName: `Ada${componentId}`, installDir: `/cache/${componentId}`, executablePath: `/cache/${componentId}/run.exe`, defaultPort: componentId === "redis" ? 6379 : 11211, status: state, isInstalled, isServiceRegistered: isInstalled };
}
function status(redisState = "Stopped", memcachedState = "Stopped"): CacheStatus { return { redis: runtime("redis", redisState), memcached: runtime("memcached", memcachedState) }; }
function deferred<T>() { let resolve!: (value: T) => void, reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
type State = {
  status: CacheStatus | null; installedCount: number | null; runningCount: number | null; busyAction: string; notice: string; noticeError: boolean;
  refresh(): Promise<void>; install(id: string): Promise<void>; remove(id: string): Promise<void>; toggle(id: string, start: boolean): Promise<void>;
  statusLabel(item: CacheRuntime): string;
};
async function flush() { for (let i = 0; i < 12; i++) await nextTick(); }
async function mount() {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia);
  vi.spyOn(useSoftwareStore(), "installComponent").mockResolvedValue({ componentId: "redis", displayName: "redis", version: "1.0", status: "Installed", installDir: "/cache/redis", serviceName: "Adaredis", generatedPassword: "", message: "installed" });
  vi.spyOn(useSoftwareStore(), "removeComponent").mockResolvedValue();
  app = renderer.createApp({ ...CacheView, render: () => h("section") } as Component).use(pinia);
  app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState;
  await flush(); return state;
}
function unmount() { app!.unmount(); app = undefined; }

it("does not present unread cache counts as measured zeroes", async () => {
  const pending = deferred<CacheStatus>(); vi.mocked(getCacheStatus).mockReturnValueOnce(pending.promise);
  const state = await mount();
  expect(state.installedCount).toBeNull(); expect(state.runningCount).toBeNull();
  pending.resolve(status()); await flush(); expect(state.installedCount).toBe(2); expect(state.runningCount).toBe(0);
});

it("does not apply a cache status read after unmount", async () => {
  const pending = deferred<CacheStatus>(); vi.mocked(getCacheStatus).mockReturnValueOnce(pending.promise);
  const state = await mount(); unmount(); pending.resolve(status()); await flush();
  expect(state.status).toBeNull();
});

it("does not dispatch overlapping manual refreshes", async () => {
  const state = await mount(), pending = deferred<CacheStatus>(); vi.mocked(getCacheStatus).mockReturnValue(pending.promise).mockClear();
  const first = state.refresh(), second = state.refresh(); pending.resolve(status()); await Promise.all([first, second]);
  expect(getCacheStatus).toHaveBeenCalledTimes(1);
});

it("prevents duplicate and cross-service actions from releasing an active operation's lock", async () => {
  const state = await mount(), pending = deferred<CacheRuntime>();
  vi.mocked(startRedis).mockReturnValue(pending.promise); vi.mocked(startMemcached).mockResolvedValue(runtime("memcached", "Running"));
  vi.mocked(getCacheStatus).mockResolvedValue(status("Running"));
  const first = state.toggle("redis", true), second = state.toggle("redis", true), third = state.toggle("memcached", true);
  await flush(); expect(state.busyAction).toBe("redis:start");
  pending.resolve(runtime("redis", "Running")); await Promise.all([first, second, third]);
  expect(startRedis).toHaveBeenCalledTimes(1); expect(startMemcached).not.toHaveBeenCalled();
});

it("does not manually refresh in the middle of a service mutation", async () => {
  const state = await mount(), pending = deferred<CacheRuntime>(); vi.mocked(startRedis).mockReturnValueOnce(pending.promise);
  vi.mocked(getCacheStatus).mockClear().mockResolvedValue(status("Running"));
  const starting = state.toggle("redis", true); await state.refresh();
  expect(getCacheStatus).not.toHaveBeenCalled();
  pending.resolve(runtime("redis", "Running")); await starting;
});

it.each(["install", "remove", "toggle"] as const)("does not hide a failed status refresh after %s with a success notice", async operation => {
  if (operation === "install") vi.mocked(getCacheStatus).mockResolvedValueOnce({ ...status(), redis: runtime("redis", "NotInstalled", false) });
  const state = await mount(); vi.mocked(getCacheStatus).mockRejectedValueOnce(new Error("status offline"));
  vi.mocked(startRedis).mockResolvedValueOnce(runtime("redis", "Running"));
  if (operation === "toggle") await state.toggle("redis", true); else await state[operation]("redis");
  expect(state.noticeError).toBe(true); expect(state.notice).toContain("panel.cache.resultUnconfirmed");
  expect(state.status).toBeNull(); expect(state.installedCount).toBeNull();
});

it("does not claim a service started when the refreshed status says it stopped", async () => {
  const state = await mount(); vi.mocked(startRedis).mockResolvedValueOnce(runtime("redis", "Running"));
  await state.toggle("redis", true);
  expect(state.noticeError).toBe(true); expect(state.notice).toBe("panel.cache.resultUnconfirmed");
});

it("does not start a follow-up status read after a mutation finishes on an unmounted screen", async () => {
  const state = await mount(), pending = deferred<CacheRuntime>(); vi.mocked(startRedis).mockReturnValueOnce(pending.promise); vi.mocked(getCacheStatus).mockClear();
  const starting = state.toggle("redis", true); unmount(); pending.resolve(runtime("redis", "Running")); await starting;
  expect(getCacheStatus).not.toHaveBeenCalled(); expect(state.notice).toBe("");
});

it.each(["unmount", "session", "refresh"])("does not apply a removal confirmation after %s changes its context", async context => {
  const state = await mount();
  confirm.mockImplementationOnce(async options => {
    if (context === "unmount") unmount();
    else if (context === "session") invalidateSessionRequests();
    else { vi.mocked(getCacheStatus).mockResolvedValueOnce(status("Running")); await state.refresh(); }
    await options.action?.(); return true;
  });
  await state.remove("redis"); expect(useSoftwareStore().removeComponent).not.toHaveBeenCalled();
});

it("does not redirect an unrecognized component action to Redis", async () => {
  const state = await mount(); await state.toggle("other-service", true);
  expect(startRedis).not.toHaveBeenCalled(); expect(startMemcached).not.toHaveBeenCalled();
});

it("does not allow writes after cache status could not be read", async () => {
  vi.mocked(getCacheStatus).mockRejectedValueOnce(new Error("unavailable")); const state = await mount();
  await state.toggle("redis", true); await state.install("redis"); await state.remove("redis");
  expect(startRedis).not.toHaveBeenCalled(); expect(useSoftwareStore().installComponent).not.toHaveBeenCalled(); expect(useSoftwareStore().removeComponent).not.toHaveBeenCalled();
});

it.each(["redis", "memcached"])("still starts and stops %s using the correct API", async componentId => {
  const state = await mount();
  const startingApi = componentId === "redis" ? startRedis : startMemcached, stoppingApi = componentId === "redis" ? stopRedis : stopMemcached;
  vi.mocked(startingApi).mockResolvedValueOnce(runtime(componentId, "Running"));
  vi.mocked(stoppingApi).mockResolvedValueOnce(runtime(componentId, "Stopped"));
  vi.mocked(getCacheStatus).mockResolvedValueOnce({ ...status(), [componentId]: runtime(componentId, "Running") }).mockResolvedValueOnce(status());
  await state.toggle(componentId, true); expect(state.notice).toBe("panel.cache.started"); expect(state.noticeError).toBe(false);
  await state.toggle(componentId, false); expect(state.notice).toBe("panel.cache.stopped"); expect(state.noticeError).toBe(false);
  expect(startingApi).toHaveBeenCalledTimes(1); expect(stoppingApi).toHaveBeenCalledTimes(1);
});

it("still installs and removes a confirmed component when its status is verified", async () => {
  const absent = { ...status(), redis: runtime("redis", "NotInstalled", false) };
  vi.mocked(getCacheStatus).mockResolvedValueOnce(absent).mockResolvedValueOnce(status()).mockResolvedValueOnce(absent);
  const state = await mount(); await state.install("redis"); expect(state.notice).toBe("panel.cache.installedMessage");
  await state.remove("redis"); expect(state.notice).toBe("panel.cache.removedMessage"); expect(state.noticeError).toBe(false);
  expect(useSoftwareStore().installComponent).toHaveBeenCalledExactlyOnceWith("redis"); expect(useSoftwareStore().removeComponent).toHaveBeenCalledExactlyOnceWith("redis");
});

it("does not send duplicate component installation requests", async () => {
  vi.mocked(getCacheStatus).mockResolvedValueOnce({ ...status(), redis: runtime("redis", "NotInstalled", false) });
  const state = await mount(), pending = deferred<Awaited<ReturnType<ReturnType<typeof useSoftwareStore>["installComponent"]>>>();
  vi.mocked(useSoftwareStore().installComponent).mockReturnValue(pending.promise);
  const first = state.install("redis"), second = state.install("redis");
  pending.resolve({ componentId: "redis", displayName: "redis", version: "1.0", status: "Installed", installDir: "/cache/redis", serviceName: "Adaredis", generatedPassword: "", message: "installed" });
  await Promise.all([first, second]); expect(useSoftwareStore().installComponent).toHaveBeenCalledTimes(1);
});

it.each(["", "Unknown"])("keeps an unknown runtime state '%s' unknown and does not start or remove that runtime", async runtimeState => {
  vi.mocked(getCacheStatus).mockResolvedValueOnce(status(runtimeState)); const state = await mount();
  expect(state.runningCount).toBeNull(); expect(state.statusLabel(state.status!.redis)).toBe("panel.health.statusUnknown");
  await state.toggle("redis", true); await state.remove("redis");
  expect(startRedis).not.toHaveBeenCalled(); expect(confirm).not.toHaveBeenCalled(); expect(useSoftwareStore().removeComponent).not.toHaveBeenCalled();
});

it("does not confirm a removal when the refreshed runtime is still installed", async () => {
  const state = await mount(); await state.remove("redis");
  expect(useSoftwareStore().removeComponent).toHaveBeenCalledExactlyOnceWith("redis");
  expect(state.noticeError).toBe(true); expect(state.notice).toBe("panel.cache.resultUnconfirmed");
});

it("allows a successful refresh and mutation after an earlier status failure", async () => {
  vi.mocked(getCacheStatus).mockRejectedValueOnce(new Error("offline")); const state = await mount();
  await state.refresh(); expect(state.installedCount).toBe(2); expect(state.noticeError).toBe(false);
  vi.mocked(getCacheStatus).mockResolvedValueOnce(status("Running")); vi.mocked(startRedis).mockResolvedValueOnce(runtime("redis", "Running"));
  await state.toggle("redis", true); expect(state.notice).toBe("panel.cache.started");
});

it("does not execute a removal if the subscription becomes read-only during confirmation", async () => {
  const state = await mount();
  confirm.mockImplementationOnce(async options => {
    useSubscriptionStore().usage = { isSuspended: true } as SubscriptionUsage;
    await options.action?.(); return true;
  });
  await state.remove("redis"); expect(useSoftwareStore().removeComponent).not.toHaveBeenCalled();
});

it("does not apply cache status belonging to an invalidated session", async () => {
  const pending = deferred<CacheStatus>(); vi.mocked(getCacheStatus).mockReturnValueOnce(pending.promise);
  const state = await mount(); invalidateSessionRequests(); pending.resolve(status()); await flush();
  expect(state.status).toBeNull(); expect(state.installedCount).toBeNull();
});
