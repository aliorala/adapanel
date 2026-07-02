<template>
  <section class="panel module-shell">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.database.transfer.title") }}</h3>
        <p class="meta">{{ t("panel.database.transfer.subtitle") }}</p>
      </div>
    </div>

    <p v-if="notice" :class="['notice', { error: noticeError }]">{{ notice }}</p>
    <div v-if="created.userName" class="notice cred-box">
      <strong>{{ t("panel.database.transfer.userCreated") }}</strong>
      <div class="cred-line">{{ t("panel.database.transfer.userLabel") }}: <code>{{ created.userName }}</code></div>
      <div v-if="created.password" class="cred-line">{{ t("panel.database.transfer.passLabel") }}: <code>{{ created.password }}</code></div>
      <small>{{ t("panel.database.transfer.credHint") }}</small>
    </div>

    <div class="form-surface">
      <h4 class="group-head">{{ t("panel.database.transfer.sourceGroup") }}</h4>
      <div class="form-grid">
        <label>
          <span class="field-label">{{ t("panel.database.transfer.sourceHost") }}</span>
          <input v-model.trim="form.sourceHost" placeholder="185.7.242.225" :disabled="busy" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.database.transfer.sourcePort") }}</span>
          <input v-model.number="form.sourcePort" type="number" min="1" max="65535" :disabled="busy" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.database.transfer.sourceUser") }}</span>
          <input v-model.trim="form.sourceUserName" placeholder="root" :disabled="busy" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.database.transfer.sourcePassword") }}</span>
          <input v-model="form.sourcePassword" type="password" autocomplete="off" :disabled="busy" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.database.transfer.sourceDb") }}</span>
          <input v-model.trim="form.sourceDatabase" placeholder="eski_db" :disabled="busy" />
        </label>
      </div>

      <h4 class="group-head">{{ t("panel.database.transfer.targetGroup") }}</h4>
      <div class="form-grid">
        <label>
          <span class="field-label">{{ t("panel.database.transfer.engine") }}</span>
          <select v-model="form.engine" :disabled="busy">
            <option value="mariadb">MariaDB</option>
            <option value="mysql">MySQL</option>
          </select>
        </label>
        <label>
          <span class="field-label">{{ t("panel.database.transfer.targetDb") }}</span>
          <input v-model.trim="form.targetDatabase" :placeholder="form.sourceDatabase || 'yeni_db'" :disabled="busy" />
          <small class="field-hint">{{ t("panel.database.transfer.targetDbHint") }}</small>
        </label>
        <label>
          <span class="field-label">{{ t("panel.database.transfer.targetUser") }}</span>
          <input v-model.trim="form.targetUserName" autocomplete="off" :disabled="busy" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.database.transfer.targetPassword") }}</span>
          <input v-model="form.targetPassword" type="text" autocomplete="off" :disabled="busy" />
        </label>
      </div>
      <p class="hint">{{ t("panel.database.transfer.targetUserHint") }}</p>

      <div class="row-actions">
        <button :disabled="busy || !canRun" @click="run">
          {{ busy ? t("panel.database.transfer.running") : t("panel.database.transfer.run") }}
        </button>
      </div>

      <p v-if="busy" class="hint">{{ t("panel.database.transfer.runningHint") }}</p>
      <p class="hint">{{ t("panel.database.transfer.help") }}</p>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import { transferDatabase } from "../api/databases";
import { extractApiDetails, extractApiMessage } from "../stores/auth";

const { t } = useI18n();
const busy = ref(false);
const notice = ref("");
const noticeError = ref(false);
const created = reactive({ userName: "", password: "" });
const form = reactive({
  engine: "mariadb",
  sourceHost: "",
  sourcePort: 3306,
  sourceUserName: "root",
  sourcePassword: "",
  sourceDatabase: "",
  targetDatabase: "",
  targetUserName: "",
  targetPassword: ""
});

const canRun = computed(() =>
  form.sourceHost.trim().length > 0 &&
  form.sourceUserName.trim().length > 0 &&
  form.sourceDatabase.trim().length > 0);

async function run() {
  if (!canRun.value) {
    return;
  }
  busy.value = true;
  showNotice("", false);
  created.userName = "";
  created.password = "";
  try {
    const result = await transferDatabase({
      engine: form.engine,
      sourceHost: form.sourceHost.trim(),
      sourcePort: form.sourcePort || 3306,
      sourceUserName: form.sourceUserName.trim(),
      sourcePassword: form.sourcePassword,
      sourceDatabase: form.sourceDatabase.trim(),
      targetDatabase: form.targetDatabase.trim(),
      targetUserName: form.targetUserName.trim(),
      targetPassword: form.targetPassword
    });
    created.userName = result.userName;
    created.password = result.password;
    showNotice(t("panel.database.transfer.done", { target: result.targetDatabase }), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

function resolveApiMessage(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  const message = translated === key ? key : translated;
  const details = extractApiDetails(error);
  return details ? `${message}\n\n${details}` : message;
}

function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}
</script>

<style scoped>
.cred-box {
  display: grid;
  gap: 4px;
}
.cred-box .cred-line code {
  font-family: ui-monospace, "Cascadia Code", Consolas, monospace;
  background: rgba(95, 227, 154, 0.12);
  color: var(--accent-1, #5fe39a);
  padding: 2px 8px;
  border-radius: 6px;
  user-select: all;
}
.cred-box small {
  opacity: 0.75;
  margin-top: 2px;
}

.group-head {
  margin: 4px 2px 10px;
  font-size: 0.84rem;
  color: var(--text-soft);
}

.group-head + .form-grid {
  margin-bottom: 18px;
}

.form-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 12px;
}

label {
  display: grid;
  gap: 6px;
}

select {
  border-radius: var(--radius-sm);
  border: 1px solid var(--panel-border);
  padding: 12px 14px;
  background: var(--input-bg);
  color: var(--text-strong);
  font: inherit;
}

.field-hint {
  color: var(--text-muted);
  font-size: 0.74rem;
}

.row-actions {
  margin-top: 14px;
}

.row-actions button {
  width: auto;
}

.hint {
  color: var(--text-muted, #94a3b8);
  font-size: 13px;
  margin-top: 10px;
  white-space: pre-line;
}
</style>
