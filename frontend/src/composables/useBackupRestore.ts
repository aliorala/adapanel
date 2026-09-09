import { getSessionEpoch } from "../api/client";
import { ref } from "vue";
import { useI18n } from "vue-i18n";
import { getBackupChain, restoreBackup, type BackupRecord } from "../api/backup";
import { useAuthStore } from "../stores/auth";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { usePrompt } from "../components/PromptDialog.vue";
import { formatBytes } from "../utils/bytes";
import { formatDateTime } from "../utils/datetime";

export function restoreResult(record: BackupRecord, t: (key: string, values?: Record<string, unknown>) => string) {
  const failed = record.status === "restored_with_errors" || (record.warnings ?? []).length > 0;
  const lines = [t(failed ? "panel.backup.restoredWithErrors" : "panel.backup.restored")];
  for (const warning of record.warnings ?? []) {
    const message = t(warning.messageKey, { target: warning.target });
    lines.push(warning.target && !message.includes(warning.target) ? `${warning.target}: ${message}` : message);
    if (warning.detail) lines.push(warning.detail);
  }
  if (record.preservedPath) lines.push(t("panel.backup.preservedPath", { path: record.preservedPath }));
  return { message: lines.join("\n"), failed };
}

/** Full restore has one authorization, password, chain and confirmation flow in every view. */
export function useBackupRestore() {
  const { t } = useI18n();
  const auth = useAuthStore();
  const { confirm } = useConfirm();
  const { prompt } = usePrompt();
  const restoringId = ref("");
  async function restore(record: BackupRecord, onResult: (record: BackupRecord) => void, onError: (error: unknown) => void, refresh?: () => Promise<unknown>) {
    if (restoringId.value || auth.session?.user?.role !== "Administrator") return;
    const epoch = getSessionEpoch();
    restoringId.value = record.id;
    let password = "";
    try {
      if (record.chainId) {
        const chain = await getBackupChain(record.id);
        if (chain.broken) throw new Error(`${t("panel.backup.chain.broken")}: ${chain.missingLinks.join(", ")}`);
      }
      if (record.encrypted) {
        const entered = await prompt({ title: t("panel.backup.restorePasswordTitle"), message: t("panel.backup.encryptionLossWarning"), label: t("panel.backup.encryptionPassword"), hint: t("panel.backup.restorePasswordHint"), password: true, allowEmpty: true, confirmLabel: t("panel.backup.restore") });
        if (entered === null) return;
        password = entered;
      }
      if (epoch !== getSessionEpoch()) return;
      await confirm({
        title: t("panel.confirm.restoreBackupTitle", { name: record.name }),
        message: t("panel.backup.restoreConfirm"),
        impact: [record.source, record.name, formatBytes(record.size, t), formatDateTime(record.createdAtUtc)].filter(Boolean),
        requireText: record.source || record.name,
        confirmLabel: t("panel.backup.restore"), busyLabel: t("panel.backup.restoring"),
        action: async () => {
          if (epoch !== getSessionEpoch() || auth.session?.user?.role !== "Administrator") return;
          try {
            const result = await restoreBackup(record.id, "", password);
            // Refresh failure must not hide the restore result or imply the restore failed.
            if (refresh) await refresh().catch(onError);
            onResult(result);
          } catch (error) { onError(error); }
        }
      });
    } catch (error) { onError(error); }
    finally { password = ""; restoringId.value = ""; }
  }
  return { restore, restoringId };
}
