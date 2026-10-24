import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { createRenderer, h, ssrContextKey, type App, type Component } from 'vue';
import Repair from './SslBindingRepair.vue';
import { getSslRepairPlan, applySslRepair, type SslRepairOption, type SslRepairPlan } from '../api/sslRepair';
vi.mock('vue-i18n', () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock('../api/sslRepair', () => ({ getSslRepairPlan: vi.fn(), applySslRepair: vi.fn() }));
vi.mock('../composables/useSubscriptionGate', () => ({ useSubscriptionGate: () => ({ writeGate: () => ({}) }) }));
vi.mock('../stores/auth', () => ({ extractApiMessage: () => 'ssl.repair.plan_changed' }));
const target = { siteName: 'site', hostName: 'example.test', port: 443 };
const cert = { thumbprint: 'A'.repeat(40), subject: 'CN=example.test', notAfterUtc: '2027-01-01T00:00:00Z', valid: true };
const option: SslRepairOption = { hostName: target.hostName, bindingInformation: '*:443:example.test', current: cert, candidate: cert, planToken: 'opaque', blockedReason: '' };
type State = { inspect(): Promise<void>; apply(option: SslRepairOption): Promise<void>; plan: SslRepairPlan | null; message: string; verified: boolean; busy: boolean };
let app: App | undefined;
function mount(onRefresh = vi.fn()) {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...Repair, render: () => h('section') } as Component, { target, onRefresh });
  app.provide(ssrContextKey, {});
  return (app.mount({}).$ as unknown as { setupState: State }).setupState;
}
beforeEach(() => { vi.clearAllMocks(); vi.mocked(getSslRepairPlan).mockResolvedValue({ siteName: 'site', recordHostName: target.hostName, options: [option] }); });
afterEach(() => { app?.unmount(); });
it('inspection never applies a certificate automatically', async () => {
  const state = mount(); await state.inspect();
  expect(getSslRepairPlan).toHaveBeenCalledWith(target); expect(applySslRepair).not.toHaveBeenCalled(); expect(state.plan?.options).toEqual([option]);
});
it.each([[true,true],[true,false],[false,true],[false,false]])('requires both binding=%s and TLS=%s for success', async (bindingApplied, tlsVerified) => {
  const refresh = vi.fn(); const state = mount(refresh);
  vi.mocked(applySslRepair).mockResolvedValue({ bindingApplied, tlsVerified, detail: 'ssl.repair.tls_failed' });
  await state.inspect(); await state.apply(option);
  expect(applySslRepair).toHaveBeenCalledWith('opaque'); expect(state.verified).toBe(bindingApplied && tlsVerified);
  expect(state.message === 'ssl.repair.verified').toBe(bindingApplied && tlsVerified);
  expect(state.plan).toBeNull(); expect(refresh).toHaveBeenCalledOnce();
});
it('rejects stale plans without claiming success and reloads warnings', async () => {
  const refresh = vi.fn(); const state = mount(refresh);
  vi.mocked(applySslRepair).mockRejectedValue(new Error('409'));
  await state.inspect(); await state.apply(option);
  expect(state.message).toBe('ssl.repair.plan_changed'); expect(state.verified).toBe(false); expect(state.plan).toBeNull(); expect(refresh).toHaveBeenCalledOnce();
});
it('does not submit blocked options or duplicate clicks', async () => {
  const state = mount(); await state.apply({ ...option, planToken: '' });
  expect(applySslRepair).not.toHaveBeenCalled();
  state.busy = true; await state.apply(option); expect(applySslRepair).not.toHaveBeenCalled();
});
