<template>
  <section class="consumers" :aria-busy="loading ? 'true' : 'false'">
    <div class="consumers-head">
      <div class="consumers-title">
        <strong>{{ label("title", "Şu an kim kullanıyor?") }}</strong>
        <small class="meta">
          {{ label("subtitle", "{name} sürecinin içinde çalışan işlemler", { name: displayName }) }}
        </small>
      </div>
      <span v-if="snapshot && snapshot.available" class="meta consumers-count">
        {{ label("count", "{n} aktif", { n: snapshot.totalCount }) }}
      </span>
    </div>

    <!-- Elimizde eski bir liste varken bir tur başarısız olduysa listeyi SİLMEYİZ (titreme olmasın),
         ama kullanıcıyı bilgilendiririz: baktığı satırlar bayat olabilir. -->
    <p v-if="failed && snapshot" class="meta consumers-stale">
      {{ label("stale", "Son yenileme başarısız oldu; liste bayat olabilir.") }}
    </p>

    <!-- Henüz hiç veri yok: ya ilk tur sürüyor ya da hata aldık. (Sekme arka plandayken de buraya
         düşeriz — "aktif sorgu yok" DEMEYİZ, çünkü ölçüm hiç yapılmadı; yanlış güven vermemeli.) -->
    <p v-if="!snapshot && failed" class="notice error">{{ label("failed", "Tüketici listesi alınamadı.") }}</p>
    <p v-else-if="!snapshot" class="meta" role="status">{{ label("loading", "Okunuyor…") }}</p>

    <!-- Kurulu değil: bu bir HATA DEĞİL. Sakin, bilgilendirici mesaj. -->
    <p v-else-if="!snapshot.installed" class="field-hint">
      {{ label("notInstalled", "{name} bu sunucuda kurulu değil.", { name: displayName }) }}
    </p>

    <p v-else-if="snapshot.statusCode === 'credentials_missing'" class="field-hint">
      {{ label("credentialsMissing", "{name} kurulu ama panel ona bağlanamıyor: yönetici parolası kayıtlı değil. Veritabanları ekranından kimlik bilgisini kaydedin.", { name: displayName }) }}
    </p>

    <p v-else-if="!snapshot.available" class="notice warn">
      {{ label("unavailable", "{name} şu an sorgulanamadı. Servis durmuş olabilir; liste boş olduğu için değil, okunamadığı için görünmüyor.", { name: displayName }) }}
    </p>

    <!-- Boş durum: sakin bir cümle; "sorun yok" demek burada doğrudur. -->
    <p v-else-if="rows.length === 0" class="meta consumers-empty">
      {{ isWeb
        ? label("emptyWeb", "Şu an işlenen istek yok.")
        : label("emptyDb", "Şu an aktif sorgu yok.") }}
    </p>

    <div v-else class="consumer-table">
      <div :class="['consumer-row', 'head', { 'with-page': showPageColumn }]" aria-hidden="true">
        <span>{{ label("site", "Site") }}</span>
        <span>{{ label("duration", "Süre") }}</span>
        <span>{{ label("state", "Durum") }}</span>
        <span v-if="showPageColumn">{{ label("page", "Sayfa") }}</span>
        <span>{{ isWeb ? label("url", "İstek") : label("query", "Sorgu") }}</span>
      </div>

      <div
        v-for="row in rows"
        :key="row.key"
        :class="['consumer-row', { slow: row.slow, 'with-page': showPageColumn }]">
        <div class="consumer-who">
          <strong :title="row.who">
            {{ row.who }}
            <!-- Aynı sitenin bir veritabanı buradaki uygulamaya, diğeri dışarıdaki bir sunucuya
                 hizmet ediyor olabilir. Bunu ayıran tek gözlem istemci adresidir. -->
            <span v-if="row.remote" class="remote-badge" :title="label('remoteHint', 'Bu bağlantı sunucunun dışından geliyor')">
              {{ label("remote", "dışarıdan") }}
            </span>
          </strong>
          <small class="meta">{{ row.whoDetail }}</small>
        </div>

        <span class="consumer-duration">
          <span v-if="row.slow" class="slow-dot" :title="label('slowHint', '5 saniyeden uzun sürüyor')">!</span>
          {{ row.durationText }}
        </span>

        <span class="consumer-state">{{ row.stateText || "—" }}</span>

        <!-- Muhtemel sayfa: MySQL sorgunun kaynağını bilmez, bu zaman örtüşmesinden çıkarılmış
             bir TAHMİNDİR; kesin olmadığında "~" ile işaretlenir. -->
        <span v-if="showPageColumn" class="consumer-page">
          <template v-if="row.page">
            <span v-if="!row.page.exact" class="page-approx" :title="row.page.all">~</span>
            <code :title="row.page.all || row.page.text">{{ row.page.text }}</code>
          </template>
          <template v-else>—</template>
        </span>

        <!-- Sorgu varsayılan olarak tek satır; teşhis için tamamı gerektiğinde tıklayınca açılır.
             30 satırın hepsini açık göstermek tabloyu okunmaz yapardı. -->
        <div :class="['consumer-statement', { expanded: expandedRows.has(row.key) }]">
          <button
            v-if="row.statement"
            type="button"
            class="statement-toggle"
            :aria-expanded="expandedRows.has(row.key)"
            :title="expandedRows.has(row.key) ? label('collapse', 'Kapat') : label('expand', 'Tamamını göster')"
            @click="toggleRow(row.key)">
            <code>{{ row.statement }}</code>
          </button>
          <span v-else>—</span>

          <div v-if="expandedRows.has(row.key)" class="statement-actions">
            <button type="button" class="secondary compact" @click="copyStatement(row.statement)">
              {{ copiedKey === row.key ? label("copied", "Kopyalandı") : label("copy", "Kopyala") }}
            </button>
          </div>

          <small v-if="row.redacted" class="meta">
            {{ label("redacted", "parola içerdiği için gizlendi") }}
          </small>
        </div>
      </div>
    </div>

    <p v-if="rows.length > 0" class="field-hint consumers-foot">
      {{ isWeb
        ? label("footWeb", "IIS'te o an işlenen istekler. Uzun süren satır asılı kalmış bir isteği gösterir.")
        : label("footDb", "Uykudaki (boşta) bağlantılar listelenmez; yalnız gerçekten çalışan sorgular görünür.") }}
      <template v-if="showPageColumn">
        {{ label("pageHint", "Sayfa sütunu bir TAHMİNDİR: veritabanı, sorgunun hangi sayfadan geldiğini bilmez.") }}
      </template>
    </p>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import {
  getResourceConsumers,
  type ConsumerSource,
  type ResourceConsumerSnapshot
} from "../api/consumers";

