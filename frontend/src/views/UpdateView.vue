<template>
  <section class="panel update-panel">
    <div class="section-head">
      <div>
        <h2>{{ t("panel.update.title") }}</h2>
        <p class="meta">{{ t("panel.update.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="Boolean(busy) || refreshing || monitoring" @click="refresh(true)">
        {{ refreshing ? t("panel.update.checking") : t("panel.update.refresh") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <UpdateStatusCard :status="status" :confirmed="statusConfirmed" :checking="refreshing"
      :phase="busy === 'download' ? 'downloading' : installPhase" />

    <template v-if="status">
      <section v-if="installPhase" :class="['install-progress', `is-${installPhase}`]" aria-labelledby="install-watch-title">
        <div class="install-progress-head">
          <span class="install-phase-icon" aria-hidden="true">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
              <template v-if="installPhase === 'done'"><circle cx="12" cy="12" r="9" /><path d="m8 12 2.5 2.5L16 9" /></template>
              <template v-else-if="installPhase === 'failed' || installPhase === 'timeout'"><path d="m12 3 10 18H2L12 3Z" /><path d="M12 9v5m0 3h.01" /></template>
              <template v-else><circle cx="12" cy="12" r="9" /><path d="M12 7v5l3 2" /></template>
            </svg>
          </span>
          <div class="install-progress-copy">
            <h3 id="install-watch-title" role="status">{{ t('panel.update.phase.' + installPhase) }}</h3>
            <p class="meta">{{ t(installPhase === 'done' ? 'panel.update.phaseVerifiedHint' : installPhase === 'failed' || installPhase === 'timeout' ? 'panel.update.phaseCheckAgainHint' : 'panel.update.phaseHintReal') }}</p>
          </div>
        </div>
        <dl class="install-observations">
          <div><dt>{{ t('panel.update.targetVersion') }}</dt><dd>{{ installTargetVersion || '—' }}</dd></div>
          <div><dt>{{ t('panel.update.watchElapsed') }}</dt><dd>{{ formatElapsed(installElapsedSeconds) }}</dd></div>
        </dl>
      </section>

      <section v-if="!status.isInstallScheduled && !monitoring && (status.isUpdateAvailable || status.isPackageReady || busy)" class="update-next-step">
        <div class="next-step-heading">
          <h3>{{ t("panel.update.nextStep") }}</h3>
          <p class="meta">{{ t("panel.update.safeApplyHint") }}</p>
        </div>
        <div class="update-actions">
          <button :class="{ secondary: status.isPackageReady }" :disabled="!canDownload" @click="downloadUpdate">
            {{ busy === "download" ? t("panel.update.downloading") : t("panel.update.download") }}
          </button>
          <button :class="{ secondary: !status.isPackageReady }" :disabled="!canInstall" @click="installUpdate">
            {{ busy === "install" ? t("panel.update.scheduling") : t("panel.update.install") }}
          </button>
        </div>
      </section>

      <section class="update-notes">
        <h3>{{ t("panel.update.notes") }}</h3>
        <p v-if="notlar.anahtar" class="meta">{{ notlar.anahtar }}</p>
        <ReleaseNotes v-else-if="notlar.metin" :text="notlar.metin" />
        <p v-else class="meta">{{ t("panel.update.noNotes") }}</p>
      </section>

      <details v-if="status.packageSha256 || status.packagePath" class="update-details">
        <summary>{{ t("panel.update.technicalDetails") }}</summary>
        <dl class="detail-grid">
          <div v-if="status.preparedPackageSha256"><dt>{{ t("panel.update.preparedChecksum") }}</dt><dd>{{ status.preparedPackageSha256 }}</dd></div>
          <div v-if="status.packageSha256"><dt>{{ t("panel.update.releaseChecksum") }}</dt><dd>{{ status.packageSha256 }}</dd></div>
          <div v-if="status.packagePath"><dt>{{ t("panel.update.packagePath") }}</dt><dd>{{ status.packagePath }}</dd></div>
        </dl>
      </details>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import {
  applyUpdate,
  checkForUpdate,
  installPreparedUpdate,
  type UpdateAttempt,
  type UpdateStatus,
} from "../api/update";
import { useConfirm } from "../components/ConfirmDialog.vue";
import ReleaseNotes from "../components/ReleaseNotes.vue";
import { extractApiMessage, useAuthStore } from "../stores/auth";
import { getSessionEpoch } from "../api/client";
import UpdateStatusCard from "../components/UpdateStatusCard.vue";

const { t } = useI18n();
const { confirm } = useConfirm();
const status = ref<UpdateStatus | null>(null);
const busy = ref<"" | "download" | "install">("");
const refreshing = ref(false);
let statusRevision = 0;
const statusConfirmed = ref(false);
const monitoring = ref(false);
const auth = useAuthStore();
const mountedEpoch = getSessionEpoch(), actor = auth.session?.user?.id, role = auth.session?.user?.role;
let disposed = false;
const current = () => !disposed && getSessionEpoch() === mountedEpoch && auth.session?.user?.id === actor && auth.session?.user?.role === role;
const validHash = (hash?: string) => typeof hash === "string" && /^[a-f\d]{64}$/i.test(hash);
const preparedIdentity = (value: UpdateStatus | null) => !!value?.preparedVersion?.trim() && validHash(value.preparedPackageSha256);
const prepared = (value: UpdateStatus | null) => !!value?.isPackageReady && preparedIdentity(value);
const canDownload = computed(() => statusConfirmed.value && !!status.value && !status.value.isCheckFailed && status.value.isUpdateAvailable &&
  !!status.value.downloadUrl && validHash(status.value.packageSha256) && !status.value.isInstallScheduled && !busy.value && !refreshing.value && !monitoring.value);
const canInstall = computed(() => statusConfirmed.value && prepared(status.value) && !status.value?.isInstallScheduled && !busy.value && !refreshing.value && !monitoring.value);
const notice = ref("");
const noticeError = ref(false);

/**
 * Kurulumun GÖZLEMLENEN aşaması — tahmin değil, her yoklamada gerçekten ölçülen durum:
 * scheduled  = panel hâlâ yanıt veriyor, kurulum henüz servisi durdurmadı
 * restarting = son istek yanıtlanamadı; yeniden başlama nedeni doğrulanmış değil
 * verifying  = istek yeniden yanıtlandı, sürüm karşılaştırılıyor
 * done       = yeni sürüm doğrulandı
 * timeout    = 15 dakika doldu
 */
const installPhase = ref<"" | "scheduled" | "restarting" | "verifying" | "done" | "failed" | "timeout">("");
const installElapsedSeconds = ref(0);
const installTargetVersion = ref("");
const installAttempt = ref<UpdateAttempt | null>(null);
// Bu denemenin baslangic ani: calistiricinin BU denemeye ait durumu mu, yoksa onceki bir denemeden
// kalan eski dosya mi oldugunu ayirt etmek icin sart.
let installStartedAtIso = "";

// Ekrana GİRMEK "kontrol et" demektir: kullanıcı buraya güncelleme var mı diye bakmaya gelir,
// önbellekten bayat cevap vermek bu ekranın tek işini boşa çıkarır.
onMounted(async () => {
  await refresh(true);
});

// NOT: Güncelleme kaynağı (GitHub depo) ayarı kullanıcı isteğiyle UI'dan KALDIRILDI; backend
// varsayılanı (SelfUpdateService.DefaultRepo) kullanılır, gerekirse config/update-github.json'dan değişir.
async function refresh(force = true) {
  if (!current() || busy.value || refreshing.value || monitoring.value) return;
  refreshing.value = true;
  const revision = ++statusRevision;
  try {
    const next = await checkForUpdate(force);
    if (!current() || revision !== statusRevision) return;
    status.value = next;
    statusConfirmed.value = true;
    // A fresh measurement replaces a finished watch; any still-scheduled
    // installation remains represented by the returned server status.
    if (installPhase.value === "failed" || installPhase.value === "timeout") {
      installPhase.value = "";
      installElapsedSeconds.value = 0;
      installTargetVersion.value = "";
      installAttempt.value = null;
    }
    notice.value = "";
    if (next.isInstallScheduled) {
      startPostInstallWatch(next.preparedVersion?.trim() ?? "", next.currentVersion, next.lastAttempt, next);
    }
  } catch (error) {
    if (current() && revision === statusRevision) { statusConfirmed.value = false; showNotice(resolveApiMessage(error), true); }
  } finally {
    if (current() && revision === statusRevision) refreshing.value = false;
  }
}

async function downloadUpdate() {
  if (!current() || !canDownload.value || !status.value) return;
  const submitted = { ...status.value }, revision = ++statusRevision;
  busy.value = "download";
  try {
    const result = await applyUpdate(submitted.downloadUrl, submitted.packageSha256);
    if (!current() || revision !== statusRevision) return;
    status.value = result;
    const verified = prepared(result) && sameVersion(result.preparedVersion!, submitted.latestVersion) &&
      result.preparedPackageSha256!.toLowerCase() === submitted.packageSha256.toLowerCase();
    statusConfirmed.value = verified;
    showNotice(t(verified ? "panel.update.staged" : "panel.update.resultUnconfirmed"), !verified);
  } catch (error) {
    if (current() && revision === statusRevision) { statusConfirmed.value = false; showNotice(resolveApiMessage(error), true); }
  } finally {
    if (current() && revision === statusRevision) busy.value = "";
  }
}

async function installUpdate() {
  if (!current() || !canInstall.value || !status.value) return;
  const submitted = { ...status.value }, revision = ++statusRevision;
  const expectedVersion = submitted.preparedVersion!, expectedPackageSha256 = submitted.preparedPackageSha256!;
  busy.value = "install";
  try {
    const approved = await confirm({
    title: t("panel.confirm.installUpdateTitle", { name: expectedVersion }),
    message: t("panel.update.installConfirm"),
    impact: [
      `${t("panel.update.current")}: ${submitted.currentVersion}`,
      `${t("panel.update.preparedVersion")}: ${expectedVersion}`
    ],
    tone: "neutral",
    confirmLabel: t("panel.update.install"),
    busyLabel: t("panel.update.scheduling"),
  });
    if (!approved || !current() || revision !== statusRevision) return;
    // This read explains a changed confirmation; the server checks the same
    // expected identity atomically when it schedules the installation.
    const checked = await checkForUpdate();
    if (!current() || revision !== statusRevision) return;
    status.value = checked;
    if (!prepared(checked) || checked.isInstallScheduled || !sameVersion(checked.preparedVersion!, expectedVersion) ||
      checked.preparedPackageSha256!.toLowerCase() !== expectedPackageSha256.toLowerCase()) {
      statusConfirmed.value = false;
      showNotice(t("panel.update.targetChanged"), true);
      return;
    }
    const result = await installPreparedUpdate(expectedVersion, expectedPackageSha256);
    if (!current() || revision !== statusRevision) return;
    status.value = result;
    if (!result.isInstallScheduled || !preparedIdentity(result) || !sameVersion(result.preparedVersion!, expectedVersion) ||
      result.preparedPackageSha256!.toLowerCase() !== expectedPackageSha256.toLowerCase()) {
      statusConfirmed.value = false;
      showNotice(t("panel.update.resultUnconfirmed"), true);
      return;
    }
    statusConfirmed.value = true;
    showNotice(t("panel.update.waitingRestart"), false);
    startPostInstallWatch(expectedVersion, checked.currentVersion, checked.lastAttempt, result);
  } catch (error) {
    if (current() && revision === statusRevision) { statusConfirmed.value = false; showNotice(resolveApiMessage(error), true); }
  } finally {
    if (current() && revision === statusRevision) busy.value = "";
  }
}

// Kurulum planlandiktan sonra paneli izle: servis yeniden başlarken istekler düşer (sessizce beklenir),
// panel YENİ sürümle dönünce sayfa otomatik yenilenir (Plesk davranışı). Oturum düşmüşse (401) login'e döner.
let postInstallHandle: number | null = null;
let progressHandle: number | null = null;
let reloadHandle: number | null = null;
let watchGeneration = 0;

function sameVersion(first: string, second: string) {
  const normalize = (value: string) => value.trim().replace(/^v/i, "").replace(/(?:\.0)+$/, "");
  return normalize(first) === normalize(second);
}

function startPostInstallWatch(expectedVersion: string, previousVersion: string, previousAttempt: UpdateAttempt | null, scheduledStatus: UpdateStatus) {
  stopPostInstallWatch();
  installElapsedSeconds.value = 0;
  installTargetVersion.value = "";
  installAttempt.value = null;
  const scheduledAt = Date.parse(scheduledStatus.installScheduledAtUtc ?? "");
  const checkedAt = Date.parse(scheduledStatus.checkedAtUtc);
  if (!expectedVersion.trim() || !Number.isFinite(scheduledAt) || !Number.isFinite(checkedAt) || scheduledAt > checkedAt) {
    statusConfirmed.value = false;
    showNotice(t("panel.update.resultUnconfirmed"), true);
    return;
  }
  // Both timestamps come from the server. Only elapsed time since this read uses
  // the browser clock, so clock skew and reopening the view cannot extend the window.
  const observedAge = checkedAt - scheduledAt;
  const generation = watchGeneration, startedAt = Date.now();
  const previousAttemptIdentity = JSON.stringify(previousAttempt);
  const owned = () => current() && generation === watchGeneration;
  monitoring.value = true;
  installPhase.value = "scheduled";
  installTargetVersion.value = expectedVersion;
  installElapsedSeconds.value = Math.floor(observedAge / 1000);
  installStartedAtIso = new Date(scheduledAt).toISOString();
  const reloadLater = () => {
    reloadHandle = window.setTimeout(() => {
      reloadHandle = null;
      if (current()) window.location.reload();
    }, 1500);
  };
  const updateElapsed = () => {
    if (!owned()) { stopPostInstallWatch(true); return; }
    const elapsed = observedAge + Math.max(0, Date.now() - startedAt);
    installElapsedSeconds.value = Math.floor(elapsed / 1000);
    if (elapsed >= 15 * 60_000) {
      installPhase.value = "timeout";
      statusConfirmed.value = false;
      stopPostInstallWatch(true);
      showNotice(t("panel.update.updateTimeout"), true);
    }
  };
  updateElapsed();
  if (!owned()) return;
  // Keep the original deadline independent of HTTP completion, including hung responses.
  progressHandle = window.setInterval(updateElapsed, 1000);
  const poll = async () => {
    postInstallHandle = null;
    if (!owned()) { stopPostInstallWatch(true); return; }
    try {
      const fresh = await checkForUpdate();
      if (!owned()) return;
      status.value = fresh;
      statusConfirmed.value = true;
      const attempt = fresh.lastAttempt;
      const finished = attempt?.isFinished === true && attemptIsNewerThan(attempt, installStartedAtIso) && JSON.stringify(attempt) !== previousAttemptIdentity;
      if (sameVersion(fresh.currentVersion, expectedVersion) &&
        (!sameVersion(previousVersion, expectedVersion) || (finished && attempt.isFailed === false))) {
        installPhase.value = "done";
        stopPostInstallWatch(true);
        showNotice(t("panel.update.updated"), false);
        reloadLater();
        return;
      }
      if (finished) {
        installPhase.value = "failed";
        installAttempt.value = attempt;
        stopPostInstallWatch(true);
        showNotice(attempt.isFailed === true ? describeAttemptFailure(attempt) : t("panel.update.resultUnconfirmed"), true);
        return;
      }
      installPhase.value = installPhase.value === "restarting" || installPhase.value === "verifying" ? "verifying" : "scheduled";
    } catch (error) {
      if (!owned()) return;
      statusConfirmed.value = false;
      if ((error as { response?: { status?: number } })?.response?.status === 401) {
        stopPostInstallWatch(true);
        if (current()) window.location.reload();
        return;
      }
      installPhase.value = "restarting";
    }
    if (owned()) postInstallHandle = window.setTimeout(() => { void poll(); }, 5000);
  };
  postInstallHandle = window.setTimeout(() => { void poll(); }, 5000);
}

/** Invalidate in-flight polls as well as scheduled callbacks. */
function stopPostInstallWatch(keepProgress = false) {
  watchGeneration++;
  monitoring.value = false;
  if (postInstallHandle !== null) { window.clearTimeout(postInstallHandle); postInstallHandle = null; }
  if (progressHandle !== null) { window.clearInterval(progressHandle); progressHandle = null; }
  if (reloadHandle !== null) { window.clearTimeout(reloadHandle); reloadHandle = null; }
  if (!keepProgress) installPhase.value = "";
}

/** Durum dosyasi BU denemeye mi ait? Onceki bir denemeden kalan dosya yanlis sonuc gosterirdi. */
function attemptIsNewerThan(attempt: UpdateAttempt, startedAtIso: string) {
  if (!attempt.updatedAtUtc || !startedAtIso) {
    return false;
  }

  // Calistirici gorevi 15 saniye sonra basliyor; saat kaymalarina karsi kucuk bir tolerans birakiyoruz.
  return new Date(attempt.updatedAtUtc).getTime() >= new Date(startedAtIso).getTime() - 30_000;
}

function describeAttemptFailure(attempt: UpdateAttempt) {
  // Cikis kodu 5 = kurulum yardimcisi iptal etti (dosyalar kullanimdaydi). Kullaniciya ham sayi
  // yerine ne oldugunu soyle.
  if (attempt.installerExitCode === 5) {
    return t("panel.update.attemptAborted");
  }

  // Hukmu panel verdiyse sebep serbest metin degil bir ceviri anahtaridir (dil secimine uyar).
  if (attempt.errorKey) {
    return translate(attempt.errorKey);
  }

  return attempt.error
    ? t("panel.update.attemptFailedWithReason", { reason: attempt.error })
    : t("panel.update.attemptFailed");
}

function formatElapsed(seconds: number) {
  const minutes = Math.floor(seconds / 60);
  const rest = seconds % 60;
  return minutes > 0
    ? t("panel.update.elapsedMinutes", { minutes, seconds: rest })
    : t("panel.update.elapsedSeconds", { seconds: rest });
}

onBeforeUnmount(() => { disposed = true; statusRevision++; stopPostInstallWatch(); });

/**
 * Arka uç bu alana İKİ farklı şey koyar: ya bir çeviri ANAHTARI ("update.package_staged"),
 * ya da GitHub sürüm notlarının tam metni. Ayrımı ölçerek yapıyoruz: anahtarlar boşluk ya da
 * satır sonu içermez.
 *
 * Serbest metni t()'ye VERMİYORUZ -- vue-i18n `{...}` gördüğünde onu yer tutucu sanar; kod
 * örneği ya da JSON içeren bir sürüm notu bu yüzden bozulurdu.
 */
const notlar = computed(() => {
  const ham = (status.value?.notes ?? "").trim();
  if (!ham) return { anahtar: "", metin: "" };
  if (/^[A-Za-z0-9_.]+$/.test(ham)) {
    return { anahtar: translate(ham), metin: "" };
  }
  return { anahtar: "", metin: ham };
});

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
/* 16px -> --sp-12: `.panel` kendi izgara araligini 12px veriyor (base.css). Bu ekran 16px
   yazdigi icin ust baslik ile ilk kutu arasi 12, kutular arasi 16 oluyordu -- ayni sayfada iki
   ritim. Ayrica 16 ve 12, olcegin iki komsu adimi; farki gorunur ama gerekcesi yoktu. */
.update-panel,
.detail-grid,
.update-actions {
  display: grid;
  gap: var(--sp-12);
}

/* Uc kutucuk: ortak serit 4 sutunlu, burada 3 deger var. Dar ekran davranisi (900/720)
   base.css'ten MIRAS ALINIR, burada tekrarlanmaz. */
.update-summary {
  grid-template-columns: repeat(3, minmax(0, 1fr));
}

.up-to-date {
  padding: var(--sp-10) var(--sp-12);
  border: 1px solid color-mix(in srgb, var(--ok-1) 40%, var(--panel-border));
  border-radius: var(--radius-sm);
  background: color-mix(in srgb, var(--ok-1) 8%, var(--surface-1));
  color: var(--text-strong);
}

.install-progress {
  --watch-color: var(--accent-1);
  display: grid;
  gap: var(--sp-16);
  padding: var(--sp-16);
  border: 1px solid color-mix(in srgb, var(--watch-color) 25%, var(--panel-border));
  border-left: 3px solid var(--watch-color);
  border-radius: var(--radius-sm);
  background: var(--surface-1);
  min-width: 0;
}
.install-progress.is-done { --watch-color: var(--ok-1); }
.install-progress.is-failed { --watch-color: var(--danger-1); }
.install-progress.is-timeout { --watch-color: var(--warn-1); }
.install-progress-head { display: flex; align-items: flex-start; gap: var(--sp-12); }
.install-phase-icon { display: grid; place-items: center; flex: 0 0 38px; height: 38px; border-radius: 50%; color: var(--text-strong); background: color-mix(in srgb, var(--watch-color) 10%, var(--surface-1)); }
.install-phase-icon svg { width: 22px; height: 22px; }
.install-progress-copy { min-width: 0; }
.install-progress-copy h3 { margin: 0 0 var(--sp-4); font-size: var(--fs-md); line-height: 1.5; overflow-wrap: anywhere; }
.install-progress-copy p { line-height: 1.6; overflow-wrap: anywhere; }
.install-observations { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--sp-12); margin: 0; padding-top: var(--sp-12); border-top: 1px solid var(--panel-border); }
.install-observations > div { min-width: 0; }
.install-observations dt { color: var(--text-muted); font-size: var(--fs-xs); }
.install-observations dd { margin: var(--sp-4) 0 0; font-weight: 600; font-variant-numeric: tabular-nums; overflow-wrap: anywhere; }
@media (max-width: 420px) { .install-progress-head { flex-direction: column; } }

/* 3 -> 2 sutun: izgarada YALNIZ IKI oge var (SHA256 ve paket yolu). Ucuncu sutun her zaman
   bostu ve iki uzun deger, bos bir sutunun yaninda gereksiz yere daraliyordu. */
.detail-grid {
  grid-template-columns: repeat(2, minmax(0, 1fr));
  margin: 0;
}

.detail-grid div {
  display: grid;
  gap: var(--sp-4);
  min-width: 0;
}

/* SHA256 ve paket yolu: bosluksuz uzun diziler. `word-break` (asagida) satiri kirar ama tek
   aralikli yazi tipi olmadan 64 karakterlik ozet gozle karsilastirilamaz. */
.detail-grid dd {
  font-family: var(--font-mono);
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
  grid-template-columns: max-content max-content;
  align-items: center;
}

.update-next-step { display: grid; gap: var(--sp-12); padding: var(--sp-16); border: 1px solid var(--panel-border); border-radius: var(--radius-md); }
.next-step-heading h3, .update-notes h3 { margin: 0 0 var(--sp-8); }
.next-step-heading p { line-height: 1.6; max-width: 80ch; }
.update-notes { min-width: 0; padding-top: var(--sp-8); }
.update-details { border-top: 1px solid var(--panel-border); padding-top: var(--sp-12); min-width: 0; }
.update-details summary { cursor: pointer; color: var(--text-muted); padding: var(--sp-8) 0; width: fit-content; }
.update-details summary:hover { color: var(--text-strong); }
.update-details summary:focus-visible { outline: 2px solid var(--focus-ring); outline-offset: 3px; border-radius: var(--radius-sm); }
.update-details .detail-grid { padding: var(--sp-12) 0; }

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
