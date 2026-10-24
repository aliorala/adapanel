<template>
  <!--
    GİDEN POSTA RÖLESİ (SMARTHOST) — YALNIZ YÖNETİCİ.

    Sunucunun TÜM giden postasının hangi üçüncü tarafa teslim edileceğini belirler; uç
    `Administrator` ile sınırlı, çağıran taraf da aynı koşulu uygular (bkz. MailView).

    PAROLA: sunucudan HİÇ dönmez (maskeli bile değil). Bu yüzden alan hiçbir zaman doldurulmaz;
    yalnızca "kayıtlı mı" durumu yazılır. Boş bırakılırsa saklı parola korunur.
  -->
  <section class="form-surface mail-relay-card">
    <div class="section-head">
      <div>
        <h3>{{ t("panel.mail.relayTitle") }}</h3>
        <p class="meta">{{ t("panel.mail.relayHint") }}</p>
      </div>
      <button type="button" class="secondary compact" :disabled="loading" @click="load">
        {{ loading ? t("panel.common.loading") : t("panel.common.refresh") }}
      </button>
    </div>

    <SectionLoadError v-if="loadError.message.value" :message="loadError.message.value" :busy="loading" @retry="load" />

    <p v-if="notice" :class="['notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <template v-if="relay">
      <!-- "Kurulu değil" ile "sunucuya bağlanılamadı" ayrı: ikincisinde ayar OKUNAMADI, yani
           ekrandaki boş form mevcut yapılandırma sanılmamalı. -->
      <p v-if="!relay.installed" class="empty-state">{{ t("panel.mail.notInstalled") }}</p>
      <div v-else-if="provider === 'mailenable' && relay.securityStatus === 'recovery-pending'" class="stacked-field">
        <p class="notice error" role="alert">{{ t("panel.mail.relayRecoveryPending") }}</p>
        <button type="button" class="compact" :disabled="busy || loading" @click="recover">{{ t("panel.mail.relayRecover") }}</button>
      </div>
      <p v-else-if="!relay.configured" class="notice error" role="alert">{{ t("panel.mail.relayUnavailable") }}</p>

      <template v-else>
        <div class="relay-state">
          <span :class="['status-pill', relay.enabled ? 'ok' : 'warn']">
            {{ relay.enabled ? t("panel.mail.relayActive") : t("panel.mail.relayInactive") }}
          </span>
          <span v-if="relay.hasPassword" class="status-pill">{{ t("panel.mail.relayHasPassword") }}</span>
        </div>
        <p v-if="provider === 'mailenable'" class="field-hint relay-warning">{{ t("panel.mail.mailEnableRelayTlsHint") }}</p>
        <p v-if="provider === 'mailenable' && relay.enabled && relay.securityStatus !== 'verified-settings'" class="notice error" role="alert">{{ t("panel.mail.mailEnableRelayInsecure") }}</p>

        <label class="toggle-field">
          <input v-model="form.enabled" type="checkbox" :disabled="busy" />
          <span>{{ t("panel.mail.relayEnabled") }}</span>
        </label>

        <div v-if="form.enabled" class="form-grid">
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.mail.relayHost") }}</span>
            <input v-model.trim="form.host" :disabled="busy" placeholder="smtp.example.com" />
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.mail.relayPort") }}</span>
            <input v-model.number="form.port" type="number" min="1" max="65535" :disabled="busy" />
            <!-- Zorlama YOK, yalnızca ipucu: doğrudan TLS pratikte 465'tir. -->
            <small v-if="form.implicitTls && form.port === 587" class="field-hint">{{ t("panel.mail.relayPortTlsHint") }}</small>
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.mail.relayProtocol") }}</span>
            <select v-model="form.protocol" :disabled="busy">
              <option value="smtp">SMTP</option>
              <option v-if="provider !== 'mailenable'" value="lmtp">LMTP</option>
            </select>
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.mail.relayUsername") }}</span>
            <input v-model.trim="form.username" :disabled="busy" autocomplete="off" />
            <small class="field-hint">{{ t("panel.mail.relayUsernameHint") }}</small>
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.mail.relayPassword") }}</span>
            <!-- Gerçek gizli alan: window.prompt ile parola sormak YASAK (tarayıcı diyalogu
                 engellenirse kullanıcı hiçbir şey görmüyordu) ve parola açıkta kalıyordu. -->
            <PasswordField v-model="form.password" autocomplete="new-password" :disabled="busy" />
            <small class="field-hint">
              {{ relay.hasPassword ? t("panel.mail.relayPasswordKeep") : t("panel.mail.relayPasswordHint") }}
            </small>
          </label>
          <label class="toggle-field">
            <input v-model="form.implicitTls" type="checkbox" :disabled="busy || (provider === 'mailenable' && !form.implicitTls)" />
            <span>{{ t("panel.mail.relayImplicitTls") }}</span>
          </label>
          <label v-if="provider !== 'mailenable'" class="toggle-field">
            <input v-model="form.allowInvalidCerts" type="checkbox" :disabled="busy" />
            <span>{{ t("panel.mail.relayAllowInvalidCerts") }}</span>
          </label>
          <p v-if="form.allowInvalidCerts" class="field-hint relay-warning">{{ t("panel.mail.relayAllowInvalidCertsWarn") }}</p>
        </div>

        <p v-else class="field-hint">{{ t("panel.mail.relayDisabledHint") }}</p>

        <div class="row-actions">
          <button type="button" class="compact" :disabled="busy" @click="save">
            {{ busy ? t("panel.common.saving") : t("panel.common.save") }}
          </button>
        </div>
      </template>
    </template>
  </section>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import PasswordField from "./PasswordField.vue";

