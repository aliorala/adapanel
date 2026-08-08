<template>
  <section class="query-history">
    <!-- NE OLMADIĞINI DA SÖYLÜYORUZ. Bu bir örnekleme kaydıdır; iki örnek arasında başlayıp biten
         çok kısa sorgular kaydedilmemiş olabilir. "Her sorgu burada" izlenimi vermek, kullanıcının
         eksik veriye dayanıp yanlış sonuca varmasına yol açardı. -->
    <p class="field-hint history-scope">
      {{ t("panel.queryLog.scopeHint", { seconds: page?.sampleIntervalSeconds ?? 5, days: page?.retentionDays ?? 30 }) }}
    </p>

    <div class="history-filters">
      <label class="stacked-field">
        <span class="field-label">{{ t("panel.queryLog.day") }}</span>
        <select v-model="filters.day" class="compact-select">
          <option value="">{{ t("panel.queryLog.allDays") }}</option>
          <option v-for="day in page?.availableDays ?? []" :key="day" :value="day">{{ day }}</option>
        </select>
      </label>

      <label class="stacked-field">
        <span class="field-label">{{ t("panel.queryLog.source") }}</span>
        <select v-model="filters.source" class="compact-select">
          <option value="">{{ t("panel.queryLog.allSources") }}</option>
          <option value="mariadb">MariaDB</option>
          <option value="mysql">MySQL</option>
          <option value="postgresql">PostgreSQL</option>
        </select>
      </label>

      <label class="stacked-field">
        <span class="field-label">{{ t("panel.queryLog.site") }}</span>
        <select v-model="filters.siteName" class="compact-select">
          <option value="">{{ t("panel.queryLog.allSites") }}</option>
          <option v-for="site in siteOptions" :key="site" :value="site">{{ site }}</option>
        </select>
      </label>

      <label class="stacked-field">
        <span class="field-label">{{ t("panel.queryLog.database") }}</span>
        <select v-model="filters.target" class="compact-select">
          <option value="">{{ t("panel.queryLog.allDatabases") }}</option>
          <option v-for="db in databaseOptions" :key="db" :value="db">{{ db }}</option>
        </select>
      </label>

      <label class="stacked-field">
        <span class="field-label">{{ t("panel.queryLog.minDuration") }}</span>
        <select v-model.number="filters.minDurationSeconds" class="compact-select">
          <option :value="0">{{ t("panel.queryLog.anyDuration") }}</option>
          <option v-for="s in [1, 3, 5, 10, 30, 60]" :key="s" :value="s">{{ t("panel.queryLog.atLeast", { seconds: s }) }}</option>
        </select>
      </label>

      <label class="stacked-field history-search">
        <span class="field-label">{{ t("panel.queryLog.search") }}</span>
        <input v-model.trim="filters.search" :placeholder="t('panel.queryLog.searchPlaceholder')" spellcheck="false" />
      </label>

      <label class="checkbox-field">
        <input v-model="filters.remoteOnly" type="checkbox" />
        <span>{{ t("panel.queryLog.remoteOnly") }}</span>
      </label>

      <button class="secondary compact" :disabled="loading" @click="resetFilters">
        {{ t("panel.queryLog.reset") }}
      </button>
    </div>

    <p v-if="error" class="notice error" role="alert">{{ error }}</p>
    <p v-else-if="loading && !page" class="empty-state">{{ t("panel.common.loading") }}</p>

    <template v-else-if="page">
      <!-- ÖZET ÖNCE: "hangi sorgu çok çalışmış" sorusu, tek tek satırları okumadan cevaplanır.
           Sıralama toplam süreye göre — sunucuyu asıl yoran, çok çalışan değil çok SÜREN olabilir. -->
      <div v-if="page.topShapes.length > 0" class="history-summary">
        <h4>{{ t("panel.queryLog.topTitle") }}</h4>
        <p class="meta">{{ t("panel.queryLog.topHint") }}</p>
        <div class="data-table">
          <div class="data-row table-head shape-row">
            <span>{{ t("panel.queryLog.shape") }}</span>
            <span>{{ t("panel.queryLog.count") }}</span>
            <span>{{ t("panel.queryLog.totalTime") }}</span>
            <span>{{ t("panel.queryLog.site") }}</span>
          </div>
          <div v-for="(row, index) in page.topShapes" :key="index" class="data-row shape-row">
            <span class="cell-main mono">
              <strong>{{ row.shape }}</strong>
              <small v-if="row.sample && row.sample !== row.shape">{{ row.sample }}</small>
            </span>
            <span>{{ row.count }}</span>
            <span class="cell-main">
              <strong>{{ formatSeconds(row.totalSeconds) }}</strong>
              <small>{{ t("panel.queryLog.longest", { time: formatSeconds(row.maxSeconds) }) }}</small>
            </span>
            <span>{{ row.siteName || "—" }}</span>
          </div>
        </div>
      </div>

      <div class="history-count">
        {{ t("panel.queryLog.matchCount", { count: page.totalCount }) }}
      </div>

      <p v-if="page.entries.length === 0" class="empty-state">{{ t("panel.queryLog.empty") }}</p>

      <div v-else class="data-table">
        <div class="data-row table-head entry-row">
          <span>{{ t("panel.queryLog.ended") }}</span>
          <span>{{ t("panel.queryLog.duration") }}</span>
          <span>{{ t("panel.queryLog.who") }}</span>
          <span>{{ t("panel.queryLog.query") }}</span>
        </div>
        <div v-for="(entry, index) in page.entries" :key="index" class="data-row entry-row">
          <span class="cell-main">
            <strong>{{ formatTime(entry.endedAtUtc) }}</strong>
            <small>{{ entry.source }}</small>
          </span>
          <span :class="['status-pill', durationClass(entry.durationSeconds)]">
            {{ formatSeconds(entry.durationSeconds) }}
          </span>
          <span class="cell-main">
            <strong>
              {{ entry.siteName || entry.target || "—" }}
              <span v-if="entry.remoteClient" class="remote-badge">{{ t("panel.queryLog.remote") }}</span>
            </strong>
            <small>{{ [entry.target, entry.user, entry.clientHost, entry.ownerName].filter(Boolean).join(" · ") }}</small>
          </span>
          <span class="cell-main mono">
            <strong>{{ entry.statement || "—" }}</strong>
            <!-- Maskeleme GİZLENMEZ: kullanıcı metnin neden eksik göründüğünü bilmeli. -->
            <small v-if="entry.redacted">{{ t("panel.queryLog.redacted") }}</small>
          </span>
        </div>
      </div>

      <div v-if="page.totalCount > filters.take" class="row-actions history-pager">
        <button class="secondary compact" :disabled="loading || filters.skip <= 0" @click="pageBy(-1)">
          {{ t("panel.queryLog.previous") }}
        </button>
        <span class="meta">
          {{ t("panel.queryLog.pageRange", {
            from: filters.skip + 1,
            to: Math.min(filters.skip + filters.take, page.totalCount),
            total: page.totalCount
          }) }}
        </span>
        <button
          class="secondary compact"
          :disabled="loading || filters.skip + filters.take >= page.totalCount"
          @click="pageBy(1)">
          {{ t("panel.queryLog.next") }}
        </button>
      </div>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { getQueryHistory, type QueryLogPage } from "../api/queryLog";