/**
 * Paylaşılan bir sürecin İÇİNİ gösteren liste.
 *
 * NEDEN VAR: süreç izleyici "MariaDB %42 CPU" der ve orada durur — MySQL tek süreçtir, tüm siteler
 * onu paylaşır, o yüzden işletim sistemi hangi sitenin sorgusu olduğunu ASLA söyleyemez. Bu bileşen
 * motorun kendi oturum listesini gösterir ve her satırı -mümkünse- bir SİTEYE bağlar. "Site" sütunu
 * bu ekranın asıl değeridir: teknik bilmeyen biri de "alioral.xyz 12 saniyedir sorgu çalıştırıyor"
 * cümlesini okuyabilmeli.
 */
const props = withDefaults(
  defineProps<{
    source: ConsumerSource;
    /** Kaç satır çekilsin (sunucu toplam sayıyı ayrıca bildirir). */
    limit?: number;
    /** Yenileme aralığı (ms). 0 verilirse otomatik yenileme kapanır. */
    intervalMs?: number;
    /** Bu eşiğin üstündeki satırlar dikkat çeker (saniye). */
    slowSeconds?: number;
  }>(),
  { limit: 50, intervalMs: 3000, slowSeconds: 5 }
);

const { t } = useI18n();

const snapshot = ref<ResourceConsumerSnapshot | null>(null);
// Veritabanı görünümünde "muhtemel sayfa" için IIS'in anlık istekleri (yalnız eşleştirme amaçlı).
const webSnapshot = ref<ResourceConsumerSnapshot | null>(null);

