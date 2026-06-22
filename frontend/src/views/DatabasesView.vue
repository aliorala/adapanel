<template>
  <section class="panel module-shell databases-panel">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.database.title") }}</h2>
        <p class="meta">{{ t("panel.database.subtitle") }}</p>
      </div>
    </div>

    <p v-if="notice" :class="['notice', { error: noticeError }]">
      {{ notice }}
    </p>

    <div class="segmented-tabs primary-tabs" role="tablist">
      <button :class="{ active: activeModule === 'databases' }" @click="activeModule = 'databases'">
        {{ t("panel.database.module.databases") }}
      </button>
      <button :class="{ active: activeModule === 'webdb' }" @click="activeModule = 'webdb'">
        {{ t("panel.database.module.webdb") }}
      </button>
      <button :class="{ active: activeModule === 'cache' }" @click="activeModule = 'cache'">
        {{ t("panel.database.module.cache") }}
      </button>
      <button :class="{ active: activeModule === 'transfer' }" @click="activeModule = 'transfer'">
        {{ t("panel.database.module.transfer") }}
      </button>
      <button :class="{ active: activeModule === 'settings' }" @click="activeModule = 'settings'">
        {{ t("panel.database.module.settings") }}
      </button>
    </div>

    <WebDbView v-if="activeModule === 'webdb'" />
    <CacheView v-else-if="activeModule === 'cache'" />
    <DbTransferView v-else-if="activeModule === 'transfer'" />
    <DbToolSettingsView v-else-if="activeModule === 'settings'" />

    <template v-else>
      <div class="module-toolbar engine-toolbar">
        <div class="segmented-tabs engine-tabs" role="tablist">
          <button
            v-for="engine in engines"
            :key="engine.key"
            type="button"
            role="tab"
            :title="engineDescription(engine.key)"
            :aria-selected="selectedEngine === engine.key"
            :class="{ active: selectedEngine === engine.key }"
            @click="selectEngine(engine.key)">
            <span>{{ engine.label }}</span>
            <i :class="['engine-state', engineBadgeClass(engine.key)]"></i>
          </button>
        </div>

        <div class="toolbar-group">
          <span :class="['status-pill', engineBadgeClass(selectedEngine)]">
            {{ engineBadgeLabel(selectedEngine) }}
          </span>
          <button
            class="secondary compact"
            :disabled="!selectedEngineInstalled || databasesStore.loadingEngine === selectedEngine"
            @click="refreshWorkspace">
            {{ t("panel.database.refresh") }}
          </button>
        </div>
      </div>

      <section v-if="selectedEngine === 'mssql' && !workspace" class="compact-state">
        <strong>{{ t("panel.database.mssqlTitle") }}</strong>
        <span>{{ t("panel.database.mssqlPlaceholder") }}</span>
      </section>

      <section v-else-if="!selectedEngineInstalled" class="compact-state install-state">
        <div>
          <strong>{{ t("panel.database.installTitle") }}</strong>
          <span>{{ t("panel.database.installHint") }}</span>
        </div>
        <div class="install-meta">
          <span>{{ selectedEngineMeta?.label }}</span>
          <code>{{ selectedEngineMeta?.componentId || "-" }}</code>
          <button
            v-if="selectedEngineMeta?.componentId"
            class="compact"
            :disabled="softwareStore.installingComponentId === selectedEngineMeta.componentId"
            @click="installSelectedEngine">
            {{ softwareStore.installingComponentId === selectedEngineMeta?.componentId ? t("panel.software.installing") : t("panel.database.installAction") }}
          </button>
        </div>
      </section>

      <section v-else-if="needsCredentials" class="form-surface manage-existing">
        <div class="manage-head">
          <strong>{{ t("panel.database.manageExistingTitle") }}</strong>
          <span class="status-pill warn">{{ t("panel.database.detected") }}</span>
        </div>
        <p class="meta">{{ t("panel.database.manageExistingHint") }}</p>
        <div class="form-grid">
          <label>
            <span class="field-label">{{ t("panel.database.adminUser") }}</span>
            <input v-model="credForm.adminUserName" :placeholder="defaultAdminUser" />
          </label>
          <label>
            <span class="field-label">{{ t("panel.database.adminPassword") }}</span>
            <input v-model="credForm.password" type="password" @keyup.enter="submitRegisterCredentials" />
          </label>
          <label>
            <span class="field-label">{{ t("panel.database.portLabel") }}</span>
            <input v-model.number="credForm.port" type="number" min="1" max="65535" />
          </label>
        </div>
        <div class="row-actions">
          <button
            class="compact"
            :disabled="databasesStore.registeringEngine === selectedEngine || !credForm.password"
            @click="submitRegisterCredentials">
            {{ databasesStore.registeringEngine === selectedEngine ? t("panel.database.connecting") : t("panel.database.connectAndSave") }}
          </button>
        </div>
      </section>

      <template v-else-if="workspace">
        <div class="summary-strip database-summary">
          <div class="summary-item">
            <span>{{ t("panel.database.endpoint") }}</span>
            <strong>{{ workspace.host }}:{{ workspace.port }}</strong>
          </div>
          <div class="summary-item">
            <span>{{ t("panel.database.databaseCount") }}</span>
            <strong>{{ workspace.databases.length }}</strong>
          </div>
          <div class="summary-item">
            <span>{{ t("panel.database.userCount") }}</span>
            <strong>{{ workspace.users.length }}</strong>
          </div>
          <div class="summary-item">
            <span>{{ t("panel.database.service") }}</span>
            <strong>{{ workspace.serviceName || "-" }}</strong>
          </div>
        </div>

        <div class="module-toolbar workspace-toolbar">
          <div class="segmented-tabs" role="tablist">
            <button :class="{ active: activeSection === 'databases' }" @click="openSection('databases')">
              {{ t("panel.database.section.databases") }}
            </button>
            <button :class="{ active: activeSection === 'users' }" @click="openSection('users')">
              {{ t("panel.database.section.users") }}
            </button>
            <button :class="{ active: activeSection === 'connection' }" @click="openSection('connection')">
              {{ t("panel.database.section.connection") }}
            </button>
          </div>

          <div class="toolbar-group">
            <input
              v-if="activeSection === 'databases' || activeSection === 'users'"
              v-model="databaseSearch"
              class="database-search"
              :aria-label="t('panel.database.search')"
              :placeholder="t('panel.database.searchPlaceholder')" />
            <button
              v-if="activeSection === 'databases'"
              class="compact"
              @click="showDatabaseForm = !showDatabaseForm">
              {{ showDatabaseForm ? t("panel.database.cancelAction") : t("panel.database.createDbTitle") }}
            </button>
            <button
              v-if="activeSection === 'users'"
              class="compact"
              @click="showUserForm = !showUserForm">
              {{ showUserForm ? t("panel.database.cancelAction") : t("panel.database.createUserTitle") }}
            </button>
          </div>
        </div>

        <template v-if="activeSection === 'databases'">
          <section v-if="showDatabaseForm" class="form-surface">
            <p class="meta create-hint">{{ t("panel.database.createWithUserHint") }}</p>
            <div class="form-grid">
              <label>
                <span class="field-label">{{ t("panel.database.databaseName") }}</span>
                <input v-model="databaseForm.name" @keyup.enter="submitCreateDatabase" />
              </label>
              <label>
                <span class="field-label">{{ t("panel.database.userName") }}</span>
                <input v-model="databaseForm.userName" :placeholder="t('panel.database.optional')" />
              </label>
              <label>
                <span class="field-label">{{ t("panel.database.userPassword") }}</span>
                <input v-model="databaseForm.password" type="password" :placeholder="t('panel.database.optional')" />
              </label>
            </div>
            <div class="row-actions">
              <button
                class="compact"
                :disabled="databasesStore.creatingEngine === selectedEngine"
                @click="submitCreateDatabase">
                {{ databasesStore.creatingEngine === selectedEngine ? t("panel.database.saving") : t("panel.database.createDatabase") }}
              </button>
            </div>
          </section>

          <section class="data-table">
            <div class="data-row table-head database-row">
              <span>{{ t("panel.database.databaseName") }}</span>
              <span>{{ t("panel.database.owner") }}</span>
              <span>{{ t("panel.database.encoding") }}</span>
              <span>{{ t("panel.database.collation") }}</span>
              <span>{{ t("panel.files.actions") }}</span>
            </div>
            <div v-for="item in filteredDatabases" :key="item.name" class="data-row database-row">
              <span class="cell-main">
                <strong>{{ item.name }}</strong>
                <small>{{ workspace.displayName }}</small>
              </span>
              <span>{{ item.owner || "-" }}</span>
              <span>{{ item.encoding || "-" }}</span>
              <span class="cell-muted">{{ item.collation || "-" }}</span>
              <span class="row-actions">
                <button
                  v-if="selectedEngine === 'mariadb' || selectedEngine === 'mysql'"
                  class="secondary compact"
                  :disabled="openingDb === item.name"
                  @click="openInPhpMyAdmin(item.name)">
                  {{ openingDb === item.name ? t("panel.database.opening") : t("panel.database.openInPhpMyAdmin") }}
                </button>
                <button
                  class="danger compact"
                  :disabled="databasesStore.deletingKey === `${selectedEngine}:${item.name}`"
                  @click="removeDatabase(item.name)">
                  {{ databasesStore.deletingKey === `${selectedEngine}:${item.name}` ? t("panel.database.removing") : t("panel.database.deleteDatabase") }}
                </button>
              </span>
            </div>
            <p v-if="filteredDatabases.length === 0" class="empty-state">
              {{ t("panel.database.noDatabases") }}
            </p>
          </section>
        </template>

        <template v-else-if="activeSection === 'users'">
          <section v-if="showUserForm" class="form-surface">
            <div class="form-grid">
              <label>
                <span class="field-label">{{ t("panel.database.userName") }}</span>
                <input v-model="userForm.userName" />
              </label>
              <label>
                <span class="field-label">{{ t("panel.database.userPassword") }}</span>
                <input v-model="userForm.password" type="password" />
              </label>
            </div>
            <div class="row-actions">
              <button
                class="compact"
                :disabled="databasesStore.creatingUserEngine === selectedEngine"
                @click="submitCreateUser">
                {{ databasesStore.creatingUserEngine === selectedEngine ? t("panel.database.saving") : t("panel.database.createUser") }}
              </button>
              <button class="secondary compact" @click="showUserForm = false">
                {{ t("panel.database.cancelAction") }}
              </button>
            </div>
          </section>

          <section class="data-table">
            <div class="data-row table-head user-row">
              <span>{{ t("panel.database.userName") }}</span>
              <span>{{ t("panel.database.access") }}</span>
              <span>{{ t("panel.database.loginState") }}</span>
              <span>{{ t("panel.files.actions") }}</span>
            </div>
            <div v-for="item in filteredUsers" :key="`${item.userName}:${item.host}`" class="data-row user-row">
              <span class="cell-main">
                <strong>{{ item.userName }}</strong>
                <small>{{ workspace.displayName }}</small>
              </span>
              <span class="cell-muted">{{ item.host === '%' ? t('panel.database.accessAnywhere') : (item.host || t('panel.database.accessLocal')) }}</span>
              <span :class="['status-pill', item.canLogin ? 'ok' : 'warn']">
                {{ item.canLogin ? t("panel.status.enabled") : t("panel.status.disabled") }}
              </span>
              <span class="row-actions">
                <button class="secondary compact" @click="changeUserPassword(item.userName, item.host)">
                  {{ t("panel.database.changePassword") }}
                </button>
                <button
                  class="danger compact"
                  :disabled="databasesStore.deletingUserKey === `${selectedEngine}:${item.userName}:${item.host}`"
                  @click="removeUser(item.userName, item.host)">
                  {{ databasesStore.deletingUserKey === `${selectedEngine}:${item.userName}:${item.host}` ? t("panel.database.removing") : t("panel.database.deleteUser") }}
                </button>
              </span>
            </div>
            <p v-if="filteredUsers.length === 0" class="empty-state">
              {{ t("panel.database.noUsers") }}
            </p>
          </section>
        </template>

        <section v-else class="data-table connection-table">
          <div class="data-row table-head connection-row">
            <span>{{ t("panel.database.connectionProperty") }}</span>
            <span>{{ t("panel.database.connectionValue") }}</span>
          </div>
          <div class="data-row connection-row">
            <span>{{ t("panel.database.endpoint") }}</span>
            <strong>{{ workspace.host }}:{{ workspace.port }}</strong>
          </div>
          <div class="data-row connection-row">
            <span>{{ t("panel.database.adminUser") }}</span>
            <strong>{{ workspace.adminUserName }}</strong>
          </div>
          <div class="data-row connection-row">
            <span>{{ t("panel.database.adminPassword") }}</span>
            <code>{{ workspace.adminPassword || "-" }}</code>
          </div>
          <div class="data-row connection-row">
            <span>{{ t("panel.database.service") }}</span>
            <strong>{{ workspace.serviceName || "-" }}</strong>
          </div>
          <div class="data-row connection-row">
            <span>{{ t("panel.database.userScope") }}</span>
            <strong>{{ workspace.supportsHostScopedUsers ? t("panel.database.hostScopedUsers") : t("panel.database.roleUsers") }}</strong>
          </div>
        </section>
      </template>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import CacheView from "./CacheView.vue";
