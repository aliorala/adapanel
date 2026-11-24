<template>
  <section class="panel module-shell">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.database.transfer.title") }}</h3>
        <p class="meta">{{ t("panel.database.transfer.subtitle") }}</p>
      </div>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>
    <div v-if="created.userName" class="notice cred-box" role="status">
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
          <input v-model.trim="form.sourceHost" placeholder="192.0.2.10" :disabled="busy" />
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
          <PasswordField v-model="form.sourcePassword" autocomplete="off" :disabled="busy" />
        </label>
        <!-- BAĞLANTI ŞİFRELEMESİ kaynak parolasının hemen ardında: koruduğu şey odur.
             "Otomatik" uzak kaynakta zorunlu kılar, loopback'te gereksiz yere kırmaz. -->
        <label>
          <span class="field-label">{{ t("panel.database.transfer.tlsMode") }}</span>
          <select v-model="form.sourceTlsMode" :disabled="busy">
            <option value="auto">{{ t("panel.database.transfer.tls.auto") }}</option>
            <option value="required">{{ t("panel.database.transfer.tls.required") }}</option>
            <option value="disabled">{{ t("panel.database.transfer.tls.disabled") }}</option>
          </select>
          <small class="field-hint">{{ t("panel.database.transfer.tlsHint") }}</small>
        </label>
        <label>
          <span class="field-label">{{ t("panel.database.transfer.sourceDb") }}</span>
          <input v-model.trim="form.sourceDatabase" :placeholder="t('panel.database.transfer.sourceDbPlaceholder')" :disabled="busy" />
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
          <!-- `type="text"` idi: hedef veritabaninin parolasi ekranda SUREKLI aciktı. Kaynak
               parolasi gizliydi, hedef parolasi degildi -- ayni formda iki farkli kural.
               Simdi ikisi de varsayilan gizli, gormek isteyen "Goster"e basar. -->
          <PasswordField v-model="form.targetPassword" autocomplete="off" :disabled="busy" />
        </label>
      </div>
      <p class="hint">{{ t("panel.database.transfer.targetUserHint") }}</p>
      <label class="checkbox-field">
        <input v-model="form.adaptDefiners" type="checkbox" :disabled="busy" />
        <span>{{ t("panel.database.definers.adapt") }}</span>
      </label>
      <p class="hint">{{ t("panel.database.definers.restoreHint") }}</p>

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
import { computed, onBeforeUnmount, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import PasswordField from "../components/PasswordField.vue";
import { transferDatabase } from "../api/databases";
import { extractApiDetails, extractApiMessage } from "../stores/auth";
import { getSessionEpoch } from "../api/client";

const { t } = useI18n();
const busy = ref(false);
const notice = ref("");
const noticeError = ref(false);
const created = reactive({ userName: "", password: "" });
const form = reactive({
  engine: "mariadb",
  adaptDefiners: false,
  sourceHost: "",
  sourcePort: 3306,
  sourceUserName: "root",
  sourcePassword: "",
  /*
   * "auto" = uzak kaynakta şifreleme ZORUNLU, loopback'te gerekmez. Varsayılanın güvenli olması
   * şart: eskiden hiçbir şifreleme bayrağı yoktu ve mysqldump şifresiz bağlanabildiği için uzak
   * taşımada DB parolası ve tüm tablolar açık metin akabiliyordu.
   */
  sourceTlsMode: "auto",
  sourceDatabase: "",
  targetDatabase: "",
  targetUserName: "",
  targetPassword: ""
});
let disposed = false;
const session = getSessionEpoch();
const isCurrent = () => !disposed && session === getSessionEpoch();
onBeforeUnmount(() => {
  disposed = true;
  form.sourcePassword = ""; form.targetPassword = ""; created.password = "";
});

const canRun = computed(() =>
  form.sourceHost.trim().length > 0 &&
  form.sourceUserName.trim().length > 0 &&
  form.sourceDatabase.trim().length > 0 &&
  Number.isInteger(form.sourcePort) && form.sourcePort >= 1 && form.sourcePort <= 65535);

async function run() {
  if (!isCurrent() || busy.value || !canRun.value) {
    return;
  }
  busy.value = true;
  showNotice("", false);
  created.userName = "";
  created.password = "";
  try {
    const result = await transferDatabase({
      engine: form.engine,
      adaptDefiners: form.adaptDefiners,
      sourceHost: form.sourceHost.trim(),
      sourcePort: form.sourcePort,
      sourceUserName: form.sourceUserName.trim(),
      sourcePassword: form.sourcePassword,
      // Şifreleme tercihi AÇIKÇA gönderilir; gönderilmezse sunucu "auto" sayar (güvenli taraf),
      // ama o zaman kullanıcının "Şifrelemesiz" seçimi yolda sessizce düşerdi.
      sourceTlsMode: form.sourceTlsMode,
      sourceDatabase: form.sourceDatabase.trim(),
      targetDatabase: form.targetDatabase.trim(),
      targetUserName: form.targetUserName.trim(),
      targetPassword: form.targetPassword
    });
    if (!isCurrent()) return;
    created.userName = result.userName;
    created.password = result.password;
    showNotice(t("panel.database.transfer.done", { target: result.targetDatabase }), false);
  } catch (error) {
    if (isCurrent()) showNotice(resolveApiMessage(error), true);
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
  color: var(--accent-1);
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

/* Onay kutusu satirlari DISARIDA: bunlar iki satirlik bir izgara degil, yan yana duran
   kutu+etikettir. Kapsamli `label` kurali element duzeyinde oldugu icin global
   `.check-field` kuralini eziyor, onay kutusu hucreyi kaplayip etiketi alt satira dusuruyordu.
   `:where()` KULLANILIYOR, duz `:not()` DEGIL: `:not(.x)` argumaninin ozgullugunu kurala EKLER
   ((0,1,1) -> (0,2,1)) ve boylece bilesenlerin kendi `.checkbox-field[data-v]` (0,2,0) kurallarini
   ezer -- SiteAliases'in onay kutulari tam olarak boyle bozulmustu. `:where()` her zaman SIFIR
   ozgullukte oldugu icin dislama yapar ama kurali guclendirmez. */
label:not(:where(.check-field, .checkbox-field, .checkbox-row)) {
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
  /* 0.74rem = 9.99px idi — olcek tabani `--fs-2xs` (10.53px); altina inen kural yok. */
  font-size: var(--fs-2xs);
}

.row-actions {
  margin-top: 14px;
}

.row-actions button {
  width: auto;
}

/* base.css `.hint` rengi ve boyutu verir; buradaki EKLER bu ekrana ozgu: ust bosluk ve
   cok satirli ciktinin satir sonlari. */
.hint {
  margin-top: var(--sp-10);
  white-space: pre-line;
}
</style>