// Açılmış sorgu satırları. Anahtar bazlı tutulur ki 3 sn'lik yenileme açık satırı kapatmasın.
// IIS okunamıyorsa "muhtemel sayfa" korelasyonunu bırak: her turda boşuna appcmd çalıştırma.
let webUnavailable = false;

const expandedRows = ref(new Set<string>());
const copiedKey = ref("");
let copiedTimer: number | null = null;

function toggleRow(key: string) {
  const next = new Set(expandedRows.value);
  if (next.has(key)) {
    next.delete(key);
  } else {
    next.add(key);
  }
  expandedRows.value = next;
}

async function copyStatement(statement: string) {
  if (!statement) {
    return;
  }

  try {
    await navigator.clipboard.writeText(statement);
  } catch {
    // Pano izni yoksa (http bağlamı vb.) sessizce geç — kullanıcı metni zaten seçip kopyalayabilir.
    return;
  }

  const key = [...expandedRows.value].find((item) => rows.value.some((row) => row.key === item && row.statement === statement));
  copiedKey.value = key ?? "";
  if (copiedTimer !== null) {
    window.clearTimeout(copiedTimer);
  }
  copiedTimer = window.setTimeout(() => (copiedKey.value = ""), 2000);
}
const loading = ref(false);
const failed = ref(false);

// Aynı anda tek istek: yavaş sunucuda 3 sn'lik zamanlayıcı istekleri ÜST ÜSTE YIĞMASIN.
let inFlight = false;
let timer: number | null = null;
let controller: AbortController | null = null;
// Kaynak değişince yolda olan eski yanıt yeni listeyi ezmesin.
let requestId = 0;

const isWeb = computed(() => props.source === "iis");

// "Sayfa" sütunu yalnız veritabanı görünümünde ve gerçekten eşleşecek istek varken görünür;
// aksi halde her satırı "—" ile dolduran boş bir sütun eklemenin anlamı yok.
const showPageColumn = computed(
  () => !isWeb.value && (webSnapshot.value?.consumers?.length ?? 0) > 0
);

const displayName = computed(() => {
  if (snapshot.value?.displayName) {
    return snapshot.value.displayName;
  }
  const fallbacks: Record<ConsumerSource, string> = {
    mysql: "MySQL",
    mariadb: "MariaDB",
    postgresql: "PostgreSQL",
    iis: "IIS"
  };
  return fallbacks[props.source] ?? props.source;
});

// i18n anahtarları başka bir ajanda eklenecek; anahtar henüz yoksa ham anahtar yerine TR metni göster
// (SiteHealthStrip ile aynı kalıp — ekran anahtarlar gelmeden de okunabilir kalır).
function label(suffix: string, fallback: string, params?: Record<string, unknown>): string {
  const key = `panel.consumers.${suffix}`;
  const translated = params ? t(key, params) : t(key);
  return translated === key ? interpolate(fallback, params) : translated;
}

function interpolate(text: string, params?: Record<string, unknown>): string {
  if (!params) {
    return text;
  }
  return text.replace(/\{(\w+)\}/g, (match, name: string) =>
    name in params ? String(params[name]) : match);
}

function formatDuration(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds <= 0) {
    return label("justStarted", "<1 sn");
  }
  if (seconds < 60) {
    return label("seconds", "{n} sn", { n: seconds < 10 ? seconds.toFixed(1) : Math.round(seconds) });
  }
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) {
    return label("minutes", "{n} dk", { n: minutes });
  }
  return label("hours", "{n} sa", { n: Math.floor(minutes / 60) });
}

