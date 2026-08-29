<template>
  <section class="panel advisor-panel">
    <div class="section-head">
      <div>
        <h2>{{ t("panel.advisor.title") }}</h2>
        <p class="meta">{{ t("panel.advisor.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading" @click="refresh">
        {{ loading ? t("panel.common.loading") : t("panel.advisor.refresh") }}
      </button>
    </div>

    <p v-if="notice" class="notice page-notice error" role="alert">{{ notice }}</p>

    <!-- SIFIR SAYAÇ TEHLİKE RENGİNDE OLMAZ.
         ÖLÇÜLDÜ: "0 kritik" rozeti kırmızı dolgu + kırmızı kenarla çiziliyordu; panelin
         tehlike dili tam da "burada bir sorun var" demek için ayrılmış. Sıfır, sorunun
         YOKLUĞUDUR — sayaç ancak sayı > 0 iken kendi tonunu alır, sıfırda nötre düşer.
         Renk yine tek kanal değil: sayı ve önem sözcüğü rozetin İÇİNDE yazılı. -->
    <div v-if="report" class="advisor-summary">
      <span :class="['sev-pill', report.criticalCount > 0 ? 'critical' : 'zero']">
        {{ report.criticalCount }} {{ t("panel.advisor.critical") }}
      </span>
      <span :class="['sev-pill', report.warningCount > 0 ? 'warning' : 'zero']">
        {{ report.warningCount }} {{ t("panel.advisor.warning") }}
      </span>
      <span class="sev-pill info">{{ report.infoCount }} {{ t("panel.advisor.info") }}</span>
      <span class="meta checked-at">{{ t("panel.advisor.checkedAt") }}: {{ formatUtc(report.checkedAtUtc) }}</span>
    </div>

    <!-- Bazı kontroller ÇALIŞTIRILAMADIYSA "her şey yolunda" demek yalan olur: hiçbir şey
         ölçülememişken de öneri listesi boş çıkar. Bu durumu ayrı ve açık şekilde göster. -->
    <div v-if="report && report.failedChecks?.length" class="notice warn" role="status">
      {{ t("panel.advisor.checksFailed", { checks: report.failedChecks.join(", ") }) }}
    </div>

    <div v-if="report && report.items.length === 0 && !report.failedChecks?.length" class="advisor-clear">
      <span class="clear-badge">✓</span>
      <div>
        <strong>{{ t("panel.advisor.allClearTitle") }}</strong>
        <p class="meta">{{ t("panel.advisor.allClearHint") }}</p>
      </div>
    </div>

    <ul v-else-if="report" class="advisor-list">
      <li v-for="item in report.items" :key="item.id" :class="['advisor-item', item.severity]">
        <!-- Önem YALNIZ RENKLE anlatılıyordu (kırmızı/sarı/mavi nokta, `aria-hidden`). Renk körü
             kullanıcı ve ekran okuyucu için madde arasındaki fark YOK OLUYORDU: "kritik" ile
             "bilgi" aynı görünüyordu. Nokta görsel kalıyor, önem ayrıca METİN olarak veriliyor. -->
        <span class="sev-dot" :class="item.severity" aria-hidden="true"></span>
        <span class="sr-only">{{ severityText(item.severity) }}</span>
        <div class="advisor-body">
          <strong>{{ t(item.titleKey) }}</strong>
          <p v-if="item.detailKey || item.detail" class="advisor-detail">
            <span v-if="item.detailKey">{{ t(item.detailKey) }} </span>
            <code v-if="item.detail">{{ item.detail }}</code>
          </p>
        </div>
        <!-- ALTI SATIRIN ALTISI DA AYNI DÜĞMEYDİ: kritik bir bulgunun "Düzelt"i ile bilgi
             niteliğindeki bir satırınki birebir aynı çiziliyordu, yani ekranda hiyerarşi
             YOKTU ve göz nereden başlayacağını rozetin rengine bakarak çıkarmak zorundaydı.
             Yalnız KRİTİK satırların düğmesi birincil olur (panelin `compact` düğmesi);
             uyarı ve bilgi satırları ikincil kalır. Düğme sayısı, yeri ve metni AYNI. -->
        <button
          v-if="item.actionSection"
          :class="['compact', { secondary: item.severity !== 'critical' }]"
          @click="go(item.actionSection)">
          {{ t("panel.advisor.fix") }}
        </button>
      </li>
    </ul>
  </section>
</template>

<script setup lang="ts">
import { onMounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import { getAdvisorReport, type AdvisorReport } from "../api/advisor";
import { extractApiMessage } from "../stores/auth";
import { formatDateTime } from "../utils/datetime";

const { t } = useI18n();
const emit = defineEmits<{ (event: "navigate", sectionId: string): void }>();

const report = ref<AdvisorReport | null>(null);
const loading = ref(false);
const notice = ref("");

onMounted(refresh);

async function refresh() {
  loading.value = true;
  notice.value = "";
  try {
    report.value = await getAdvisorReport();
  } catch (error) {
    const key = extractApiMessage(error);
    const translated = t(key);
    notice.value = translated === key ? key : translated;
  } finally {
    loading.value = false;
  }
}

function go(sectionId: string) {
  emit("navigate", sectionId);
}

/** Önem noktasının METİN karşılığı — renk tek başına bilgi taşıyamaz. */
function severityText(severity: "critical" | "warning" | "info"): string {
  const keys = {
    critical: "panel.common.severityCritical",
    warning: "panel.common.severityWarning",
    info: "panel.common.severityInfo"
  } as const;
  return t(keys[severity]);
}

/** Tarih bicimi panelin geri kalaniyla AYNI (bkz. utils/datetime.ts). */
function formatUtc(value: string) {
  return formatDateTime(value, "-");
}
</script>

<style scoped>
.advisor-panel {
  display: grid;
  gap: 16px;
}

.advisor-summary {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 10px;
}

.sev-pill {
  padding: 4px 12px;
  border-radius: 999px;
  font-size: 0.82rem;
  font-weight: 700;
  border: 1px solid var(--panel-border);
}

.sev-pill.critical {
  color: var(--danger-text);
  background: var(--danger-bg);
  border-color: color-mix(in srgb, var(--danger-1) 40%, transparent);
}

.sev-pill.warning {
  color: var(--warn-text);
  background: var(--warn-bg);
  border-color: color-mix(in srgb, var(--warn-1) 40%, transparent);
}

.sev-pill.info {
  color: var(--text-soft);
  background: var(--surface-1);
}

/* SIFIR SAYAÇ: kendi önem rengini almaz. "Bilgi" rozetiyle aynı nötr yüzey, ama yazısı bir adım
   daha geride — sıfır, okunması gereken bir sayı değil, bakılıp geçilecek bir sayıdır. */
.sev-pill.zero {
  color: var(--text-muted);
  background: var(--surface-1);
}

.checked-at {
  margin-left: auto;
}

.advisor-clear {
  display: flex;
  align-items: center;
  gap: 14px;
  padding: 18px;
  border: 1px solid color-mix(in srgb, var(--ok-1) 40%, var(--panel-border));
  border-radius: var(--radius-md);
  background: color-mix(in srgb, var(--ok-1) 8%, var(--surface-1));
}

.clear-badge {
  display: grid;
  place-items: center;
  width: 40px;
  height: 40px;
  border-radius: 50%;
  background: color-mix(in srgb, var(--ok-1) 20%, transparent);
  color: var(--ok-text);
  font-size: 1.3rem;
  font-weight: 800;
  flex-shrink: 0;
}

.advisor-clear p {
  margin: 2px 0 0;
}

.advisor-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  gap: 10px;
}

.advisor-item {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 14px 16px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--surface-1);
}

.advisor-item.critical {
  border-color: color-mix(in srgb, var(--danger-1) 45%, var(--panel-border));
}

.advisor-item.warning {
  border-color: color-mix(in srgb, var(--warn-1) 40%, var(--panel-border));
}

.sev-dot {
  width: 10px;
  height: 10px;
  border-radius: 50%;
  flex-shrink: 0;
}

.sev-dot.critical {
  background: var(--danger-1);
}

.sev-dot.warning {
  background: var(--warn-1);
}

.sev-dot.info {
  background: var(--text-muted);
}

.advisor-body {
  flex: 1;
  min-width: 0;
  display: grid;
  gap: 2px;
}

.advisor-detail {
  margin: 0;
  color: var(--text-soft);
  font-size: 0.85rem;
}

.advisor-detail code {
  font-family: ui-monospace, "Cascadia Code", Consolas, monospace;
}

@media (max-width: 720px) {
  .advisor-item {
    flex-wrap: wrap;
  }
}
</style>
