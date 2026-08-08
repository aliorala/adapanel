<template>
  <section class="panel update-panel">
    <div class="section-head">
      <div>
        <h2>{{ t("panel.update.title") }}</h2>
        <p class="meta">{{ t("panel.update.subtitle") }}</p>
      </div>
      <button class="secondary compact" @click="refresh(true)">
        {{ t("panel.update.refresh") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <!-- Sürüm durumu gelmeden ekran boştu; yükleniyor ile "veri yok" ayırt edilemiyordu. -->
    <p v-if="!status" class="empty-state" role="status" aria-live="polite">{{ t("panel.common.loading") }}</p>

    <template v-else>
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

      <!-- Kontrol BAŞARISIZSA "güncelsiniz" demek yalan olur: yeni sürüm yayınlanmış olabilir
           ve panel onu görememiştir (en sık sebep GitHub'ın saatlik istek sınırı). -->
      <p v-if="status.isCheckFailed" class="notice warn">{{ t("panel.update.checkFailed") }}</p>
      <p v-else-if="!status.isUpdateAvailable" class="meta up-to-date">✓ {{ t("panel.update.upToDate") }}</p>

      <dl class="detail-grid">
        <div><dt>{{ t("panel.update.notes") }}</dt><dd>{{ translate(status.notes) }}</dd></div>
        <div><dt>{{ t("panel.update.sha256") }}</dt><dd>{{ status.packageSha256 || "-" }}</dd></div>
        <div><dt>{{ t("panel.update.packagePath") }}</dt><dd>{{ status.packagePath || "-" }}</dd></div>
      </dl>

      <!-- Kurulum ilerlemesi. Çubuk ZAMANLAYICIYLA değil, gözlemlenen adımlarla ilerler; her yüzde
           sunucudan gelen somut bir cevaba karşılık gelir. Panel kurulum sırasında kapalı olduğu
           için o aralıkta ara bir sayı üretilmez — uydurma yüzde göstermek yerine geçen süre yazılır.
           %100 yalnızca yeni sürüm DOĞRULANINCA görünür; başarısızlıkta çubuk ilerlemez. -->
      <div v-if="installPhase" class="install-progress" role="status" aria-live="polite">
        <div
          class="progress-track"
          role="progressbar"
          :aria-valuenow="installPercent"
          aria-valuemin="0"
          aria-valuemax="100">
          <div class="progress-fill" :class="{ done: installPhase === 'done' }" :style="{ width: installPercent + '%' }"></div>
        </div>
        <div class="progress-meta">
          <strong>{{ t(`panel.update.phase.${installPhase}`) }}</strong>
          <span class="meta">%{{ installPercent }} · {{ formatElapsed(installElapsedSeconds) }}</span>
        </div>
        <p class="meta">{{ t("panel.update.phaseHintReal") }}</p>
      </div>

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
import { extractApiMessage } from "../stores/auth";

const { t } = useI18n();
const { confirm } = useConfirm();
const status = ref<UpdateStatus | null>(null);
const busy = ref<"" | "download" | "install">("");
const notice = ref("");
const noticeError = ref(false);

/**
 * Kurulumun GÖZLEMLENEN aşaması — tahmin değil, her yoklamada gerçekten ölçülen durum:
 * scheduled  = panel hâlâ yanıt veriyor, kurulum henüz servisi durdurmadı
 * restarting = istek düşüyor, servis duruyor/yeniden başlıyor (beklenen)
 * verifying  = panel geri döndü, sürüm karşılaştırılıyor
 * done       = yeni sürüm doğrulandı
 * timeout    = 15 dakika doldu
 */
const installPhase = ref<"" | "scheduled" | "restarting" | "verifying" | "done" | "failed" | "timeout">("");
const installElapsedSeconds = ref(0);
const installAttempt = ref<UpdateAttempt | null>(null);
// Bu denemenin baslangic ani: calistiricinin BU denemeye ait durumu mu, yoksa onceki bir denemeden
// kalan eski dosya mi oldugunu ayirt etmek icin sart.
let installStartedAtIso = "";

/**
 * Yüzde, ZAMANLAYICIYLA DEĞİL gerçekten gözlemlenen kilometre taşlarıyla ilerler. Her değer,
 * sunucudan alınan somut bir cevaba karşılık gelir:
 *
 *   %10  kurulum planlandı   → API "planlandı" dedi
 *   %35  panel hâlâ ayakta   → istek başarılı, sürüm eski (kurulum henüz servisi durdurmadı)
 *   %60  panel kapandı       → istek düşüyor; dosyaların değiştirildiği an
 *   %85  panel geri döndü    → istek yeniden başarılı, sürüm karşılaştırılıyor
 *   %100 sürüm doğrulandı    → yeni sürüm okundu
 *
 * Kapalı olduğu aralıkta ara bir yüzde ÜRETİLMEZ: paneli durduran şey aynı zamanda ilerlemeyi
 * bildirecek olan şeydir, dolayısıyla o pencerede sunucudan sinyal gelmesi mümkün değildir.
 * Uydurma bir sayı yerine geçen süre ayrıca gösterilir.
 */
const installPercent = computed(() => {
  switch (installPhase.value) {
    case "scheduled":
      return installElapsedSeconds.value > 0 ? 35 : 10;
    case "restarting":
      return 60;
    case "verifying":
      return 85;
    case "done":
      return 100;
    case "failed":
    case "timeout":
      // Basarisizlikta cubugu 100'e cekmek "tamamlandi" izlenimi verirdi; oldugu yerde birakiyoruz.
      return 60;
    default:
      return 0;
  }
});

// Ekrana GİRMEK "kontrol et" demektir: kullanıcı buraya güncelleme var mı diye bakmaya gelir,
// önbellekten bayat cevap vermek bu ekranın tek işini boşa çıkarır.
onMounted(async () => {
  await refresh(true);
});

// NOT: Güncelleme kaynağı (GitHub depo) ayarı kullanıcı isteğiyle UI'dan KALDIRILDI; backend
// varsayılanı (SelfUpdateService.DefaultRepo) kullanılır, gerekirse config/update-github.json'dan değişir.
async function refresh(force = true) {
  try {
    status.value = await checkForUpdate(force);
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
let progressHandle: number | null = null;

function startPostInstallWatch(versionBeforeInstall: string) {
  stopPostInstallWatch();
  const startedAt = Date.now();
  installPhase.value = "scheduled";
  installElapsedSeconds.value = 0;
  installAttempt.value = null;
  installStartedAtIso = new Date().toISOString();

  // Çubuk saniyede bir ilerler; sunucuya 5 saniyede bir sorulur. İkisini ayırmak, bekleyen
  // kullanıcının donmuş bir ekrana bakmamasını sağlar.
  progressHandle = window.setInterval(() => {
    installElapsedSeconds.value = Math.round((Date.now() - startedAt) / 1000);
  }, 1000);
  postInstallHandle = window.setInterval(async () => {
    // 15 dakika: kurulum servisi durdurur, ~50 MB'lık paketi açar, şema göçlerini çalıştırır ve
    // servisi yeniden başlatır. Yüklü bir sunucuda (yavaş disk, kurulum dosyasını tarayan antivirüs)
    // bu 5 dakikayı rahatça aşar. Süre kısa olduğunda kullanıcı, aslında BAŞARIYLA süren bir
    // güncellemeyi başarısız sanıyordu.
    if (Date.now() - startedAt > 15 * 60_000) {
      installPhase.value = "timeout";
      stopPostInstallWatch(true);
      showNotice(t("panel.update.updateTimeout"), true);
      return;
    }

    try {
      const fresh = await checkForUpdate();
      if (fresh.currentVersion && fresh.currentVersion !== versionBeforeInstall) {
        installPhase.value = "done";
        stopPostInstallWatch(true);
        status.value = fresh;
        showNotice(t("panel.update.updated"), false);
        window.setTimeout(() => window.location.reload(), 1500);
        return;
      }

      // Panel yanıt veriyor ve sürüm DEĞİŞMEDİ. Burada kritik soru şu: kurulum hâlâ sürüyor mu,
      // yoksa çoktan bitip BAŞARISIZ mı oldu? Eskiden bu ayrım yapılmıyordu; kurulum saniyeler
      // içinde iptal olsa bile ekran dakikalarca "panel yeniden başlatılıyor" diyor, kullanıcı ne
      // sonucu ne sebebini görüyordu. Çalıştırıcının yazdığı durum bu soruyu kesin yanıtlar.
      const attempt = fresh.lastAttempt;
      if (attempt?.isFinished && attemptIsNewerThan(attempt, installStartedAtIso)) {
        installPhase.value = "failed";
        installAttempt.value = attempt;
        stopPostInstallWatch(true);
        status.value = fresh;
        showNotice(describeAttemptFailure(attempt), true);
        return;
      }

      // Kurulum daha servisi durdurmadı (zamanlanmış görev 15 saniye sonra başlıyor) ya da
      // panel yeni açıldı ve sürüm doğrulanıyor.
      installPhase.value = installPhase.value === "restarting" ? "verifying" : "scheduled";
    } catch (error) {
      if ((error as { response?: { status?: number } })?.response?.status === 401) {
        stopPostInstallWatch(true);
        window.location.reload();
        return;
      }

      // İstek düştü: servis şu an duruyor/yeniden başlıyor. BEKLENEN durum, hata değil.
      installPhase.value = "restarting";
    }
  }, 5000);
}

/** @param keepProgress true ise ilerleme kutusu ekranda kalır (bitti/zaman aşımı durumunu göstermek için). */
function stopPostInstallWatch(keepProgress = false) {
  if (postInstallHandle !== null) {
    window.clearInterval(postInstallHandle);
    postInstallHandle = null;
  }

  if (progressHandle !== null) {
    window.clearInterval(progressHandle);
    progressHandle = null;
  }

  if (!keepProgress) {
    installPhase.value = "";
  }
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

.install-progress {
  display: grid;
  gap: 8px;
  padding: 14px 16px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  background: var(--surface-1);
}

.progress-track {
  height: 8px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--text-strong) 12%, transparent);
  overflow: hidden;
}

.progress-fill {
  height: 100%;
  border-radius: 999px;
  background: var(--accent-1, #38bdf8);
  /* Adımlar arası sıçrama yumuşasın; süre kısa tutuluyor ki ilerleme "akıyormuş" gibi görünüp
     olduğundan fazlasını ima etmesin. */
  transition: width 400ms ease;
}

.progress-fill.done {
  background: var(--ok-1, #22c55e);
}

.progress-meta {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
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
