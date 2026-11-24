import { describe, expect, it } from "vitest";
import { canUseSiteTool, SITE_TOOL_ROLES } from "./siteToolAccess";

/**
 * Bu harita backend'in `[Authorize(Roles = ...)]` listesiyle AYNI olmak zorunda. Biri kayarsa:
 *  - fazla gösterirsek müşteri tıklayıp 403 alır ("hiçbir şey olmayan kart"),
 *  - eksik gösterirsek backend'in izin verdiği özellik müşteriden saklanır (A1).
 */
describe("site tool access map", () => {
  const customerSees = ["hosting", "php", "ssl", "files", "protected", "stats", "backup", "mail", "wp", "logs"];
  const staffOnly = ["toolkit", "asp", "dns", "proxy", "ftp", "apps"];
  const adminOnly = ["webdb", "domaindb", "cron", "cache", "migrate", "store"];

  it("shows the customer exactly the tools the backend allows (SSL, statistics and WordPress included)", () => {
    for (const tool of customerSees) expect(canUseSiteTool(tool, "Customer"), tool).toBe(true);
    for (const tool of [...staffOnly, ...adminOnly]) expect(canUseSiteTool(tool, "Customer"), tool).toBe(false);
  });

  it("gives the reseller the customer tools plus the reseller-only ones, never the server-level ones", () => {
    for (const tool of [...customerSees, ...staffOnly]) expect(canUseSiteTool(tool, "Reseller"), tool).toBe(true);
    for (const tool of adminOnly) expect(canUseSiteTool(tool, "Reseller"), tool).toBe(false);
  });

  it("gives the administrator every mapped tool", () => {
    for (const tool of Object.keys(SITE_TOOL_ROLES)) expect(canUseSiteTool(tool, "Administrator"), tool).toBe(true);
  });

  it("denies by default: unknown tool, unknown role, missing role", () => {
    expect(canUseSiteTool("does-not-exist", "Administrator")).toBe(false);
    expect(canUseSiteTool("ssl", "Operator")).toBe(false);
    expect(canUseSiteTool("ssl", undefined)).toBe(false);
    expect(canUseSiteTool("ssl", null)).toBe(false);
  });

  it("covers every tool the workspace can open", () => {
    // WebsiteWorkspaceView'ın allTools kimlikleri: haritada olmayan araç sessizce HİÇ görünmezdi.
    const workspaceTools = ["hosting", "php", "asp", "dns", "ssl", "proxy", "files", "ftp", "webdb", "protected", "stats",
      "backup", "cron", "logs", "cache", "mail", "toolkit", "migrate", "store", "wp", "apps"];
    for (const tool of workspaceTools) expect(SITE_TOOL_ROLES[tool], tool).toBeDefined();
  });
});
