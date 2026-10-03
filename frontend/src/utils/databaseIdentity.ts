/** Mirrors the server's database identity rules for operation-result checks. */
export function databaseIdentity(engine: string, name: string): string {
  const rawEngine = engine.trim().toLowerCase();
  const canonicalEngine = rawEngine === "postgres" ? "postgresql" : rawEngine === "sqlserver" ? "mssql" : rawEngine;
  const databaseName = name.trim();
  return `${canonicalEngine}|${canonicalEngine === "postgresql" ? databaseName : databaseName.toLowerCase()}`;
}
