<template>
  <section class="panel module-shell migration-view">
    <header class="module-head">
      <div>
        <h2>{{ t("panel.migration.title") }}</h2>
        <p>{{ t("panel.migration.subtitle") }}</p>
      </div>
    </header>

    <p v-if="notice" :class="['inline-notice', noticeError ? 'error' : 'ok']">{{ notice }}</p>

    <div class="form-surface">
      <p class="meta create-hint">{{ t("panel.migration.intro") }}</p>

      <!-- Hedef site -->
      <h3 class="section-label">{{ t("panel.migration.siteSection") }}</h3>
      <div class="form-grid">
        <label>
          <span class="field-label">{{ t("panel.migration.siteName") }}</span>
          <input v-model="form.siteName" placeholder="alioral.xyz" spellcheck="false" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.sites.physicalPath") }}</span>
          <input v-model="form.physicalPath" spellcheck="false" />
        </label>
      </div>
      <label class="checkbox">
        <input v-model="form.createSite" type="checkbox" />
        <span>{{ t("panel.migration.createSite") }}</span>
      </label>

      <!-- Dosya kaynağı -->
      <label class="checkbox section-toggle">
        <input v-model="form.pullFiles" type="checkbox" />
        <span><strong>{{ t("panel.migration.pullFiles") }}</strong></span>
      </label>
      <div v-if="form.pullFiles" class="form-grid">
        <label>
          <span class="field-label">{{ t("panel.migration.ftp.protocol") }}</span>
          <select v-model="form.fileProtocol" @change="syncFilePort">
            <option value="sftp">SFTP</option>
            <option value="ftps">FTPS</option>
            <option value="ftp">FTP</option>
          </select>
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.ftp.host") }}</span>
          <input v-model="form.fileHost" spellcheck="false" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.ftp.port") }}</span>
          <input v-model.number="form.filePort" type="number" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.ftp.user") }}</span>
          <input v-model="form.fileUserName" spellcheck="false" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.ftp.password") }}</span>
          <input v-model="form.filePassword" type="password" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.ftp.remotePath") }}</span>
          <input v-model="form.remotePath" spellcheck="false" placeholder="/httpdocs" />
        </label>
      </div>

      <!-- Veritabanı kaynağı -->
      <label class="checkbox section-toggle">
        <input v-model="form.transferDatabase" type="checkbox" />
        <span><strong>{{ t("panel.migration.transferDb") }}</strong></span>
      </label>
      <div v-if="form.transferDatabase" class="form-grid">
        <label>
          <span class="field-label">{{ t("panel.migration.dbEngine") }}</span>
          <select v-model="form.databaseEngine">
            <option value="mariadb">MariaDB</option>
            <option value="mysql">MySQL</option>
          </select>
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.ftp.host") }}</span>
          <input v-model="form.dbHost" spellcheck="false" placeholder="127.0.0.1" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.ftp.port") }}</span>
          <input v-model.number="form.dbPort" type="number" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.ftp.user") }}</span>
          <input v-model="form.dbUserName" spellcheck="false" placeholder="root" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.ftp.password") }}</span>
          <input v-model="form.dbPassword" type="password" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.sourceDb") }}</span>
          <input v-model="form.sourceDatabase" spellcheck="false" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.targetDb") }}</span>
          <input v-model="form.targetDatabase" spellcheck="false" :placeholder="form.sourceDatabase" />
        </label>
      </div>

      <div class="row-actions">
        <button class="compact" :disabled="busy || !form.siteName.trim()" @click="run">
          {{ busy ? t("panel.migration.running") : t("panel.migration.start") }}
        </button>
      </div>
    </div>

    <section v-if="steps.length" class="form-surface">
      <h3 class="section-label">{{ t("panel.migration.result") }}</h3>
      <div v-for="(step, index) in steps" :key="index" class="data-row">
        <span :class="['status-pill', step.success ? 'ok' : 'danger']">{{ step.success ? "✓" : "✗" }}</span>
        <strong>{{ t(`panel.migration.stepName.${step.step}`) }}</strong>
        <span class="cell-muted">{{ t(step.message) }}</span>
      </div>
    </section>
  </section>
</template>

<script setup lang="ts">
import { reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { migrateSite, type MigrationStep } from "../api/migration";
import { extractApiMessage } from "../stores/auth";

const { t } = useI18n();

const form = reactive({
  siteName: "",
  createSite: true,
  physicalPath: "",
  pullFiles: true,
  fileProtocol: "sftp",
  fileHost: "",
  filePort: 22,
  fileUserName: "",
  filePassword: "",
  remotePath: "/",
  transferDatabase: true,
  databaseEngine: "mariadb",
  dbHost: "",
  dbPort: 3306,
  dbUserName: "root",
  dbPassword: "",
  sourceDatabase: "",
  targetDatabase: ""
});

const busy = ref(false);
const steps = ref<MigrationStep[]>([]);
const notice = ref("");
const noticeError = ref(false);
let physicalPathEdited = false;

// Site adından varsayılan fiziksel yol türet (kullanıcı elle değiştirmediyse).
watch(
  () => form.siteName,
  (name) => {
    if (!physicalPathEdited) {
      form.physicalPath = name.trim() ? `C:\\inetpub\\wwwroot\\${name.trim()}` : "";
    }
  }
);
watch(
  () => form.physicalPath,
  () => {
    // kullanıcı elle yazdıysa otomatik türetmeyi durdur
    if (form.physicalPath && form.siteName && form.physicalPath !== `C:\\inetpub\\wwwroot\\${form.siteName.trim()}`) {
      physicalPathEdited = true;
    }
  }
);

// Protokole göre varsayılan portu güncelle (SFTP=22, FTP/FTPS=21).
function syncFilePort() {
  form.filePort = form.fileProtocol === "sftp" ? 22 : 21;
}

async function run() {
  if (!form.siteName.trim()) return;
  busy.value = true;
  notice.value = "";
  steps.value = [];
  try {
    const result = await migrateSite({ ...form, siteName: form.siteName.trim() });
    steps.value = result.steps;
    notice.value = t(result.overallSuccess ? "panel.migration.overallOk" : "panel.migration.overallPartial");
    noticeError.value = !result.overallSuccess;
  } catch (error) {
    notice.value = extractApiMessage(error);
    noticeError.value = true;
  } finally {
    busy.value = false;
  }
}
</script>

<style scoped>
.section-label {
  margin: 18px 0 8px;
  font-size: 0.92rem;
  opacity: 0.8;
}
.section-toggle {
  margin-top: 16px;
}
.migration-view .data-row {
  display: flex;
  gap: 10px;
  align-items: center;
  padding: 8px 0;
}
.inline-notice {
  padding: 8px 12px;
  border-radius: 8px;
}
.inline-notice.ok {
  background: rgba(46, 160, 67, 0.12);
}
.inline-notice.error {
  background: rgba(218, 54, 51, 0.12);
}
</style>
