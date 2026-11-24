<template>
  <div class="git-extras">
    <p v-if="notice" :class="['notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <h5>{{ t("panel.git.keyTitle") }}</h5>
    <p class="field-hint">{{ t("panel.git.keyHint") }}</p>
    <div v-if="deployment.hasDeployKey" class="settings-rows">
      <label class="setting-row align-start">
        <span>{{ t("panel.git.publicKey") }}</span>
        <textarea :value="deployment.deployPublicKey" rows="3" readonly></textarea>
        <small>{{ deployment.deployKeyFingerprint }}</small>
      </label>
    </div>
    <div class="action-row">
      <button class="secondary compact" v-bind="writeGate(busy)" @click="run(() => generateGitDeployKey(siteName), 'panel.git.keyGenerated')">
        {{ deployment.hasDeployKey ? t("panel.git.keyRegenerate") : t("panel.git.keyGenerate") }}
      </button>
      <button v-if="deployment.hasDeployKey" class="secondary compact danger" v-bind="writeGate(busy)" @click="run(() => removeGitDeployKey(siteName), 'panel.git.keyRemoved')">
        {{ t("panel.git.keyRemove") }}
      </button>
    </div>

    <h5>{{ t("panel.git.tokenTitle") }}</h5>
    <p class="field-hint">{{ t("panel.git.tokenHint") }}</p>
    <div class="settings-rows">
      <label class="setting-row">
        <span>{{ t("panel.git.tokenUser") }}</span>
        <input v-model.trim="tokenUser" autocomplete="off" spellcheck="false" />
      </label>
      <label class="setting-row">
        <span>{{ t("panel.git.tokenValue") }}</span>
        <input v-model="tokenValue" type="password" autocomplete="new-password" spellcheck="false" :placeholder="deployment.hasHttpsToken ? t('panel.git.tokenStored') : ''" />
      </label>
    </div>
    <div class="action-row">
      <button class="secondary compact" v-bind="writeGate(busy || !tokenValue || !tokenUser)" @click="saveToken">{{ t("panel.git.tokenSave") }}</button>
      <button v-if="deployment.hasHttpsToken" class="secondary compact danger" v-bind="writeGate(busy)" @click="run(() => removeGitHttpsToken(siteName), 'panel.git.tokenRemoved')">
        {{ t("panel.git.tokenRemove") }}
      </button>
    </div>

    <h5>{{ t("panel.git.postTitle") }}</h5>
    <p class="field-hint">{{ t("panel.git.postHint") }}</p>
    <div class="settings-rows">
      <label v-for="id in deployment.allowedPostDeployCommands" :key="id" class="checkbox-field">
        <input v-model="postCommands" type="checkbox" :value="id" />
        <span>{{ t(`panel.git.post.${id}`) }}</span>
      </label>
      <label class="setting-row">
        <span>{{ t("panel.git.postTimeout") }}</span>
        <input v-model.number="postTimeout" type="number" min="30" max="1800" />
      </label>
    </div>
    <div class="action-row">
      <button class="secondary compact" v-bind="writeGate(busy)" @click="savePost">{{ t("panel.git.postSave") }}</button>
    </div>

    <h5>{{ t("panel.git.historyTitle") }}</h5>
    <p v-if="!deployment.recentDeployments.length" class="field-hint">{{ t("panel.git.historyEmpty") }}</p>
    <div v-else class="data-table">
      <div v-for="entry in deployment.recentDeployments" :key="entry.id" class="data-row">
        <div class="cell-main">
          <strong>{{ new Date(entry.deployedAtUtc).toLocaleString(getFormattingLocale()) }}</strong>
          <small>{{ t(`panel.git.trigger.${entry.trigger}`) }} — {{ entry.newCommit.slice(0, 8) || "-" }}</small>
          <small v-if="entry.localChangeCount > 0">{{ t("panel.git.localChanges", { count: entry.localChangeCount, ref: entry.savedChangesRef || "-" }) }}</small>
          <small v-if="entry.postDeploy">{{ entry.postDeploy }}</small>
        </div>
        <span :class="['status-pill', entry.success ? 'ok' : 'warn']">{{ entry.success ? t("panel.git.ok") : t("panel.git.failed") }}</span>
        <button v-if="entry.previousCommit" class="secondary compact" v-bind="writeGate(busy)" @click="rollback(entry)">{{ t("panel.git.rollback") }}</button>
      </div>
    </div>

    <h5>{{ t("panel.git.removeTitle") }}</h5>
    <div class="action-row">
      <button class="secondary compact danger" v-bind="writeGate(busy)" @click="removeConfig">{{ t("panel.git.removeAction") }}</button>
    </div>
    <ul v-if="leftovers.length" class="field-hint" role="alert">
      <li v-for="item in leftovers" :key="item">{{ item }}</li>
    </ul>
  </div>
