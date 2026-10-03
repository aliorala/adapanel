import type { BackupRecord } from "../api/backup";

export type BackupDeletionPlan = { targets: BackupRecord[]; ambiguous: boolean };

/** Matches BackupSourceIdentity and DatabaseResourceIdentity on the server. */
function sourceKey(record: BackupRecord) {
  const type = record.type.trim().toLowerCase();
  const separator = record.source.indexOf(":");
  if (type !== "database" || separator < 0) return JSON.stringify([type, record.source.toUpperCase()]);
  const rawEngine = record.source.slice(0, separator).trim().toLowerCase();
  const engine = rawEngine === "postgres" ? "postgresql" : rawEngine === "sqlserver" ? "mssql" : rawEngine;
  const name = record.source.slice(separator + 1).trim();
  return JSON.stringify([type, engine, engine === "postgresql" ? name : name.toLowerCase()]);
}

/**
 * Preview the same exact-parent graph as the server. An unresolved relationship is
 * never a reason to enlarge a destructive action to every later sequence number.
 * The DELETE additionally carries these IDs as a server-checked precondition.
 */
export function getBackupDeletionPlan(records: readonly BackupRecord[], selected: BackupRecord): BackupDeletionPlan {
  const unknown = (): BackupDeletionPlan => ({ targets: [], ambiguous: true });
  const counts = new Map<string, number>();
  for (const record of records) counts.set(record.id, (counts.get(record.id) ?? 0) + 1);
  if (!selected.id?.trim() || counts.get(selected.id) !== 1) return unknown();
  const target = records.find(record => record.id === selected.id)!;
  if (!target.chainId?.trim()) return { targets: [target], ambiguous: false };

  const scope = sourceKey(target);
  const chain = records.filter(record => record.chainId?.toUpperCase() === target.chainId.toUpperCase() && sourceKey(record) === scope)
    .sort((a, b) => a.chainIndex - b.chainIndex || Date.parse(a.createdAtUtc) - Date.parse(b.createdAtUtc));
  const byId = new Map<string, BackupRecord>();
  const byIndex = new Map<number, BackupRecord[]>();
  for (const record of chain) {
    if (!record.id?.trim() || counts.get(record.id) !== 1 || !Number.isInteger(record.chainIndex) ||
      record.chainIndex < 0 || record.chainIndex > 2147483647) return unknown();
    byId.set(record.id, record);
    const bucket = byIndex.get(record.chainIndex) ?? [];
    bucket.push(record);
    byIndex.set(record.chainIndex, bucket);
  }
  if (byIndex.get(0)?.length !== 1) return unknown();

  const children = new Map<string, string[]>();
  for (const record of chain) {
    const parentId = record.baseBackupId ?? "";
    if (record.chainIndex === 0) {
      if (parentId.trim() || (record.backupMode?.trim() && record.backupMode.toLowerCase() !== "full")) return unknown();
      continue;
    }
    const candidates = byIndex.get(record.chainIndex - 1) ?? [];
    const parent = parentId.trim() ? byId.get(parentId) : candidates.length === 1 ? candidates[0] : undefined;
    if (!parent || parent.chainIndex !== record.chainIndex - 1) return unknown();
    const bucket = children.get(parent.id) ?? [];
    bucket.push(record.id);
    children.set(parent.id, bucket);
  }

  // Every edge advances exactly one sequence, so cycles cannot pass validation.
  const descendants = new Set<string>([target.id]), pending = [target.id];
  for (let index = 0; index < pending.length; index++) {
    for (const id of children.get(pending[index]) ?? []) {
      if (!descendants.has(id)) { descendants.add(id); pending.push(id); }
    }
  }
  return { targets: [target, ...chain.filter(record => record.id !== target.id && descendants.has(record.id))], ambiguous: false };
}
