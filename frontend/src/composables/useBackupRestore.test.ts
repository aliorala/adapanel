import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { useAuthStore, type CurrentUser } from "../stores/auth";
import type { BackupRecord } from "../api/backup";
import { restoreResult, useBackupRestore } from "./useBackupRestore";

const mocks = vi.hoisted(() => ({ confirm: vi.fn(), prompt: vi.fn(), restore: vi.fn(), chain: vi.fn() }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string, values?: Record<string, unknown>) => `${key}${values ? JSON.stringify(values) : ""}` }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: mocks.confirm }) }));
vi.mock("../components/PromptDialog.vue", () => ({ usePrompt: () => ({ prompt: mocks.prompt }) }));
vi.mock("../api/backup", () => ({ restoreBackup: mocks.restore, getBackupChain: mocks.chain }));
const backup = { id: "b1", name: "alpha-full", source: "alpha.example", encrypted: false, chainId: "", size: 1234, createdAtUtc: "2026-09-09T00:00:00Z", warnings: [], status: "restored", preservedPath: "", type: "site", path: "fixture.zip", format: "zip", targetType: "local", remoteUri: "", chainIndex: 0, backupMode: "full", baseBackupId: "", subscriptionUserId: "", itemCount: 1, localMissing: false, remoteMissing: false, remoteVerifiedUtc: null } satisfies BackupRecord;
beforeEach(() => {
  vi.resetAllMocks();
  setActivePinia(createPinia());
  useAuthStore().session = { isAuthenticated: true, requiresSetupWizard: false, user: { role: "Administrator" } as CurrentUser };
  mocks.restore.mockResolvedValue(backup);
  mocks.confirm.mockImplementation(async (options) => { await options.action(); return true; });
});
describe("shared full restore", () => {
  it("requires a typed target confirmation and forwards the password only to the restore request", async () => {
    mocks.prompt.mockResolvedValue("secret");
    const result = vi.fn();
    await useBackupRestore().restore({ ...backup, encrypted: true }, result, vi.fn());
    expect(mocks.confirm).toHaveBeenCalledWith(expect.objectContaining({ requireText: "alpha.example", impact: expect.arrayContaining(["alpha-full", "alpha.example"]) }));
    expect(mocks.restore).toHaveBeenCalledExactlyOnceWith("b1", "", "secret");
    expect(JSON.stringify(mocks.confirm.mock.calls)).not.toContain("secret");
    expect(result).toHaveBeenCalledWith(backup);
  });
  it("password cancellation makes no confirmation or restore request", async () => {
    mocks.prompt.mockResolvedValue(null);
    await useBackupRestore().restore({ ...backup, encrypted: true }, vi.fn(), vi.fn());
    expect(mocks.confirm).not.toHaveBeenCalled();
    expect(mocks.restore).not.toHaveBeenCalled();
  });
  it("confirmation cancellation makes no restore request", async () => {
    mocks.confirm.mockResolvedValue(false);
    await useBackupRestore().restore(backup, vi.fn(), vi.fn());
    expect(mocks.restore).not.toHaveBeenCalled();
  });
  it("does not offer full restore to a customer", async () => {
    useAuthStore().session!.user!.role = "Customer";
    await useBackupRestore().restore(backup, vi.fn(), vi.fn());
    expect(mocks.confirm).not.toHaveBeenCalled();
    expect(mocks.restore).not.toHaveBeenCalled();
  });
  it("blocks a broken incremental chain before password and confirmation", async () => {
    mocks.chain.mockResolvedValue({ broken: true, missingLinks: ["missing.zip"] });
    const error = vi.fn();
    await useBackupRestore().restore({ ...backup, chainId: "c1" }, vi.fn(), error);
    expect(error.mock.calls[0][0].message).toContain("missing.zip");
    expect(mocks.restore).not.toHaveBeenCalled();
    expect(mocks.confirm).not.toHaveBeenCalled();
  });
  it("does not hide a completed restore when the list refresh fails", async () => {
    const result = vi.fn();
    const error = vi.fn();
    await useBackupRestore().restore(backup, result, error, async () => { throw new Error("list offline"); });
    expect(result).toHaveBeenCalledWith(backup);
    expect(error).toHaveBeenCalledOnce();
  });
  it("reports partial status even without warnings and includes preserved files", () => {
    const result = restoreResult({ ...backup, status: "restored_with_errors", preservedPath: "C:/preserved" }, (key, values) => key + JSON.stringify(values ?? {}));
    expect(result.failed).toBe(true);
    expect(result.message).toContain("restoredWithErrors");
    expect(result.message).toContain("C:/preserved");
  });
});