import { extractApiDetails, extractApiMessage } from "../stores/auth";

const { t } = useI18n();

const page = ref<QueryLogPage | null>(null);
const loading = ref(false);
const error = ref("");
let controller: AbortController | null = null;
let debounce: number | null = null;

const filters = reactive({
  day: "",
  source: "",
  siteName: "",
  target: "",
  minDurationSeconds: 0,
  search: "",
  remoteOnly: false,
  skip: 0,
  take: 100
});

/**
 * Site ve veritabanı seçenekleri, O ANKİ sonuçtan türetilir.
 *
 * Ayrı bir "tüm siteler" ucu çağırmıyoruz: burada anlamlı olan, geçmişte GERÇEKTEN sorgu çalıştırmış
 * olanlardır. Hiç sorgusu olmayan siteyi listelemek, seçilince boş sonuç veren ölü bir seçenek olurdu.
 */
const siteOptions = computed(() => tekilDegerler((entry) => entry.siteName));
const databaseOptions = computed(() => tekilDegerler((entry) => entry.target));

function tekilDegerler(sec: (entry: QueryLogPage["entries"][number]) => string) {
  const kume = new Set<string>();
  for (const entry of page.value?.entries ?? []) {
    const deger = (sec(entry) || "").trim();
    if (deger) {
      kume.add(deger);
    }
  }
  // Seçili değer sonuçta yoksa da listede kalsın; aksi halde filtre kendini siler.
  if (filters.siteName) kume.add(filters.siteName);
  if (filters.target) kume.add(filters.target);
  return [...kume].sort((a, b) => a.localeCompare(b));
}