/**
 * Bir veritabanı sorgusunun HANGİ SAYFADAN geldiğini MySQL bilmez — sorgu ile HTTP isteği
 * arasında taşınan bir bağ yoktur. Elimizdeki tek dürüst ipucu zaman örtüşmesi: aynı sitenin
 * o an IIS'te işlenen istekleri. Sorgudan SONRA başlamış bir istek onu açmış olamaz, o yüzden
 * yalnız sorgu kadar (veya daha uzun) süredir çalışan istekler aday sayılır.
 * Tek aday varsa adresi gösteririz; birden çoksa sayısını — çünkü hangisi olduğunu BİLEMEYİZ.
 */
function pageHintFor(siteName: string, durationSeconds: number) {
  const requests = webSnapshot.value?.consumers ?? [];
  if (!siteName || requests.length === 0) {
    return null;
  }

  // 0.5 sn tolerans: iki ölçüm aynı anda alınmadığı için sınırdaki adayı elemeyelim.
  const candidates = requests.filter(
    (request) =>
      request.siteName === siteName &&
      request.durationSeconds + 0.5 >= durationSeconds
  );

  if (candidates.length === 0) {
    return null;
  }

  if (candidates.length === 1) {
    return { text: candidates[0].statement || candidates[0].target, exact: true, all: "" };
  }

  return {
    text: label("pageCandidates", "{n} istek", { n: candidates.length }),
    exact: false,
    all: candidates.map((request) => request.statement || request.target).join("\n")
  };
}

const rows = computed(() => {
  const data = snapshot.value;
  if (!data || !data.available) {
    return [];
  }

  return data.consumers.map((item, index) => {
    // Site bilinmiyorsa teknik hedefe (veritabanı / app pool) düşeriz — asla boş satır göstermeyiz.
    const who = item.siteName || item.target || label("unknownSite", "Bilinmeyen");

    // Sitesi bu sunucuda olmayan veritabanları: satır sahipsiz görünmesin. Sahip biliniyorsa
    // müşteri adını, bilinmiyorsa "panelde kayıtlı değil"i yazarız — ikisi FARKLI durumlardır.
    const externalNote = !isWeb.value && !item.siteLinked
      ? item.ownerName
        ? `${label("ownedBy", "müşteri")}: ${item.ownerName} · ${label("siteElsewhere", "site bu sunucuda değil")}`
        : label("unmanagedDb", "panelde kayıtlı değil")
      : "";

    const detailParts = [
      item.siteName && item.target && item.siteName !== item.target ? item.target : "",
      externalNote,
      item.user,
      item.clientHost,
      item.command
    ].filter((part) => Boolean(part && part.trim()));

    return {
      key: `${item.connectionId || "row"}-${index}`,
      who,
      whoDetail: detailParts.join(" · "),
      durationText: formatDuration(item.durationSeconds),
      slow: item.durationSeconds >= props.slowSeconds,
      stateText: item.state,
      statement: item.statement,
      redacted: item.redacted,
      remote: !isWeb.value && item.remoteClient,
      page: isWeb.value ? null : pageHintFor(item.siteName ?? "", item.durationSeconds)
    };
  });
});

