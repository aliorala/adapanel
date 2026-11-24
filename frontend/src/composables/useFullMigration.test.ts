import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { createRenderer, defineComponent, nextTick, type App } from 'vue';
import { useFullMigration } from './useFullMigration';
import * as api from '../api/fullMigration';
import { invalidateSessionRequests } from '../api/client';

vi.mock('../api/fullMigration', async original => ({ ...await original<object>(), discover: vi.fn(), acquire: vi.fn(),
  acquisitions: vi.fn(), packages: vi.fn(), operations: vi.fn(), plan: vi.fn(), start: vi.fn(), credentials: vi.fn(), cancelAcquisition: vi.fn(), cancelOperation: vi.fn() }));
let app: App | undefined;
let state: ReturnType<typeof useFullMigration>;
const packageId = '11111111-1111-1111-1111-111111111111';
const key = (id: string) => api.resourceKey(packageId, id);
const pack = { id: packageId, provider: 'adapanel', sourceName: 'source', createdAtUtc: '', complete: true, issues: [],
  sites: [{ id: 'site', name: 'one.test', siteIds: [], runtimeProfile: 'php', sourcePhysicalPath: 'D:\\one', hostNames: [] }],
  databases: [{ id: 'db', name: 'independent', engine: 'mariadb', format: 'sql', siteIds: [] }],
  mailDomains: [{ id: 'mail', name: 'one.test', siteIds: ['site'], active: true, accounts: [] }], dnsZones: [{ id: 'dns', name: 'one.test', siteIds: ['site'] }],
  tasks: [{ id: 'task', name: 'job', siteIds: ['site'] }] } satisfies api.PackageSummary;
const job = { id: 'job-1', state: 'pending', packageIds: [], warnings: [], remoteRecoveries: [], packageRecoveries: [] } as unknown as api.Acquisition;
const validPlan = { id: 'plan-1', planHash: 'a'.repeat(64), expiresAtUtc: '2100-01-01T00:00:00Z', canApply: true, items: [], blockers: [], requiredActions: [] } as unknown as api.Plan;
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(r => { resolve = r; }); return { promise, resolve }; }
async function flush() { for (let i = 0; i < 15; i++) await nextTick(); }
async function mount() {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp(defineComponent({ setup() { state = useFullMigration(); return () => null; } })); app.mount({}); await flush();
}
beforeEach(() => { vi.useFakeTimers(); vi.clearAllMocks(); vi.mocked(api.acquisitions).mockResolvedValue([]); vi.mocked(api.operations).mockResolvedValue([]); vi.mocked(api.packages).mockResolvedValue([structuredClone(pack)]); });
afterEach(() => { app?.unmount(); app = undefined; vi.useRealTimers(); });