import DbToolSettingsView from "./DbToolSettingsView.vue";
import DbTransferView from "./DbTransferView.vue";
import WebDbView from "./WebDbView.vue";
import { openPhpMyAdmin } from "../api/databases";
import { openReachable } from "../utils/url";
import { extractApiMessage } from "../stores/auth";
import { useDatabasesStore } from "../stores/databases";
import { useSoftwareStore } from "../stores/software";

type DatabaseModule = "databases" | "webdb" | "cache" | "transfer" | "settings";
type WorkspaceSection = "databases" | "users" | "connection";

const { t } = useI18n();
const databasesStore = useDatabasesStore();
const softwareStore = useSoftwareStore();

const notice = ref("");
const noticeError = ref(false);
const databaseSearch = ref("");
const activeModule = ref<DatabaseModule>("databases");
const activeSection = ref<WorkspaceSection>("databases");
const showDatabaseForm = ref(false);
const showUserForm = ref(false);

const databaseForm = reactive({
  name: "",
  userName: "",
  password: ""
});

const userForm = reactive({
  userName: "",
  password: ""
});

const credForm = reactive({
  adminUserName: "",
  password: "",
  port: 3306
});

const engines = computed(() => ([
  { key: "mariadb", label: t("panel.database.engine.mariadb"), componentId: "mariadb-11" },
  { key: "mysql", label: t("panel.database.engine.mysql"), componentId: "mysql-8" },
  { key: "postgresql", label: t("panel.database.engine.postgresql"), componentId: "postgresql-16" },
  { key: "mssql", label: t("panel.database.engine.mssql"), componentId: "" }
]));