async function load() {
  // Sekme arka plandaysa sunucuyu boşuna yorma; önceki istek bitmeden yenisini gönderme.
  if (inFlight || document.hidden) {
    return;
  }

  const current = ++requestId;
  inFlight = true;
  loading.value = true;
  controller = new AbortController();

  try {
    // Veritabanı kaynaklarında IIS'in o an işlediği istekleri de çekiyoruz: MySQL bir sorgunun
    // hangi sayfadan geldiğini BİLMEZ, ama aynı sitenin aynı anda açık olan isteklerini
    // eşleştirerek "muhtemel sayfa" gösterebiliriz (bkz. pageHintFor). Başarısız olursa
    // sessizce geçilir — asıl liste bundan etkilenmez.
    //
    // MASRAF KONTROLÜ: bu ek istek sunucuda her seferinde `appcmd` SÜRECİ başlatır. Üç saniyede bir
    // bunu bedavaya yapmamak için iki koşul: IIS okunamıyorsa bir daha DENEME, ve eşleştirilecek
    // veritabanı satırı yoksa (liste boş) hiç isteme.
    const wantWebCorrelation = !isWeb.value && !webUnavailable && (snapshot.value?.consumers.length ?? 1) > 0;

    const [result, web] = await Promise.all([
      getResourceConsumers(props.source, props.limit, controller.signal),
      wantWebCorrelation
        ? getResourceConsumers("iis", props.limit, controller.signal).catch(() => null)
        : Promise.resolve(null)
    ]);

    if (current !== requestId) {
      return;
    }

    if (wantWebCorrelation) {
      // Kurulu değil / okunamıyor → bir daha sorma (kaynak değişince sıfırlanır).
      webUnavailable = web !== null && (!web.installed || !web.available);
      webSnapshot.value = webUnavailable ? null : web;
    }

    snapshot.value = result;
    failed.value = false;
  } catch {
    // Ağ/yetki hatası: son iyi listeyi silmeyiz (titreme olmasın), yalnız uyarı satırı çıkarırız.
    if (current === requestId) {
      failed.value = true;
    }
  } finally {
    if (current === requestId) {
      loading.value = false;
    }
    inFlight = false;
  }
}

function startTimer() {
  stopTimer();
  if (props.intervalMs > 0) {
    timer = window.setInterval(() => void load(), props.intervalMs);
  }
}

function stopTimer() {
  if (timer !== null) {
    window.clearInterval(timer);
    timer = null;
  }
}

// Sekme geri gelince beklemeden tazele (aksi halde kullanıcı bayat listeye bakar).
function onVisibilityChange() {
  if (!document.hidden) {
    void load();
  }
}

watch(
  () => props.source,
  () => {
    // Kaynak değişti: eski veriyi hemen bırak, uçuşan isteği iptal et.
    controller?.abort();
    requestId += 1;
    inFlight = false;
    snapshot.value = null;
    webSnapshot.value = null;
    // IIS "okunamıyor" damgasını sıfırla: kullanıcı bu arada IIS'i düzeltmiş olabilir.
    webUnavailable = false;
    expandedRows.value = new Set();
    failed.value = false;
    void load();
  }
);

onMounted(() => {
  void load();
  startTimer();
  document.addEventListener("visibilitychange", onVisibilityChange);
});

onUnmounted(() => {
  stopTimer();
  document.removeEventListener("visibilitychange", onVisibilityChange);
  controller?.abort();
});

// Üst ekran isterse elle tazeleyebilsin (ör. "Yenile" düğmesi).
defineExpose({ refresh: load });
</script>

<style scoped>
.consumers {
  display: grid;
  gap: 10px;
  min-width: 0;
}

.consumers-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 10px;
  flex-wrap: wrap;
}

.consumers-title {
  display: grid;
  gap: 2px;
  min-width: 0;
}

.consumers-count {
  white-space: nowrap;
  font-size: 0.78rem;
}

.consumers-empty {
  padding: 10px 2px;
}

.consumers-stale {
  color: var(--warn-text);
  font-size: 0.76rem;
}

/* Sütunlar: kim · süre · durum · sorgu. Sorgu sütunu esner, diğerleri sabit kalır. */
.consumer-table {
  display: grid;
  gap: 2px;
  min-width: 0;
}

.remote-badge {
  margin-left: 6px;
  padding: 1px 6px;
  border-radius: 999px;
  font-size: 0.68rem;
  font-weight: 600;
  color: var(--text-muted);
  border: 1px solid var(--panel-border);
  white-space: nowrap;
}

/* Kapalıyken tek satır, açıkken tam metin: satır sonları korunur, uzun sorgu sarılır. */
.statement-toggle {
  display: block;
  width: 100%;
  padding: 0;
  border: 0;
  background: none;
  text-align: left;
  cursor: pointer;
  color: inherit;
  min-width: 0;
}

