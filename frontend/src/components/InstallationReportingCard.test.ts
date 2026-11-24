import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from 'vue';
import InstallationReportingCard from './InstallationReportingCard.vue';
import type { InstallationReporting } from '../api/installationReporting';

const api = vi.hoisted(() => ({ get: vi.fn(), save: vi.fn(), locale: { value: 'tr' } }));
vi.mock('../api/installationReporting', () => ({ getInstallationReporting: api.get, saveInstallationReporting: api.save }));
vi.mock('vue-i18n', () => ({ useI18n: () => ({ locale: api.locale }) }));
const report = (overrides: Partial<InstallationReporting> = {}): InstallationReporting => ({
  enabled: true, revision: 'initial', consentAtUtc: null, lastAttemptAtUtc: null,
  lastAcceptedAtUtc: null, nextAttemptAtUtc: null, lastError: '', ...overrides
});
type State = {
  saved: InstallationReporting | null; enabled: boolean; busy: boolean; failed: boolean; message: string;
  deliveryError: string; load(): Promise<void>; save(): Promise<void>;
};
let app: App | undefined;
async function flush() { for (let i = 0; i < 12; i++) await nextTick(); }
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(yes => { resolve = yes; });
  return { promise, resolve };
}
async function mount() {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...InstallationReportingCard, render: () => h('section') } as Component);
  app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState;
  await flush(); return state;
}
beforeEach(() => { vi.resetAllMocks(); api.locale.value = 'tr'; api.get.mockResolvedValue(report()); api.save.mockResolvedValue(report({ revision: 'saved' })); });
afterEach(() => { app?.unmount(); app = undefined; });

it.each(['tr', 'en'])('separates saved preferences from failed delivery in %s', async locale => {
  api.locale.value = locale;
  const state = await mount();
  api.save.mockResolvedValue(report({ revision: 'saved', lastError: 'network', lastAttemptAtUtc: '2026-10-28T10:00:00Z', nextAttemptAtUtc: '2026-10-28T10:05:00Z' }));
  await state.save();
  expect(state.message).toContain(locale === 'tr' ? 'Tercih kaydedildi' : 'Preference saved');
  expect(state.message).not.toContain(locale === 'tr' ? 'gönderildi' : 'report sent');
  expect(state.deliveryError).toBe(locale === 'tr' ? 'Bağlantı kurulamadı' : 'Connection failed');
  expect(state.saved?.lastAcceptedAtUtc).toBeNull();
  expect(state.saved?.nextAttemptAtUtc).toBe('2026-10-28T10:05:00Z');
});
it('does not submit a duplicate preference update while delivery is pending', async () => {
  const state = await mount(), pending = deferred<InstallationReporting>(); api.save.mockReturnValue(pending.promise);
  const saving = state.save(); await flush(); await state.save(); await state.load();
  expect(api.save).toHaveBeenCalledOnce(); expect(api.get).toHaveBeenCalledOnce();
  pending.resolve(report({ revision: 'saved' })); await saving;
});
it('keeps the previous measured state and shows an error when saving fails', async () => {
  const state = await mount(); api.save.mockRejectedValue(new Error('failed'));
  await state.save(); expect(state.failed).toBe(true); expect(state.saved?.revision).toBe('initial');
});
it.each(['load', 'save'] as const)('does not publish a late %s response after unmount', async operation => {
  const state = await mount(), pending = deferred<InstallationReporting>();
  (operation === 'load' ? api.get : api.save).mockReturnValue(pending.promise);
  const completing = state[operation](); await flush(); app!.unmount(); app = undefined;
  pending.resolve(report({ enabled: false, revision: 'late' })); await completing;
  expect(state.saved?.revision).toBe('initial'); expect(state.message).toBe('');
});
