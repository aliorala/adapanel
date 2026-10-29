<template>
  <section class="panel module-shell site-logs">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.siteLogs.title") }}</h3>
        <p class="meta">{{ log && log.path ? log.path : t("panel.siteLogs.subtitle") }}</p>
      </div>
      <div class="log-actions">
        <select v-model.number="lines" :disabled="loading || rotationBusy" @change="load">
          <option :value="120">120</option>
          <option :value="300">300</option>
          <option :value="500">500</option>
        </select>
        <button class="secondary compact" :disabled="loading || rotationBusy || !siteName" @click="load">
          {{ loading ? t("panel.siteLogs.loading") : t("panel.siteLogs.refresh") }}
        </button>
      </div>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>
    <p v-if="!siteName" class="meta">{{ t("panel.php.selectSite") }}</p>

    <template v-else>
      <pre class="log-output">{{ content }}</pre>
      <p v-if="log && log.lastModifiedUtc" class="hint">
        {{ t("panel.siteLogs.updated") }}: {{ formatDate(log.lastModifiedUtc) }}
      </p>

      <section v-if="rotation" class="rotation-card">
        <div class="rotation-head">
          <h4>{{ t("panel.siteLogs.rotationTitle") }}</h4>
          <button class="secondary compact" v-bind="writeGate(rotationBusy)" @click="runRotation">
            {{ rotationBusy ? t("panel.siteLogs.loading") : t("panel.siteLogs.rotationRun") }}
          </button>
        </div>
        <p class="hint">{{ t("panel.siteLogs.rotationHint") }}</p>
        <div class="rotation-stats">
          <div>
            <span>{{ t("panel.siteLogs.rotationLast") }}</span>
            <strong>{{ rotation.lastRotationUtc ? formatDate(rotation.lastRotationUtc) : t("panel.siteLogs.rotationNever") }}</strong>
          </div>
          <div>
            <span>{{ t("panel.siteLogs.rotationArchived") }}</span>
            <strong>{{ rotation.rotatedFiles }}</strong>
          </div>
          <div>
            <span>{{ t("panel.siteLogs.rotationSaved") }}</span>
            <strong>{{ formatBytes(rotation.freedBytes) }}</strong>
          </div>
          <div>
            <span>{{ t("panel.siteLogs.rotationCurrent") }}</span>
            <strong>{{ formatBytes(rotation.currentLogBytes) }} + {{ formatBytes(rotation.currentArchiveBytes) }}</strong>
          </div>
          <div v-if="rotation.skippedLockedFiles > 0">
            <span>{{ t("panel.siteLogs.rotationLocked") }}</span>
            <strong>{{ rotation.skippedLockedFiles }}</strong>
          </div>
        </div>
        <p v-if="rotation.status === 'error'" class="notice error" role="alert">
          {{ rotation.error === "directory_is_reparse_point"
            ? t("panel.logs.rotationErrorReparsePoint")
            : rotation.error }}
        </p>
      </section>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import {
  getSiteLog,
  getSiteLogRotation,
  runSiteLogRotation,
  type LogRotationTarget,
  type LogTail
} from "../api/logs";
import { extractApiMessage, useAuthStore } from "../stores/auth";
import { getSessionEpoch } from "../api/client";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";
import { useSitesStore } from "../stores/sites";
import { formatBytes as sharedFormatBytes } from "../utils/bytes";
import { formatDateTime } from "../utils/datetime";

const { t } = useI18n();
const { confirm } = useConfirm();
// Dondurme DOSYA SILER — yazma islemidir. Askidaki abonelikte dugme basilmadan once pasiflesir
// (bkz. composables/useSubscriptionGate.ts). Gunlugu yenileme bir OKUMA'dir, kapiya girmez.
const { writeGate } = useSubscriptionGate();
const sitesStore = useSitesStore();
const auth = useAuthStore();
const siteName = computed(() => sitesStore.selectedSiteName);
const log = ref<LogTail | null>(null);
const rotation = ref<LogRotationTarget | null>(null);
const rotationBusy = ref(false);
const lines = ref(120);
const loading = ref(false);
const notice = ref("");
const noticeError = ref(false);

const content = computed(() =>
  log.value && log.value.lines.length ? log.value.lines.join("\n") : t("panel.siteLogs.empty"));

let disposed = false;
let generation = 0;
let readSequence = 0;
function captureContext() {
  const site = siteName.value, session = auth.session, actor = session?.user?.id, role = session?.user?.role;
  const version = generation, epoch = getSessionEpoch();
  return { site, current: () => !disposed && generation === version && siteName.value === site &&
    auth.session === session && session?.isAuthenticated === true && session.user?.id === actor &&
    session.user?.role === role && getSessionEpoch() === epoch };
}
onBeforeUnmount(() => { disposed = true; ++generation; ++readSequence; });
watch(() => [siteName.value, auth.session, auth.session?.isAuthenticated, auth.session?.user?.id, auth.session?.user?.role], () => {
  ++generation; ++readSequence;
  log.value = null; rotation.value = null; rotationBusy.value = false; loading.value = false;
  notice.value = ""; noticeError.value = false;
  void load();
}, { immediate: true, flush: "sync" });