const installedComponentIds = computed(() => new Set(
  (softwareStore.catalog?.installed ?? [])
    .filter((component) => component.status.toLowerCase() === "installed")
    .map((component) => component.componentId)));

const selectedEngine = computed(() => databasesStore.selectedEngine);
const selectedEngineMeta = computed(() =>
  engines.value.find((engine) => engine.key === selectedEngine.value) ?? engines.value[0]);

const selectedEngineInstalled = computed(() =>
  selectedEngine.value === "mssql" ||
  (!!selectedEngineMeta.value?.componentId &&
   installedComponentIds.value.has(selectedEngineMeta.value.componentId)));

const currentEngineStatus = computed(() => databasesStore.currentEngineStatus);
const needsCredentials = computed(() =>
  selectedEngine.value !== "mssql" &&
  selectedEngineInstalled.value &&
  currentEngineStatus.value !== null &&
  !currentEngineStatus.value.ready);
const defaultAdminUser = computed(() =>
  selectedEngine.value === "postgresql" ? "postgres" : "root");

const workspace = computed(() => databasesStore.currentWorkspace);
const filteredDatabases = computed(() => {
  const query = databaseSearch.value.trim().toLowerCase();
  const rows = workspace.value?.databases ?? [];
  if (!query) {
    return rows;
  }

  return rows.filter((item) =>
    [item.name, item.owner, item.encoding, item.collation]
      .some((value) => (value ?? "").toLowerCase().includes(query)));
});

