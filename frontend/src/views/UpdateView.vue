<template>
  <section class="panel update-panel">
    <div class="section-head">
      <div>
        <h2>{{ t("panel.update.title") }}</h2>
        <p class="meta">{{ t("panel.update.subtitle") }}</p>
      </div>
      <button class="secondary compact" @click="refresh">
        {{ t("panel.update.refresh") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', { error: noticeError }]">{{ notice }}</p>

    <template v-if="status">
      <div class="update-summary">
        <article>
          <span>{{ t("panel.update.current") }}</span>
          <strong>{{ status.currentVersion }}</strong>
        </article>
        <article>
          <span>{{ t("panel.update.latest") }}</span>
          <strong>{{ status.latestVersion }}</strong>
        </article>
        <article>
          <span>{{ t("panel.update.packageState") }}</span>
          <strong>
            {{
              status.isInstallScheduled
                ? t("panel.update.scheduled")
                : status.isPackageReady
                  ? t("panel.update.ready")
                  : t("panel.update.notReady")
            }}
          </strong>
        </article>
      </div>

      <p v-if="!status.isUpdateAvailable" class="meta up-to-date">✓ {{ t("panel.update.upToDate") }}</p>

      <dl class="detail-grid">
        <div><dt>{{ t("panel.update.notes") }}</dt><dd>{{ translate(status.notes) }}</dd></div>
        <div><dt>{{ t("panel.update.sha256") }}</dt><dd>{{ status.packageSha256 || "-" }}</dd></div>
        <div><dt>{{ t("panel.update.packagePath") }}</dt><dd>{{ status.packagePath || "-" }}</dd></div>
      </dl>

      <div class="update-actions">
        <button
          :disabled="!status.downloadUrl || !status.packageSha256 || !status.isUpdateAvailable || Boolean(busy)"
          @click="downloadUpdate">
          {{ busy === "download" ? t("panel.webdb.deploying") : t("panel.update.download") }}
        </button>
        <button
          class="danger"
          :disabled="!status.isPackageReady || status.isInstallScheduled || Boolean(busy)"
          @click="installUpdate"
        >
          {{ busy === "install" ? t("panel.update.scheduling") : t("panel.update.install") }}
        </button>
        <p class="meta">{{ t("panel.update.safeApplyHint") }}</p>
      </div>
    </template>
  </section>
</template>

<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import { applyUpdate, checkForUpdate, installPreparedUpdate, type UpdateStatus } from "../api/update";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { extractApiMessage } from "../stores/auth";

const { t } = useI18n();
const { confirm } = useConfirm();
const status = ref<UpdateStatus | null>(null);
const busy = ref<"" | "download" | "install">("");
const notice = ref("");
const noticeError = ref(false);

onMounted(async () => {
  await refresh();
});

// NOT: Güncelleme kaynağı (GitHub depo) ayarı kullanıcı isteğiyle UI'dan KALDIRILDI; backend
// varsayılanı (SelfUpdateService.DefaultRepo) kullanılır, gerekirse config/update-github.json'dan değişir.
async function refresh() {
  try {
    status.value = await checkForUpdate();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function downloadUpdate() {
  if (!status.value) {
    return;
  }

  busy.value = "download";
  try {
    status.value = await applyUpdate(status.value.downloadUrl, status.value.packageSha256);
    showNotice(t("panel.update.staged"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = "";
  }
}

async function installUpdate() {
  if (!status.value?.isPackageReady) {
    return;
  }

  // Yıkıcı değil (silme yok, yalnız servis yeniden başlar) → nötr ton, ad yazdırma yok.
  const approved = await confirm({
    title: t("panel.confirm.installUpdateTitle", { name: status.value.latestVersion }),
    message: t("panel.update.installConfirm"),
    impact: [
      `${t("panel.update.current")}: ${status.value.currentVersion}`,
      `${t("panel.update.latest")}: ${status.value.latestVersion}`
    ],
    tone: "neutral",
    confirmLabel: t("panel.update.install"),
    busyLabel: t("panel.update.scheduling"),
  });
  if (!approved) {
    return;
  }

  const versionBeforeInstall = status.value.currentVersion;
  busy.value = "install";
  try {
    status.value = await installPreparedUpdate();
    showNotice(t("panel.update.waitingRestart"), false);
    startPostInstallWatch(versionBeforeInstall);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = "";
  }
}

// Kurulum planlandiktan sonra paneli izle: servis yeniden başlarken istekler düşer (sessizce beklenir),
// panel YENİ sürümle dönünce sayfa otomatik yenilenir (Plesk davranışı). Oturum düşmüşse (401) login'e döner.
let postInstallHandle: number | null = null;

function startPostInstallWatch(versionBeforeInstall: string) {
  stopPostInstallWatch();
  const startedAt = Date.now();
  postInstallHandle = window.setInterval(async () => {
    if (Date.now() - startedAt > 5 * 60_000) {
      stopPostInstallWatch();
      showNotice(t("panel.update.updateTimeout"), true);
      return;
    }

    try {
      const fresh = await checkForUpdate();
      if (fresh.currentVersion && fresh.currentVersion !== versionBeforeInstall) {
        stopPostInstallWatch();
        status.value = fresh;
        showNotice(t("panel.update.updated"), false);
        window.setTimeout(() => window.location.reload(), 1500);
      }
    } catch (error) {
      if ((error as { response?: { status?: number } })?.response?.status === 401) {
        stopPostInstallWatch();
        window.location.reload();
      }
    }
  }, 5000);
}

function stopPostInstallWatch() {
  if (postInstallHandle !== null) {
    window.clearInterval(postInstallHandle);
    postInstallHandle = null;
  }
}

onBeforeUnmount(stopPostInstallWatch);

function translate(key: string) {
  const translated = t(key);
  return translated === key ? key : translated;
}

function resolveApiMessage(error: unknown) {
  return translate(extractApiMessage(error));
}

function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}
</script>

<style scoped>
.update-panel,
.detail-grid,
.update-summary,
.update-actions {
  display: grid;
  gap: 16px;
}

.up-to-date {
  padding: 10px 14px;
  border: 1px solid color-mix(in srgb, var(--ok-1, #22c55e) 40%, var(--panel-border));
  border-radius: var(--radius-sm);
  background: color-mix(in srgb, var(--ok-1, #22c55e) 8%, var(--surface-1));
  color: var(--text-strong);
}

.section-head {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  align-items: flex-start;
}

.update-summary {
  grid-template-columns: repeat(3, minmax(0, 1fr));
}

.update-summary article {
  display: grid;
  gap: 5px;
  padding: 14px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  background: var(--input-bg);
}

.update-summary span {
  color: var(--text-muted);
}

.detail-grid {
  grid-template-columns: repeat(3, minmax(0, 1fr));
  margin: 0;
}

.detail-grid div {
  display: grid;
  gap: 4px;
}

.meta,
.detail-grid dt {
  color: var(--text-muted);
}

.detail-grid dd,
.meta {
  margin: 0;
}

.detail-grid dd {
  word-break: break-word;
}

.update-actions {
  grid-template-columns: max-content max-content minmax(0, 1fr);
  align-items: center;
}

.update-actions button {
  width: fit-content;
}

@media (max-width: 900px) {
  .update-summary,
  .detail-grid {
    grid-template-columns: 1fr;
  }

  .update-actions {
    grid-template-columns: 1fr;
  }

  .update-actions button {
    width: 100%;
  }
}
</style>