function formatSeconds(total: number) {
  if (total < 60) {
    return t("panel.queryLog.seconds", { value: total });
  }
  const dakika = Math.floor(total / 60);
  const saniye = total % 60;
  return saniye === 0
    ? t("panel.queryLog.minutes", { value: dakika })
    : t("panel.queryLog.minutesSeconds", { minutes: dakika, seconds: saniye });
}

function formatTime(iso: string) {
  const tarih = new Date(iso);
  return Number.isNaN(tarih.getTime()) ? iso : tarih.toLocaleString();
}

function durationClass(seconds: number) {
  if (seconds >= 30) return "danger";
  if (seconds >= 5) return "warn";
  return "ok";
}

function resetFilters() {
  filters.day = "";
  filters.source = "";
  filters.siteName = "";
  filters.target = "";
  filters.minDurationSeconds = 0;
  filters.search = "";
  filters.remoteOnly = false;
  filters.skip = 0;
}

function pageBy(yon: number) {
  filters.skip = Math.max(0, filters.skip + yon * filters.take);
}

async function load() {
  controller?.abort();
  controller = new AbortController();
  loading.value = true;
  error.value = "";

  try {
    page.value = await getQueryHistory(
      {
        // Tek gün seçildiyse aralık o güne kilitlenir; boşsa sunucu saklama penceresinin tamamına bakar.
        from: filters.day || undefined,
        to: filters.day || undefined,
        source: filters.source || undefined,
        siteName: filters.siteName || undefined,
        target: filters.target || undefined,
        minDurationSeconds: filters.minDurationSeconds || undefined,
        search: filters.search || undefined,
        remoteOnly: filters.remoteOnly || undefined,
        skip: filters.skip || undefined,
        take: filters.take
      },
      controller.signal
    );
  } catch (hata: unknown) {
    // İptal edilen istek HATA DEĞİLDİR: filtre hızlı değiştirildiğinde önceki istek iptal olur ve
    // bunu kırmızı bir uyarı olarak göstermek kullanıcıyı boşuna telaşlandırırdı.
    if ((hata as { name?: string })?.name === "CanceledError" || (hata as { name?: string })?.name === "AbortError") {
      return;
    }
    const anahtar = extractApiMessage(hata);
    const cevrilmis = t(anahtar);
    const ayrinti = extractApiDetails(hata);
    error.value = (cevrilmis === anahtar ? anahtar : cevrilmis) + (ayrinti ? `\n\n${ayrinti}` : "");
  } finally {
    loading.value = false;
  }
}

// Filtre değişince sayfa başa döner — aksi halde kullanıcı 5. sayfada boş sonuç görürdü.
watch(
  () => [filters.day, filters.source, filters.siteName, filters.target, filters.minDurationSeconds, filters.search, filters.remoteOnly],
  () => {
    filters.skip = 0;
  }
);

// Metin kutusu her harfte istek atmasın.
watch(
  () => ({ ...filters }),
  () => {
    if (debounce !== null) {
      window.clearTimeout(debounce);
    }
    debounce = window.setTimeout(() => void load(), 250);
  },
  { deep: true }
);

onMounted(() => void load());
onUnmounted(() => {
  controller?.abort();
  if (debounce !== null) {
    window.clearTimeout(debounce);
  }
});
</script>

<style scoped>
.query-history {
  display: grid;
  gap: 12px;
}

.history-scope {
  margin: 0;
}

.history-filters {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  gap: 10px;
  padding: 10px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--surface-0);
}

.history-search {
  flex: 1 1 220px;
}

/* .checkbox-field base.css'te (paylasilan sinif, paylasilan tanim). */

.history-summary h4 {
  margin: 0 0 2px;
}

.history-summary .meta {
  margin: 0 0 8px;
}

.history-count {
  color: var(--text-muted);
  font-size: 0.86rem;
}

/* Sorgu = kod: değişken genişlikli yazıda "l1I" ve "O0" ayırt edilemiyor. */
.mono strong,
.mono small {
  font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
  font-size: 0.8rem;
  font-weight: 500;
  line-height: 1.4;
}

.shape-row {
  grid-template-columns: minmax(0, 2.4fr) 0.5fr 0.8fr minmax(0, 0.9fr);
  align-items: start;
}

.entry-row {
  grid-template-columns: 1fr 0.7fr 1.2fr minmax(0, 2.4fr);
  align-items: start;
}

.remote-badge {
  margin-left: 6px;
  padding: 1px 7px;
  border-radius: 999px;
  background: var(--warn-bg);
  color: var(--warn-text);
  font-size: 0.72rem;
  font-weight: 700;
}

.history-pager {
  justify-content: center;
}
</style>