const filteredUsers = computed(() => {
  const query = databaseSearch.value.trim().toLowerCase();
  const rows = workspace.value?.users ?? [];
  if (!query) {
    return rows;
  }

  return rows.filter((item) =>
    [item.userName, item.host]
      .some((value) => (value ?? "").toLowerCase().includes(query)));
});

onMounted(async () => {
  try {
    if (!softwareStore.catalog && !softwareStore.loading) {
      await softwareStore.fetchCatalog();
    }

    await databasesStore.fetchEngines();
    selectInitialEngine();
    await loadSelectedEngine();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
});

watch(() => databasesStore.selectedEngine, async () => {
  try {
    await loadSelectedEngine();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
});

// Seçili motor "yönetilebilir" ise çalışma alanını getir; kimlik bilgisi gerekiyorsa
// 409 gürültüsü üretmeden kayıt formunu hazırla (portu makul varsayılana çek).
async function loadSelectedEngine() {
  if (!selectedEngineInstalled.value || selectedEngine.value === "mssql") {
    return;
  }

  if (needsCredentials.value) {
    credForm.adminUserName = currentEngineStatus.value?.adminUserName ?? "";
    credForm.password = "";
    credForm.port = currentEngineStatus.value?.port || (selectedEngine.value === "postgresql" ? 5432 : 3306);
    return;
  }

  await databasesStore.fetchWorkspace(selectedEngine.value);
}

async function submitRegisterCredentials() {
  if (!credForm.password) {
    return;
  }

  try {
    await databasesStore.registerCredentials({
      engine: selectedEngine.value,
      adminUserName: credForm.adminUserName.trim() || undefined,
      password: credForm.password,
      port: credForm.port > 0 ? credForm.port : undefined
    });
    credForm.password = "";
    showNotice(t("panel.database.credentialsSaved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

function selectInitialEngine() {
  const preferred = engines.value.find((engine) =>
    engine.key !== "mssql" &&
    engine.componentId &&
    installedComponentIds.value.has(engine.componentId));

  databasesStore.selectEngine(preferred?.key ?? "mariadb");
}

function selectEngine(engine: string) {
  databaseSearch.value = "";
  showDatabaseForm.value = false;
  showUserForm.value = false;
  databasesStore.selectEngine(engine);
}

function openSection(section: WorkspaceSection) {
  activeSection.value = section;
  databaseSearch.value = "";
}

function engineStatusFor(engineKey: string) {
  return databasesStore.engines.find((item) => item.engine === engineKey) ?? null;
}

function engineBadgeClass(engineKey: string) {
  if (engineKey === "mssql") {
    return databasesStore.workspaces.mssql ? "ok" : "muted";
  }

  if (!installedComponentIds.value.has(selectedComponentId(engineKey))) {
    return "warn";
  }

  // Kurulu ama yönetici parolası yoksa (harici algılanan motor) sarı uyarı göster.
  const status = engineStatusFor(engineKey);
  return status && !status.ready ? "warn" : "ok";
}

function engineBadgeLabel(engineKey: string) {
  if (engineKey === "mssql") {
    return databasesStore.workspaces.mssql
      ? t("panel.php.installedState")
      : t("panel.database.autoDetect");
  }

  if (!installedComponentIds.value.has(selectedComponentId(engineKey))) {
    return t("panel.database.notInstalled");
  }

  const status = engineStatusFor(engineKey);
  return status && !status.ready
    ? t("panel.database.needsCredentials")
    : t("panel.php.installedState");
}

function engineDescription(engineKey: string) {
  return t(`panel.database.engineDescription.${engineKey}`);
}

function selectedComponentId(engineKey: string) {
  return engines.value.find((engine) => engine.key === engineKey)?.componentId ?? "";
}

async function installSelectedEngine() {
  if (!selectedEngineMeta.value?.componentId) {
    return;
  }

  try {
    await softwareStore.installComponent(selectedEngineMeta.value.componentId);
    await softwareStore.fetchCatalog();
    await databasesStore.fetchEngines();
    await loadSelectedEngine();
    showNotice(t("software.install.completed"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function refreshWorkspace() {
  if (!selectedEngineInstalled.value) {
    return;
  }

  try {
    await databasesStore.fetchEngines();
    await loadSelectedEngine();
    showNotice(t("panel.database.refreshed"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function submitCreateDatabase() {
  try {
    await databasesStore.createDatabase({
      engine: selectedEngine.value,
      name: databaseForm.name,
      userName: databaseForm.userName.trim() || undefined,
      password: databaseForm.password || undefined
    });
    databaseForm.name = "";
    databaseForm.userName = "";
    databaseForm.password = "";
    showDatabaseForm.value = false;
    showNotice(t("database.create.success"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function removeDatabase(name: string) {
  try {
    await databasesStore.deleteDatabase(selectedEngine.value, name);
    showNotice(t("database.delete.success"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function submitCreateUser() {
  try {
    await databasesStore.createUser({
      engine: selectedEngine.value,
      userName: userForm.userName,
      password: userForm.password,
      // Plesk gibi: host seçtirilmez; her yerden erişim ('%').
      allowedHost: "%"
    });

    userForm.userName = "";
    userForm.password = "";
    showUserForm.value = false;
    showNotice(t("database.user_created"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function removeUser(userName: string, host: string) {
  if (!window.confirm(t("panel.database.confirmDeleteUser"))) {
    return;
  }
  try {
    await databasesStore.deleteUser(selectedEngine.value, userName, host);
    showNotice(t("database.user_deleted"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

const openingDb = ref("");
async function openInPhpMyAdmin(databaseName: string) {
  openingDb.value = databaseName;
  try {
    const result = await openPhpMyAdmin(selectedEngine.value, databaseName);
    // Backend 127.0.0.1 dönebilir; uzak tarayıcı için paneli gördüğümüz host'a çevir.
    openReachable(result.url);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    openingDb.value = "";
  }
}

async function changeUserPassword(userName: string, host: string) {
  const password = window.prompt(t("panel.database.newPasswordPrompt", { user: userName }));
  if (password === null) {
    return;
  }
  if (password.length < 6) {
    showNotice(t("panel.database.passwordTooShort"), true);
    return;
  }
  try {
    await databasesStore.setUserPassword(selectedEngine.value, userName, password, host);
    showNotice(t("panel.database.passwordChanged"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

function resolveApiMessage(error: unknown): string {
  const key = extractApiMessage(error);
  const translated = t(key);
  return translated === key ? key : translated;
}

function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}
</script>

<style scoped>
.primary-tabs {
  width: fit-content;
  max-width: 100%;
}

.engine-toolbar {
  align-items: stretch;
}

.engine-tabs {
  flex: 1;
}

.engine-tabs button {
  display: inline-flex;
  align-items: center;
  gap: 8px;
}

.engine-state {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--text-muted);
}

.engine-state.ok {
  background: var(--accent-1);
  box-shadow: 0 0 0 3px color-mix(in srgb, var(--accent-1) 11%, transparent);
}

.engine-state.warn {
  background: var(--warn-text);
}

.compact-state {
  display: flex;
  justify-content: space-between;
  gap: 14px;
  align-items: center;
  padding: 14px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--input-bg);
}

.compact-state div,
.compact-state > span {
  display: grid;
  gap: 4px;
}

.compact-state span {
  color: var(--text-muted);
}

.install-meta {
  display: flex !important;
  align-items: center;
  gap: 10px !important;
}

.install-meta code,
.connection-row code {
  color: var(--text-soft);
  word-break: break-all;
}

.database-summary {
  grid-template-columns: 1.2fr 0.7fr 0.7fr 1fr;
}

.workspace-toolbar {
  align-items: center;
}

.database-search {
  width: min(310px, 38vw);
}

.inline-create {
  grid-template-columns: minmax(220px, 430px) auto;
  align-items: end;
}

.inline-create button {
  width: fit-content;
}

.database-row {
  grid-template-columns: minmax(190px, 1.35fr) 0.8fr 0.7fr minmax(180px, 1.1fr) auto;
}

.user-row {
  grid-template-columns: minmax(190px, 1.2fr) 0.9fr 1fr 0.7fr;
}

.grant-grid {
  grid-template-columns: repeat(4, minmax(0, 1fr));
}

.grant-surface {
  max-width: 1180px;
}

.form-action {
  width: fit-content;
}

.manage-existing {
  max-width: 760px;
}

.manage-head {
  display: flex;
  align-items: center;
  gap: 12px;
  justify-content: space-between;
}

.connection-table {
  max-width: 900px;
}

.connection-row {
  grid-template-columns: minmax(190px, 0.7fr) 1.3fr;
  min-width: 560px;
}

.compact-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.compact-head h3,
.compact-head p {
  margin: 0;
}

.section-head {
  display: flex;
  gap: 12px;
  justify-content: space-between;
}

.meta {
  margin: 0;
  color: var(--text-muted);
}

@media (max-width: 980px) {
  .database-summary,
  .grant-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .workspace-toolbar {
    align-items: flex-start;
  }

  .database-search {
    width: min(100%, 310px);
  }
}

@media (max-width: 720px) {
  .compact-state,
  .install-meta {
    align-items: flex-start;
    flex-direction: column;
  }

  .database-summary,
  .grant-grid,
  .inline-create {
    grid-template-columns: 1fr;
  }

  .workspace-toolbar .toolbar-group {
    width: 100%;
  }

  .database-search {
    flex: 1;
    min-width: 180px;
  }
}
</style>
