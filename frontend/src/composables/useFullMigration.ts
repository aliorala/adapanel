import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue';
import * as api from '../api/fullMigration';
import { getSessionEpoch } from '../api/client';
import { extractApiDetails, extractApiMessage } from '../stores/auth';
import { parseRemoteTrustPrompt, type RemoteTrustPrompt } from '../utils/remoteTrust';

export function useFullMigration() {
  const source = reactive({ provider: 'adapanel' as api.Provider, host: '', token: '', port: 22, user: 'root', password: '', privateKey: '', passphrase: '' });
  const acceptedFingerprint = ref('');
  const trust = ref<RemoteTrustPrompt | null>(null);
  const discovery = ref<api.Discovery | null>(null);
  const sourceUnits = ref<string[]>([]);
  const sourceSelection = reactive<api.ExportSelection>({ allSites: true, siteIds: [], allDatabases: true, databaseIds: [], includeMail: true, includeDns: true, includeTasks: false, taskIds: [] });
  const selection = reactive<api.PlanRequest>({ ...sourceSelection, siteIds: [], databaseIds: [], taskIds: [], packageIds: [], targetBasePath: 'C:\\inetpub\\wwwroot', mappings: [], addressMappings: {}, acceptNewCredentials: false });
  const addressRows = ref<{ from: string; to: string }[]>([]);
  const packages = ref<api.PackageSummary[]>([]), acquisitions = ref<api.Acquisition[]>([]), operations = ref<api.Operation[]>([]);
  const plan = ref<api.Plan | null>(null), credentials = ref<api.Credential[]>([]), credentialOperation = ref('');
  const busy = ref(false), error = ref(''), pollError = ref('');
  let alive = true, epoch = getSessionEpoch(), timer: ReturnType<typeof setTimeout> | undefined, polling = false;
  const controller = new AbortController();
  let sourceRevision = 0, planRevision = 0, requestId = '';
  function clearSecrets() { source.token = ''; source.password = ''; source.privateKey = ''; source.passphrase = ''; credentials.value = []; credentialOperation.value = ''; }
  function current() {
    if (alive && epoch === getSessionEpoch()) return true;
    clearSecrets(); discovery.value = null; packages.value = []; acquisitions.value = []; operations.value = []; plan.value = null;
    controller.abort(); return false;
  }
  watch(source, () => { sourceRevision++; requestId = ''; discovery.value = null; sourceUnits.value = []; acceptedFingerprint.value = ''; trust.value = null; }, { flush: 'sync' });
  watch(sourceSelection, () => { requestId = ''; }, { deep: true, flush: 'sync' });
  watch(sourceUnits, () => { requestId = ''; }, { deep: true, flush: 'sync' });
  watch([selection, addressRows], () => { planRevision++; plan.value = null; }, { deep: true, flush: 'sync' });
  const selectedPackages = computed(() => packages.value.filter(p => selection.packageIds.includes(p.id)));
  const resources = computed(() => selectedPackages.value.flatMap(p => [
    ...p.sites.map(r => ({ ...r, key: api.resourceKey(p.id, r.id), kind: 'site', packageName: p.sourceName })),
    ...p.databases.map(r => ({ ...r, key: api.resourceKey(p.id, r.id), kind: 'database', packageName: p.sourceName })),
    ...p.mailDomains.map(r => ({ ...r, key: api.resourceKey(p.id, r.id), kind: 'mail', packageName: p.sourceName })),
    ...p.dnsZones.map(r => ({ ...r, key: api.resourceKey(p.id, r.id), kind: 'dns', packageName: p.sourceName })),
    ...p.tasks.map(r => ({ ...r, key: api.resourceKey(p.id, r.id), kind: 'task', packageName: p.sourceName }))
  ]));
  const selectedResources = computed(() => {
    const keys = api.selectedResourceKeys(packages.value, selection);
    return resources.value.filter(resource => keys.has(resource.key));
  });
  function connection(): api.Connection {
    return source.provider === 'adapanel'
      ? { provider: source.provider, adaPanel: { baseUrl: source.host.trim(), apiToken: source.token, acceptedCertificateFingerprint: acceptedFingerprint.value } }
      : { provider: source.provider, ssh: { provider: source.provider, platform: 'linux', host: source.host.trim(), sshPort: Number(source.port), sshUsername: source.user,
        password: source.password, privateKey: source.privateKey, privateKeyPassphrase: source.passphrase, acceptedSshFingerprint: acceptedFingerprint.value } };
  }
  async function action(work: () => Promise<void>) {
    if (busy.value || !current()) return;
    busy.value = true; error.value = '';
    try { await work(); } catch (failure) {
      if (current()) { const key = extractApiMessage(failure); error.value = key; trust.value = parseRemoteTrustPrompt(key, extractApiDetails(failure)); }
    } finally { busy.value = false; }
  }
  async function discover() { await action(async () => {
    const revision = sourceRevision, result = await api.discover(connection(), controller.signal);
    if (!current() || revision !== sourceRevision) return;
    const remote = result.hostingPanel;
    if (remote?.errorCode) { error.value = remote.errorCode; trust.value = parseRemoteTrustPrompt(remote.errorCode, remote.hostFingerprint); return; }
    discovery.value = result; sourceUnits.value = remote?.units.map(x => x.id) || []; trust.value = null;
  }); }
  async function acceptTrust() { if (!trust.value || busy.value) return; acceptedFingerprint.value = trust.value.fingerprint; trust.value = null; await discover(); }
  async function acquire() { await action(async () => {
    if (!discovery.value) return;
    requestId ||= crypto.randomUUID();
    const result = await api.acquire({ clientRequestId: requestId, connection: connection(), sourceUnitIds: [...sourceUnits.value], adaPanelSelection: JSON.parse(JSON.stringify(sourceSelection)) }, controller.signal);
    if (!current()) return;
    acquisitions.value = [result, ...acquisitions.value.filter(x => x.id !== result.id)];
    clearSecrets(); discovery.value = null;
    await refresh();
  }); }
  function mapping(key: string, name: string): api.Mapping {
    let entry = selection.mappings.find(x => x.resourceId === key);
    if (!entry) { entry = { resourceId: key, targetName: name }; selection.mappings.push(entry); }
    return entry;
  }
  watch(resources, items => {
    for (const item of items) {
      const entry = mapping(item.key, item.name);
      if (item.kind === 'site' && entry.runtimeProfile === undefined) entry.runtimeProfile = 'runtimeProfile' in item ? String(item.runtimeProfile || 'auto') : 'auto';
    }
    const keys = new Set(items.map(x => x.key));
    for (const field of ['siteIds', 'databaseIds', 'taskIds'] as const) {
      if (selection[field].some(key => !keys.has(key))) selection[field] = selection[field].filter(key => keys.has(key));
    }
  });
  async function preparePlan() { await action(async () => {
    const addressMappings: Record<string, string> = {};
    for (const row of addressRows.value) {
      if (!row.from.trim() && !row.to.trim()) continue;
      if (!row.from.trim() || !row.to.trim() || Object.prototype.hasOwnProperty.call(addressMappings, row.from.trim())) { error.value = 'panel.fullMigration.addressInvalid'; return; }
      addressMappings[row.from.trim()] = row.to.trim();
    }
    const revision = planRevision, body: api.PlanRequest = JSON.parse(JSON.stringify(selection));
    body.addressMappings = addressMappings;
    const selected = api.selectedResourceKeys(packages.value, body);
    body.mappings = body.mappings.filter(mapping => selected.has(mapping.resourceId));
    const result = await api.plan(body, controller.signal);
    if (current() && revision === planRevision) plan.value = result;
  }); }
  async function start() { await action(async () => {
    const approved = plan.value;
    if (!approved?.canApply || Date.parse(approved.expiresAtUtc) <= Date.now()) { error.value = 'panel.fullMigration.planExpired'; return; }
    const result = await api.start({ planId: approved.id, planHash: approved.planHash }, controller.signal);
    if (!current()) return;
    operations.value = [result, ...operations.value.filter(x => x.id !== result.id)];
    plan.value = null; credentials.value = []; credentialOperation.value = '';
  }); }
  async function cancel(kind: 'acquisition' | 'operation', id: string) { await action(async () => {
    if (kind === 'acquisition') { const result = await api.cancelAcquisition(id, controller.signal); if (current()) acquisitions.value = acquisitions.value.map(x => x.id === id ? result : x); }
    else { const result = await api.cancelOperation(id, controller.signal); if (current()) operations.value = operations.value.map(x => x.id === id ? result : x); }
  }); }
  async function showCredentials(id: string) { await action(async () => {
    credentials.value = []; credentialOperation.value = '';
    const result = await api.credentials(id, controller.signal);
    if (current()) { credentials.value = result; credentialOperation.value = id; }
  }); }
  function hideCredentials() { credentials.value = []; credentialOperation.value = ''; }
  async function refresh() {
    if (polling || !current()) return;
    polling = true;
    try {
      const results = await Promise.allSettled([api.acquisitions(controller.signal), api.operations(controller.signal), api.packages(controller.signal)]);
      if (!current()) return;
      const [a, o, p] = results;
      if (a.status === 'fulfilled') acquisitions.value = a.value;
      if (o.status === 'fulfilled') operations.value = o.value;
      if (p.status === 'fulfilled') packages.value = p.value;
      pollError.value = results.some(x => x.status === 'rejected') ? 'panel.fullMigration.refreshFailed' : '';
    } finally { polling = false; }
  }
  async function poll() { await refresh(); if (current()) timer = setTimeout(poll, 5000); }
  onMounted(poll);
  onBeforeUnmount(() => { alive = false; if (timer) clearTimeout(timer); controller.abort(); clearSecrets(); });
  return { source, sourceUnits, sourceSelection, acceptedFingerprint, trust, discovery, selection, addressRows, packages, resources, selectedResources,
    acquisitions, operations, plan, credentials, credentialOperation, busy, error, pollError, discover, acceptTrust, acquire, mapping,
    preparePlan, start, cancel, showCredentials, hideCredentials, refresh };
}