import { getMailRelay, setMailRelay, type MailRelay } from "../api/mail";
import { useConfirm } from "./ConfirmDialog.vue";
import SectionLoadError from "./SectionLoadError.vue";
import { useNotice, useSectionError } from "../composables/useNotice";

const { t } = useI18n();
const props = defineProps<{ provider?: "stalwart" | "mailenable" }>();
const { confirm } = useConfirm();
const { notice, noticeError, showNotice, showError } = useNotice();
const loadError = useSectionError();

const relay = ref<MailRelay | null>(null);
const loading = ref(false);
const busy = ref(false);

/** Parola ALANI forma sunucudan HİÇ doldurulmaz; yalnız kullanıcının yazdığı değer taşınır. */
const form = reactive({
  enabled: false,
  host: "",
  port: 587,
  protocol: "smtp",
  implicitTls: false,
  allowInvalidCerts: false,
  username: "",
  password: ""
});

onMounted(load);

async function load() {
  loading.value = true;
  try {
    const current = await getMailRelay();
    relay.value = current;
    applyToForm(current);
    loadError.clear();
  } catch (error) {
    // Okunamadıysa form GÖSTERMİYORUZ: boş bir form "röle yok" sanılıp yanlışlıkla kurulurdu.
    relay.value = null;
    loadError.capture(error);
  } finally {
    loading.value = false;
  }
}

function applyToForm(current: MailRelay) {
  form.enabled = current.enabled;
  form.host = current.host;
  form.port = current.port > 0 ? current.port : 587;
  form.protocol = current.protocol || "smtp";
  form.implicitTls = current.implicitTls;
  form.allowInvalidCerts = props.provider === "mailenable" ? false : current.allowInvalidCerts;
  form.username = current.username;
  // Parola sunucudan dönmediği için burada da SIFIRLANIR: ekranda saklı parolanın yerine
  // sahte bir değer göstermek, "değiştirdim" sanılan bir kaydetmeye yol açardı.
  form.password = "";
}

/**
 * Kaydetme yıkıcı değildir ama SUNUCUNUN TÜM GİDEN POSTASININ nereye teslim edileceğini
 * değiştirir; yanlışlıkla basılması postayı sessizce durdurabilir. Bu yüzden onay istenir ve
 * onayda sonucun ne olacağı (rölenin kurulması ya da kaldırılması) tek tek yazılır.
 */
async function recover() {
  await confirm({
    title: t("panel.mail.relayRecover"),
    message: t("panel.mail.relayRecoverConfirm"),
    impact: [t("panel.mail.relayRecoverImpact")],
    tone: "neutral",
    confirmLabel: t("panel.mail.relayRecover"),
    action: async () => {
      busy.value = true;
      try {
        const restored = await setMailRelay({ recoverOnly: true, enabled: false, host: "", port: 587,
          protocol: "smtp", implicitTls: false, allowInvalidCerts: false, username: "", password: "" });
        relay.value = restored;
        applyToForm(restored);
        showNotice(t("mail.relay_recovered"));
      } catch (error) {
        showError(error);
      } finally {
        busy.value = false;
      }
    }
  });
}

async function save() {
  if (!relay.value) {
    return;
  }

  if (form.enabled && !form.host) {
    // Sunucunun döndüreceği anahtarın AYNISI: kullanıcı iki farklı metin görmez.
    showNotice(t("mail.relay_host_required"), true);
    return;
  }
  if (form.enabled && form.username && !form.password && !relay.value.hasPassword) {
    showNotice(t("mail.relay_password_required"), true);
    return;
  }

  const willRemove = !form.enabled;
  const impact = willRemove
    ? [t("panel.mail.relayImpactRemoved"), t("panel.mail.relayImpactPasswordCleared")]
    : [
        `${t("panel.mail.relayHost")}: ${form.host}:${form.port}`,
        `${t("panel.mail.relayProtocol")}: ${form.protocol.toUpperCase()}`,
        form.username ? `${t("panel.mail.relayUsername")}: ${form.username}` : t("panel.mail.relayNoAuth")
      ];

  await confirm({
    title: willRemove ? t("panel.mail.relayConfirmRemoveTitle") : t("panel.mail.relayConfirmSaveTitle"),
    message: willRemove ? t("panel.mail.relayConfirmRemove") : t("panel.mail.relayConfirmSave"),
    impact,
    tone: "neutral",
    confirmLabel: t("panel.common.save"),
    action: async () => {
      busy.value = true;
      try {
        const saved = await setMailRelay({
          enabled: form.enabled,
          host: form.host,
          port: form.port,
          protocol: form.protocol,
          implicitTls: form.implicitTls,
          allowInvalidCerts: form.allowInvalidCerts,
          username: form.username,
          // BOŞ = saklı parolayı koru (sunucu sözleşmesi).
          password: form.password
        });
        relay.value = saved;
        applyToForm(saved);
        showNotice(t(saved.enabled ? "mail.relay_saved" : "mail.relay_removed"));
      } catch (error) {
        showError(error);
      } finally {
        busy.value = false;
      }
    }
  });
}
</script>

<style scoped>
.mail-relay-card {
  display: grid;
  gap: 10px;
}

.relay-state {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
}

.relay-warning {
  color: var(--danger-text);
}
</style>