.statement-toggle code {
  display: block;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 0.78rem;
}

.consumer-statement.expanded .statement-toggle code {
  overflow: visible;
  text-overflow: clip;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  max-height: 40vh;
  overflow-y: auto;
  padding: 6px 8px;
  border: 1px solid var(--panel-border);
  border-radius: 8px;
  background: var(--surface-1);
  user-select: text;
}

.statement-actions {
  margin-top: 6px;
}

.consumer-row.with-page {
  grid-template-columns: minmax(110px, 1.1fr) 72px minmax(80px, 0.7fr) minmax(0, 1.3fr) minmax(0, 2fr);
}

.consumer-page {
  display: flex;
  align-items: center;
  gap: 5px;
  min-width: 0;
}

.consumer-page code {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 0.78rem;
}

/* Kesin olmayan eşleşme işareti: kullanıcı bunun tahmin olduğunu görmeli. */
.page-approx {
  flex-shrink: 0;
  color: var(--text-muted);
  font-weight: 700;
  cursor: help;
}

.consumer-row {
  display: grid;
  grid-template-columns: minmax(120px, 1.2fr) 76px minmax(90px, 0.8fr) minmax(0, 2.4fr);
  align-items: center;
  gap: 10px;
  padding: 7px 9px;
  border: 1px solid transparent;
  border-radius: 9px;
  min-width: 0;
}

/* Zebra: başlık satırı 1. çocuktur, veri satırları 2'den başlar → tek/çift doğal olarak dönüşümlü.
   ("nth-child(... of S)" bilinçli KULLANILMADI; eski tarayıcılarda desteklenmiyor. */
.consumer-row:not(.head):nth-child(odd) {
  background: var(--surface-1);
}

.consumer-row.head {
  color: var(--text-muted);
  font-size: 0.72rem;
  font-weight: 700;
  letter-spacing: 0.02em;
  text-transform: uppercase;
}

/* Uzun süren satır göze batsın: asıl aranan "kim asılı kalmış" satırı budur. */
.consumer-row.slow {
  background: var(--warn-bg);
  border-color: color-mix(in srgb, var(--warn-1) 35%, transparent);
}

.consumer-who {
  display: grid;
  gap: 1px;
  min-width: 0;
}

.consumer-who strong {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 0.86rem;
}

.consumer-who small {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 0.72rem;
}

.consumer-duration {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  font-variant-numeric: tabular-nums;
  font-size: 0.82rem;
  font-weight: 700;
  white-space: nowrap;
}

.slow-dot {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 15px;
  height: 15px;
  border-radius: 999px;
  background: var(--warn-1);
  color: #1b1300;
  font-size: 0.68rem;
  font-weight: 800;
}

.consumer-state {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--text-muted);
  font-size: 0.78rem;
}

/* Sorgu metni ASLA taşmaz: tek satır, kırpılır; tamamı title ipucunda. */
.consumer-statement {
  display: grid;
  gap: 1px;
  min-width: 0;
}

.consumer-statement code {
  display: block;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 0.75rem;
  line-height: 1.45;
}

.consumer-statement small {
  font-size: 0.68rem;
}

.consumers-foot {
  margin: 0;
}

/* Dar ekran: tablo satırı kart olur, başlık şeridi gizlenir. */
@media (max-width: 720px) {
  .consumer-row {
    grid-template-columns: 1fr auto;
    gap: 4px 8px;
    padding: 9px;
    border: 1px solid var(--panel-border);
    border-radius: 10px;
  }

  .consumer-row.head {
    display: none;
  }

  .consumer-row:not(.head):nth-child(odd) {
    background: transparent;
  }

  .consumer-who {
    grid-column: 1;
  }

  .consumer-duration {
    grid-column: 2;
    justify-self: end;
  }

  .consumer-state,
  .consumer-statement {
    grid-column: 1 / -1;
  }
}
</style>
