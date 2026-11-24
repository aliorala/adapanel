import { apiClient } from './client';
import { UserFacingError } from '../utils/apiError';

export type Provider = 'adapanel' | 'cpanel' | 'plesk';
export type Issue = { code: string; resourceId: string; detail: string };
export type Resource = { id: string; name: string; siteIds: string[]; mappingRequired?: boolean; count?: number };
export type SourceSite = Resource & { runtimeProfile: string; sourcePhysicalPath: string; hostNames: string[]; fileCount?: number | null };
export type SourceDatabase = Resource & { engine: string; format: string };
export type SourceMail = Resource & { active: boolean | null; accounts: { email: string; active: boolean | null; quotaMb: number }[] };
export type Inventory = { sites: SourceSite[]; databases: SourceDatabase[]; tasks: Resource[]; issues: Issue[]; complete: boolean };
export type Connection = { provider: Provider; adaPanel?: { baseUrl: string; apiToken: string; acceptedCertificateFingerprint: string }; ssh?: {
  provider: Provider; platform: 'linux'; host: string; sshPort: number; sshUsername: string; password: string;
  privateKey: string; privateKeyPassphrase: string; acceptedSshFingerprint: string;
} };
export type Discovery = { provider: Provider; adaPanel?: Inventory | null; hostingPanel?: { supported: boolean; errorCode: string; hostFingerprint: string; sourceVersion: string; units: Resource[] } | null };
export type ExportSelection = { allSites: boolean; siteIds: string[]; allDatabases: boolean; databaseIds: string[]; includeMail: boolean; includeDns: boolean; includeTasks: boolean; taskIds: string[] };
export type Acquisition = { id: string; clientRequestId: string; provider: Provider; sourceHost: string; state: string; stage: string;
  createdAtUtc: string; errorCode: string; cancellationRequested: boolean; packageIds: string[]; contentReady: boolean;
  normalizedPayloadBytes: number; downloadedBytes: number | null; warnings: string[];
  remoteRecoveries: { sourceUnitId: string; workspace: string; files: string[]; cleanupComplete: boolean; processCompletionConfirmed: boolean }[];
  packageRecoveries: { packageId: string; sourceUnitId: string; verified: boolean; requiredAction: string }[];
};
export type PackageSummary = Inventory & { id: string; provider: Provider; sourceName: string; createdAtUtc: string; mailDomains: SourceMail[]; dnsZones: Resource[] };
export type Mapping = { resourceId: string; targetName: string; targetPath?: string; runtimeProfile?: string; databaseUserName?: string; linkedSiteId?: string;
  unknownMailboxQuotaMb?: number | null; unknownMailboxActive?: boolean | null; unknownMailDomainActive?: boolean | null; unknownMailAliasActive?: boolean | null;
  taskCommand?: string; taskArguments?: string; taskWorkingDirectory?: string; taskUrl?: string; taskType?: string; taskScheduleType?: string;
  taskInterval?: number; taskTimeOfDay?: string; taskMappingConfirmed?: boolean };
export type PlanRequest = ExportSelection & { packageIds: string[]; targetBasePath: string; mappings: Mapping[]; addressMappings: Record<string, string>; acceptNewCredentials: boolean };
export type PlanItem = { id: string; packageId: string; kind: string; sourceId: string; sourceName: string; target: Mapping; blockers: string[]; warnings: string[] };
export type Plan = { id: string; planHash: string; expiresAtUtc: string; selection: PlanRequest; items: PlanItem[]; blockers: string[]; requiredActions: string[]; canApply: boolean };
export type ItemResult = { id: string; kind: string; sourceName: string; targetName: string; state: string; errorCode: string; partialStatePossible: boolean; createdResources: string[]; evidence: string[]; requiredActions: string[] };
export type Operation = { id: string; planId: string; state: string; createdAtUtc: string; cancellationRequested: boolean; items: ItemResult[]; requiredActions: string[]; credentialsAvailable: boolean; dataTransferred: boolean };
export type Credential = { itemId: string; kind: string; resourceName: string; userName: string; password: string; host: string };
type Envelope<T> = { success: boolean; data: T; message?: string };
const base = '/migration/full';
async function request<T>(method: 'get' | 'post', path: string, data?: unknown, signal?: AbortSignal): Promise<T> {
  const response = await apiClient.request<Envelope<T>>({ method, url: base + path, data, signal });
  if (!response.data.success) throw new UserFacingError(response.data.message || 'common.unexpected_error');
  return response.data.data;
}
export const discover = (connection: Connection, signal?: AbortSignal) => request<Discovery>('post', '/discover', connection, signal);
export const acquire = (body: { clientRequestId: string; connection: Connection; sourceUnitIds: string[]; adaPanelSelection: ExportSelection }, signal?: AbortSignal) => request<Acquisition>('post', '/acquisitions', body, signal);
export const acquisitions = (signal?: AbortSignal) => request<Acquisition[]>('get', '/acquisitions', undefined, signal);
export const packages = (signal?: AbortSignal) => request<PackageSummary[]>('get', '/packages', undefined, signal);
export const cancelAcquisition = (id: string, signal?: AbortSignal) => request<Acquisition>('post', `/acquisitions/${encodeURIComponent(id)}/cancel`, {}, signal);
export const plan = (body: PlanRequest, signal?: AbortSignal) => request<Plan>('post', '/plans', body, signal);
export const start = (body: { planId: string; planHash: string }, signal?: AbortSignal) => request<Operation>('post', '/operations', body, signal);
export const operations = (signal?: AbortSignal) => request<Operation[]>('get', '/operations', undefined, signal);
export const cancelOperation = (id: string, signal?: AbortSignal) => request<Operation>('post', `/operations/${encodeURIComponent(id)}/cancel`, {}, signal);
export const credentials = (id: string, signal?: AbortSignal) => request<Credential[]>('get', `/operations/${encodeURIComponent(id)}/credentials`, undefined, signal);

export const resourceKey = (packageId: string, id: string) => packageId.replace(/-/g, '').toLowerCase() + ':' + id;
export const isRunning = (state: string) => state === 'pending' || state === 'running';
export function selectedResourceKeys(packages: PackageSummary[], selection: PlanRequest): Set<string> {
  const result = new Set<string>();
  for (const pack of packages.filter(p => selection.packageIds.includes(p.id))) {
    const key = (id: string) => resourceKey(pack.id, id);
    const sites = new Set(pack.sites.filter(s => selection.allSites || selection.siteIds.includes(key(s.id))).map(s => s.id));
    for (const site of sites) result.add(key(site));
    for (const db of pack.databases) if (selection.allDatabases || selection.databaseIds.includes(key(db.id))) result.add(key(db.id));
    for (const mail of pack.mailDomains) if (selection.includeMail && mail.siteIds.some(id => sites.has(id))) result.add(key(mail.id));
    for (const dns of pack.dnsZones) if (selection.includeDns && dns.siteIds.some(id => sites.has(id))) result.add(key(dns.id));
    for (const task of pack.tasks) if (selection.includeTasks && (!selection.taskIds.length || selection.taskIds.includes(key(task.id)))) result.add(key(task.id));
  }
  return result;
}
export function outcomeKind(operation: Pick<Operation, 'state' | 'dataTransferred'>): 'success' | 'warning' | 'error' | 'running' {
  if (isRunning(operation.state)) return 'running';
  if (!operation.dataTransferred) return 'error';
  return operation.state === 'completed' ? 'success' : 'warning';
}
