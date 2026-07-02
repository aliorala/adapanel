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

      <div class="update-github">
        <h3>{{ t("panel.update.githubTitle") }}</h3>
        <p class="meta">{{ t("panel.update.githubHint") }}</p>
        <div class="github-row">
          <input v-model.trim="githubRepo" placeholder="aliorala/adapanel" spellcheck="false" />
          <button class="secondary compact" :disabled="Boolean(busy)" @click="saveGithub">
            {{ busy === "github" ? t("panel.update.githubSaving") : t("panel.update.githubSave") }}
          </button>
        </div>
      </div>

      <dl class="detail-grid">
        <div><dt>{{ t("panel.update.notes") }}</dt><dd>{{ translate(status.notes) }}</dd></div>
        <div><dt>{{ t("panel.update.sha256") }}</dt><dd>{{ status.packageSha256 || "-" }}</dd></div>
        <div><dt>{{ t("panel.update.packagePath") }}</dt><dd>{{ status.packagePath || "-" }}</dd></div>
      </dl>

      <div class="update-actions">
        <button :disabled="!status.downloadUrl || !status.packageSha256 || Boolean(busy)" @click="downloadUpdate">
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
import { onMounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import { applyUpdate, checkForUpdate, installPreparedUpdate, setUpdateGithubRepo, type UpdateStatus } from "../api/update";
import { extractApiMessage } from "../stores/auth";

const { t } = useI18n();
const status = ref<UpdateStatus | null>(null);
const busy = ref<"" | "download" | "install" | "github">("");
const notice = ref("");
const noticeError = ref(false);
const githubRepo = ref("");

onMounted(async () => {
  await refresh();
});

async function refresh() {
  try {
    status.value = await checkForUpdate();
    githubRepo.value = status.value.githubRepo;
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

// GitHub deposunu (owner/repo) kaydet → panel en son Release'i buradan otomatik görür.
async function saveGithub() {
  busy.value = "github";
  try {
    status.value = await setUpdateGithubRepo(githubRepo.value);
    githubRepo.value = status.value.githubRepo;
    showNotice(t("panel.update.githubSaved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = "";
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
  if (!status.value?.isPackageReady || !window.confirm(t("panel.update.installConfirm"))) {
    return;
  }

  busy.value = "install";
  try {
    status.value = await installPreparedUpdate();
    showNotice(t("panel.update.installScheduled"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = "";
  }
}

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

.update-github {
  display: grid;
  gap: 6px;
  padding: 14px 16px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--surface-1);
}

.update-github h3 {
  margin: 0;
}

.github-row {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-top: 4px;
}

.github-row input {
  flex: 1 1 240px;
  font-family: ui-monospace, "Cascadia Code", Consolas, monospace;
}

.github-row button {
  flex: none;
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