it('keeps independent databases selectable while excluding unselected site/mail/task mappings', async () => {
  await mount(); state.selection.packageIds = [packageId]; await flush();
  expect(state.selection.mappings.find(x => x.resourceId === key('site'))?.runtimeProfile).toBe('php');
  state.selection.allSites = false; state.selection.includeTasks = false;
  expect(state.selectedResources.value.map(x => x.kind)).toEqual(['database']);
  vi.mocked(api.plan).mockResolvedValue(validPlan); await state.preparePlan();
  const body = vi.mocked(api.plan).mock.calls[0][0];
  expect(body.mappings.map(x => x.resourceId)).toEqual([key('db')]);
  expect(body.allDatabases).toBe(true); expect(body.siteIds).toEqual([]);
});
it('selects mail and DNS only for selected sites; empty optional task selection explicitly means all', () => {
  const selection = { packageIds: [packageId], allSites: false, siteIds: [key('site')], allDatabases: false, databaseIds: [], includeMail: true, includeDns: true, includeTasks: true, taskIds: [] } as unknown as api.PlanRequest;
  expect([...api.selectedResourceKeys([pack], selection)]).toEqual([key('site'), key('mail'), key('dns'), key('task')]);
  selection.includeTasks = false; selection.siteIds = [];
  expect([...api.selectedResourceKeys([pack], selection)]).toEqual([]);
});
it('includes editable DNS site associations in the plan and removes them when DNS is excluded', async () => {
  await mount(); state.selection.packageIds = [packageId]; await flush();
  expect(state.resources.value.find(x => x.kind === 'dns')?.key).toBe(key('dns'));
  state.mapping(key('dns'), 'one.test').linkedSiteId = key('site');
  vi.mocked(api.plan).mockResolvedValue(validPlan); await state.preparePlan();
  expect(vi.mocked(api.plan).mock.calls[0][0].mappings.find(x => x.resourceId === key('dns'))?.linkedSiteId).toBe(key('site'));
  state.selection.includeDns = false; await state.preparePlan();
  expect(vi.mocked(api.plan).mock.calls[1][0].mappings.some(x => x.resourceId === key('dns'))).toBe(false);
});
it('invalidates a reviewed plan when its selection changes and refuses a late outdated plan', async () => {
  await mount(); const pending = deferred<api.Plan>(); vi.mocked(api.plan).mockReturnValue(pending.promise);
  const action = state.preparePlan(); state.selection.targetBasePath = 'D:\\new'; pending.resolve(validPlan); await action;
  expect(state.plan.value).toBeNull(); expect(api.start).not.toHaveBeenCalled();
  vi.mocked(api.plan).mockResolvedValue(validPlan); await state.preparePlan(); expect(state.plan.value?.canApply).toBe(true);
  state.selection.includeMail = false; expect(state.plan.value).toBeNull();
});
it('reuses the acquisition id after a lost response and clears source secrets only after acceptance', async () => {
  await mount(); state.source.host = 'https://source.test:8890'; state.source.token = 'private-token';
  vi.mocked(api.discover).mockResolvedValue({ provider: 'adapanel', adaPanel: { sites: [], databases: [], tasks: [], issues: [], complete: true } });
  await state.discover(); vi.mocked(api.acquire).mockRejectedValueOnce(new Error('network failure')).mockResolvedValueOnce(job);
  await state.acquire(); expect(state.source.token).toBe('private-token'); await state.acquire();
  expect(vi.mocked(api.acquire).mock.calls[0][0].clientRequestId).toBe(vi.mocked(api.acquire).mock.calls[1][0].clientRequestId);
  expect(state.source.token).toBe(''); expect(state.discovery.value).toBeNull();
});
for (const invalidation of ['session', 'unmount'] as const) {
  it(`discards late credentials after ${invalidation} and never fetches them during polling`, async () => {
    await mount(); expect(api.credentials).not.toHaveBeenCalled();
    const pending = deferred<api.Credential[]>(); vi.mocked(api.credentials).mockReturnValue(pending.promise);
    const action = state.showCredentials('operation');
    if (invalidation === 'session') invalidateSessionRequests(); else { app?.unmount(); app = undefined; }
    pending.resolve([{ itemId: 'db', kind: 'database', resourceName: 'db', userName: 'user', password: 'SECRET', host: 'localhost' }]);
    await action; expect(state.credentials.value).toEqual([]); expect(state.credentialOperation.value).toBe('');
    expect(api.cancelOperation).not.toHaveBeenCalled(); expect(api.cancelAcquisition).not.toHaveBeenCalled();
  });
}
it('discards discovery when the source address changes mid-request and clears accepted identity', async () => {
  await mount(); state.source.host = 'https://old.test'; state.acceptedFingerprint.value = 'SHA256:OLD';
  const pending = deferred<api.Discovery>(); vi.mocked(api.discover).mockReturnValue(pending.promise);
  const action = state.discover(); state.source.host = 'https://new.test'; pending.resolve({ provider: 'adapanel' }); await action;
  expect(state.discovery.value).toBeNull(); expect(state.acceptedFingerprint.value).toBe('');
});
it('shows the native TLS fingerprint and retries only with the explicitly accepted identity', async () => {
  await mount(); state.source.host = 'https://source.test:8890'; state.source.token = 'private-token';
  const fingerprint = 'SHA256:' + 'AB'.repeat(32);
  vi.mocked(api.discover).mockRejectedValueOnce({ isAxiosError: true, response: { status: 409, data: { message: 'migration.tls.untrusted', details: fingerprint } } })
    .mockResolvedValueOnce({ provider: 'adapanel', adaPanel: { sites: [], databases: [], tasks: [], issues: [], complete: true } });
  await state.discover(); expect(state.trust.value?.fingerprint).toBe(fingerprint); expect(state.discovery.value).toBeNull();
  expect(vi.mocked(api.discover).mock.calls[0][0].adaPanel?.acceptedCertificateFingerprint).toBe('');
  await state.acceptTrust(); expect(vi.mocked(api.discover).mock.calls[1][0].adaPanel?.acceptedCertificateFingerprint).toBe(fingerprint);
  expect(state.trust.value).toBeNull(); expect(state.discovery.value?.provider).toBe('adapanel');
});
it('shows the SSH first-connection fingerprint returned inside discovery without starting acquisition', async () => {
  await mount(); state.source.provider = 'plesk'; state.source.host = 'source.test'; state.source.password = 'private-password';
  const fingerprint = 'SHA256:' + 'A'.repeat(43);
  vi.mocked(api.discover).mockResolvedValue({ provider: 'plesk', hostingPanel: { supported: true, errorCode: 'migration.hostkey.untrusted', hostFingerprint: fingerprint, sourceVersion: '', units: [] } });
  await state.discover(); expect(state.trust.value?.fingerprint).toBe(fingerprint); expect(api.acquire).not.toHaveBeenCalled(); expect(state.discovery.value).toBeNull();
});
it('preserves last status and warns when one status request fails rather than showing an empty successful list', async () => {
  await mount(); state.operations.value = [{ id: 'last', state: 'partial', dataTransferred: false } as api.Operation];
  vi.mocked(api.operations).mockRejectedValue(new Error('unavailable')); await state.refresh();
  expect(state.operations.value[0].state).toBe('partial'); expect(state.pollError.value).toBe('panel.fullMigration.refreshFailed');
});
it('rejects duplicate DNS mapping keys before requesting a plan', async () => {
  await mount(); state.addressRows.value = [{ from: 'old.test', to: 'one.test' }, { from: 'old.test', to: 'two.test' }];
  await state.preparePlan(); expect(api.plan).not.toHaveBeenCalled(); expect(state.error.value).toBe('panel.fullMigration.addressInvalid');
});
it.each(['partial', 'failed', 'cancelled', 'interrupted'])('does not label %s as successful', state => {
  expect(api.outcomeKind({ state, dataTransferred: false })).toBe('error');
});
it('keeps completed-with-actions distinct from fully verified content', () => {
  expect(api.outcomeKind({ state: 'completed_with_actions', dataTransferred: true })).toBe('warning');
});