async function load() {
  if (rotationBusy.value) return;
  const context = captureContext(), request = ++readSequence, requestedLines = lines.value;
  const current = () => context.current() && request === readSequence && lines.value === requestedLines;
  if (!context.site || !context.current()) {
    return;
  }
  loading.value = true;
  notice.value = "";
  try {
    const result = await getSiteLog(context.site, requestedLines);
    if (!current()) return;
    log.value = result;
    // Döndürme durumu AYRI yakalanır: bu kartın okunamaması, günlük kuyruğunu göstermeye engel değil.
    try {
      const status = await getSiteLogRotation(context.site);
      if (current()) rotation.value = status;
    } catch {
      if (current()) rotation.value = null;
    }
  } catch (error) {
    if (current()) gosterHata(error);
  } finally {
    if (current()) loading.value = false;
  }
}

/*
 * Bu düğme DOSYA SİLEBİLİR (saklama sınırları açıksa) — `window.confirm` yerine panelin kendi
 * onay diyaloğu kullanılır. Tarayıcı diyaloğu engellenmiş bir sekmede `window.confirm` `false`
 * döner ve düğme SESSİZCE HİÇBİR ŞEY YAPMAZ; kullanıcı düğmeyi bozuk sanır.
 */
async function runRotation() {
  const confirmationContext = captureContext();
  if (!confirmationContext.site || !confirmationContext.current() || rotationBusy.value) {
    return;
  }

  const approved = await confirm({
    title: t("panel.siteLogs.rotationRunTitle"),
    message: t("panel.siteLogs.rotationConfirm"),
    confirmLabel: t("panel.logs.rotationRunConfirmLabel")
  });

  if (!approved || !confirmationContext.current() || rotationBusy.value) {
    return;
  }

  ++generation; ++readSequence;
  const context = captureContext();
  loading.value = false;
  rotationBusy.value = true;
  notice.value = "";
  try {
    const result = await runSiteLogRotation(context.site);
    if (!context.current()) return;
    rotation.value = result;
    if (result.status === "error") {
      notice.value = result.error === "directory_is_reparse_point"
        ? t("panel.logs.rotationErrorReparsePoint") : result.error || t("panel.logs.rotationFailed");
      noticeError.value = true;
    } else {
      notice.value = t("panel.logs.rotationCompleted");
      noticeError.value = false;
    }
  } catch (error) {
    if (context.current()) gosterHata(error);
  } finally {
    if (context.current()) rotationBusy.value = false;
  }
}

function gosterHata(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  notice.value = translated === key ? key : translated;
  noticeError.value = true;
}

/** Bayt bicimi panelin geri kalaniyla AYNI (bkz. utils/bytes.ts); ekran kendi bicimini uydurmaz. */
function formatBytes(value: number) {
  return sharedFormatBytes(value, t);
}

/** Tarih bicimi panelin geri kalaniyla AYNI (bkz. utils/datetime.ts). */
function formatDate(iso: string) {
  return formatDateTime(iso);
}
</script>

<style scoped>
.site-logs {
  display: grid;
  gap: 12px;
}

.log-actions {
  display: flex;
  gap: 8px;
  align-items: center;
}

.log-actions select {
  width: auto;
  border-radius: var(--radius-sm);
  border: 1px solid var(--panel-border);
  padding: 9px 11px;
  background: var(--input-bg);
  color: var(--text-strong);
  font: inherit;
}

.log-actions button {
  width: auto;
}

.log-output {
  margin: 0;
  min-height: 280px;
  max-height: 560px;
  overflow: auto;
  padding: 14px 16px;
  border: 1px solid var(--panel-border);
  border-radius: 10px;
  /* Koyu konsol yüzeyi BİLEREK sabit (bkz. base.css `pre` kuralı; bu öge bir <pre>, yani
     açık temada base.css zaten devralıyor). Kabuk çıktısı açık zeminde okunmaz. */
  background: #0b0f17;
  color: #d6e2f0;
  font-family: var(--font-mono, ui-monospace, "Cascadia Code", Consolas, monospace);
  font-size: 0.8rem;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
}

.rotation-card {
  display: grid;
  gap: 10px;
  padding: 14px 16px;
  border: 1px solid var(--panel-border);
  border-radius: 10px;
  background: var(--surface-1);
}

.rotation-head {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 12px;
}

.rotation-head h4 {
  margin: 0;
}

.rotation-head button {
  width: auto;
}

.rotation-stats {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
  gap: 10px;
}

.rotation-stats div {
  display: grid;
  gap: 2px;
}

.rotation-stats span {
  color: var(--text-muted);
  /* 0.74rem = 9.99px idi — olcek tabani `--fs-2xs` (10.53px); altina inen kural yok. */
  font-size: var(--fs-2xs);
}
</style>