</template>

<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { isCancel } from "axios";
import {
  generateGitDeployKey, removeGitDeployKey, removeGitDeployment, removeGitHttpsToken, rollbackGit,
  setGitHttpsToken, setGitPostDeploy,
  type GitDeployHistory, type GitDeployment
} from "../api/git";
import { useConfirm } from "./ConfirmDialog.vue";
import { extractApiMessage } from "../stores/auth";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";
import { getFormattingLocale } from "../utils/locale";

const props = defineProps<{ siteName: string; deployment: GitDeployment }>();
const emit = defineEmits<{ (e: "updated", deployment: GitDeployment): void; (e: "removed"): void }>();

const { t } = useI18n();
const { confirm } = useConfirm();
const { writeGate } = useSubscriptionGate();
const busy = ref(false);
const notice = ref("");
const noticeError = ref(false);
const leftovers = ref<string[]>([]);
const tokenUser = ref(props.deployment.httpsUsername || "x-access-token");
const tokenValue = ref("");
const postCommands = ref<string[]>([...props.deployment.postDeployCommands]);
const postTimeout = ref(props.deployment.postDeployTimeoutSeconds || 600);
let disposed = false;
onBeforeUnmount(() => { disposed = true; tokenValue.value = ""; });

// Sunucudan yeni durum gelince taslak formu ona çek (başka sekme/yenileme).
watch(() => props.deployment, value => {
  postCommands.value = [...value.postDeployCommands];
  postTimeout.value = value.postDeployTimeoutSeconds || 600;
});

function show(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}

function resolveMessage(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  return translated === key ? key : translated;
}

async function run(operation: () => Promise<GitDeployment>, successKey: string) {
  const site = props.siteName;
  busy.value = true;
  try {
    const result = await operation();
    if (disposed || site !== props.siteName) return;
    emit("updated", result);
    show(t(successKey), false);
  } catch (error) {
    if (!disposed && !isCancel(error)) show(resolveMessage(error), true);
  } finally {
    if (!disposed) busy.value = false;
  }
}

async function saveToken() {
  const value = tokenValue.value;
  // Jeton alanı gönderimden HEMEN sonra temizlenir: ekranda/bellekte kalmasın.
  await run(() => setGitHttpsToken(props.siteName, tokenUser.value, value), "panel.git.tokenSaved");
  tokenValue.value = "";
}

async function savePost() {
  await run(() => setGitPostDeploy(props.siteName, postCommands.value, postTimeout.value), "panel.git.postSaved");
}

async function rollback(entry: GitDeployHistory) {
  const site = props.siteName;
  await confirm({
    title: t("panel.git.rollbackTitle"),
    message: t("panel.git.rollbackConfirm", { commit: entry.previousCommit.slice(0, 8) }),
    impact: [t("panel.git.rollbackImpact1"), t("panel.git.rollbackImpact2")],
    action: async () => {
      busy.value = true;
      try {
        const result = await rollbackGit(site, entry.id);
        if (disposed || site !== props.siteName) return;
        emit("updated", result.deployment);
        show(result.success ? t("git.rolled_back") : t("git.deploy_failed"), !result.success);
      } catch (error) {
        if (!disposed && !isCancel(error)) show(resolveMessage(error), true);
      } finally {
        if (!disposed) busy.value = false;
      }
    }
  });
}

async function removeConfig() {
  const site = props.siteName;
  await confirm({
    title: t("panel.git.removeConfirmTitle", { name: site }),
    message: t("panel.git.removeConfirm"),
    impact: [t("panel.git.removeImpact1"), t("panel.git.removeImpact2"), t("panel.git.removeImpact3")],
    requireText: site,
    action: async () => {
      busy.value = true;
      try {
        const result = await removeGitDeployment(site, site);
        if (disposed || site !== props.siteName) return;
        leftovers.value = result.leftovers;
        show(result.leftovers.length ? t("git.removed_partial") : t("git.removed"), result.leftovers.length > 0);
        emit("removed");
      } catch (error) {
        if (!disposed && !isCancel(error)) show(resolveMessage(error), true);
      } finally {
        if (!disposed) busy.value = false;
      }
    }
  });
}
</script>
