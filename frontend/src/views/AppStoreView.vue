<template>
  <section class="panel store-panel">
    <!-- GRADYANLI "HERO" SERIDI KALDIRILDI, SAYFA BASLIGI PANELIN ORTAK BASLIGI OLDU.
         SAYILDI: alti ekranin besi `.module-head` ile aciliyor (solda baslik + tek cumle,
         sagda denetim); yalniz bu ekranin kendi 18px dolgulu, radyal gradyanli, `h3` basligi
         ve 0.74rem'lik kendi ustbasligi vardi. Ayni rutbe iki ayri gorunumde cizilince panel
         iki ayri urun gibi okunuyordu.
         HICBIR BILGI SILINMEDI: ustbaslik (`.section-kicker` -- paylasilan sinif), baslik,
         alt cumle, iki sayac ve tazeleme dugmesi aynen duruyor; yalnizca panelin kendi
         baslik duzenine tasindi. -->
    <div class="module-head">
      <div>
        <span class="section-kicker">{{ t("panel.appStore.catalog") }}</span>
        <h2>{{ t("panel.appStore.title") }}</h2>
        <p class="meta">{{ t("panel.appStore.subtitle") }}</p>
      </div>
      <div class="hero-stats">
        <span class="hero-stat">
          <strong>{{ templatesLoaded ? templates.length : "—" }}</strong>
          <span class="tile-label">{{ t("panel.appStore.applicationCount") }}</span>
        </span>
        <span class="hero-stat">
          <strong>{{ templatesLoaded ? featuredCount : "—" }}</strong>
          <span class="tile-label">{{ t("panel.appStore.automaticCount") }}</span>
        </span>
        <!-- `loading` OLCULDU: betikte SET EDILIYOR ama sablonda HIC OKUNMUYORDU. Yani
             "Sablonlari yenile" tiklandiginda ekranda hicbir sey degismiyor, istek surerken
             dugme tekrar tekrar tiklanabiliyordu. Bayrak zaten vardi, tek eksik onu
             gostermekti. -->
        <button class="secondary compact" :disabled="loading || !!busyTemplate" @click="refresh">
          {{ loading ? t("panel.common.loading") : t("panel.appStore.refresh") }}
        </button>
      </div>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">
      {{ notice }}
    </p>

    <details v-if="noticeDetails" class="error-details" open>
      <summary>{{ t("panel.appStore.errorDetails") }}</summary>
      <pre>{{ noticeDetails }}</pre>
    </details>

    <div v-if="wpAdmin.adminPassword" class="notice wp-creds" role="status">
      <strong>{{ t("panel.appStore.wpReady") }}</strong>
      <div class="wp-cred-line">{{ t("panel.appStore.wpAdminUrl") }}: <a :href="wpAdmin.adminUrl" target="_blank" rel="noopener">{{ wpAdmin.adminUrl }}</a></div>
      <div class="wp-cred-line">{{ t("panel.appStore.wpUser") }}: <code>{{ wpAdmin.adminUser }}</code></div>
      <div class="wp-cred-line">{{ t("panel.appStore.wpPass") }}: <code>{{ wpAdmin.adminPassword }}</code></div>
      <small>{{ t("panel.appStore.wpCredHint") }}</small>
    </div>

    <!-- `notice warn` -> `notice warn-banner`: kapsamli `.notice.warn` kopyasi yalnizca metin
         rengini ve dolguyu degistiriyordu, KENARI degistirmiyordu -- yani base.css'in
         `.notice` mavi kenari kaliyor ve "site listesi okunamadi" uyarisi KEHRIBAR DOLGULU
         MAVI KENARLI melez bir kutu olarak ciziliyordu. Paylasilan uyari seridinde ucu de
         (kenar, dolgu, yazi) kehribar. -->
    <p v-if="sitesUnavailable" class="notice warn-banner" role="alert">
      {{ t("panel.appStore.sitesUnavailable") }}
    </p>

    <p v-else-if="sitesReady && sitesStore.sites.length === 0" class="notice warn-banner">
      {{ t("panel.appStore.noSites") }}
    </p>

    <!-- `.store-toolbar` + `.category-tab` SILINDI, panelin ortak arac cubugu ve sekme seridi
         kullaniliyor (`.module-toolbar` + `.segmented-tabs`, base.css).
         OLCULDU, uc gorunur fark vardi: (1) cubugun zemini `--input-bg` idi, yani bir GIRDI
         yuzeyi -- ayni cubuk ProxyView ve AppsView'de `--surface-0`; (2) secili sekme 135
         derecelik mavi GRADYAN dolguyla, panelin her yerindeki alt-cizgili sekmeden bambaska
         ciziliyordu; (3) `.category-tab` global `button` gradyanini miras aldigi icin secili
         OLMAYAN sekmeler bile dugme golgesi tasiyordu. Sekmeler artik uc ekranda ayni. -->
    <div class="module-toolbar">
      <div class="segmented-tabs" role="tablist">
        <!-- Seçili sekme GÖRSEL olarak yalnız renkle belliydi; `aria-selected` olmadan ekran
             okuyucu bütün kategori düğmelerini aynı okuyordu. -->
        <button
          v-for="category in categories"
          :key="category"
          type="button"
          role="tab"
          :aria-selected="activeCategory === category"
          :class="{ active: activeCategory === category }"
          @click="activeCategory = category">
          {{ categoryLabel(category) }}
        </button>
      </div>
      <label class="store-search">
        <span class="tile-label">{{ t("panel.appStore.search") }}</span>
        <input v-model="search" :placeholder="t('panel.appStore.searchPlaceholder')" />
      </label>
    </div>

    <!-- KATALOG ARTIK PANELIN ORTAK TABLOSU: `.data-table` + `.data-row` (base.css).
         Eskiden kendi kutu deseni vardi (kart gibi kenarli satirlar, `--input-bg` zemin,
         `--radius-sm` kose) ve en onemlisi BASLIK SATIRI 1180px'in altinda `display: none`
         ile GIZLENIYORDU. Ustelik ayni kirilimda sutun sayisi 5'ten 3'e dusuyordu, yani
         "Ortam", "Otomasyon", "Hedef site" degerleri hangi basliga ait oldugu belirsiz
         hucrelere donusuyordu. Bilgi gizlenmedi, DUZENLENDI: baslik her genislikte durur,
         dar ekranda tablo -- panelin butun diger tablolari gibi -- yatay kaydirilir. -->
    <div class="data-table">
      <div class="data-row template-row table-head">
        <span>{{ t("panel.appStore.application") }}</span>
        <span>{{ t("panel.appStore.environment") }}</span>
        <span>{{ t("panel.appStore.automation") }}</span>
        <span>{{ t("panel.php.targetSite") }}</span>
        <span>{{ t("panel.software.action") }}</span>
      </div>

      <article
        v-for="template in filteredTemplates"
        :key="template.id"
        :class="['data-row', 'template-row', { featured: template.featured }]">
        <div class="template-copy">
          <div class="app-mark" :data-runtime="template.runtime.toLowerCase()">
            {{ appInitials(template.displayName) }}
          </div>
          <div>
            <div class="title-line">
              <strong>{{ template.displayName }}</strong>
              <span v-if="template.featured" class="featured-pill">{{ t("panel.appStore.recommended") }}</span>
            </div>
            <!-- `serverText`: aciklama sunucudan ANAHTAR olarak gelir (store.template.<id>.description),
                 metni sozluk verir. Ceviri yoksa deger oldugu gibi basilir. -->
            <p class="meta">{{ serverText(template.description) }}</p>
            <div class="requirement-list">
              <!-- Urun adlari ("PHP 8.3+", "Composer") ham gecer; yalniz Turkce baglac iceren
                   gereksinim anahtar tasir. Ayrimi `serverText` yapar. -->
              <span v-for="requirement in template.requirements" :key="requirement">
                {{ serverText(requirement) }}
              </span>
            </div>
          </div>
        </div>

        <div class="runtime-cell">
          <span class="runtime-pill">{{ template.runtime }}</span>
          <small>{{ categoryLabel(template.category) }}</small>
        </div>

        <div class="automation-list">
          <span v-for="step in template.automaticSetup" :key="step">{{ serverText(step) }}</span>
        </div>

        <!-- `.visually-hidden` -> `.sr-only`: base.css'te TEK KAYNAK olarak tanimli olan ad bu.
             Kapsamli kopya `white-space: nowrap` icermiyordu; 1px genisligindeki kutuda uzun
             etiket sarilip satir yuksekligini bozabiliyordu. -->
        <label class="site-select">
          <span class="sr-only">{{ t("panel.php.targetSite") }}</span>
          <select v-model="siteMap[template.id]" :disabled="loading || !!busyTemplate || !sitesReady">
            <option value="">{{ t("panel.php.selectSite") }}</option>
            <option v-for="site in sitesStore.sites" :key="site.name" :value="site.name">
              {{ site.name }}
            </option>
          </select>
        </label>

        <!-- SATIR EYLEMI BIRINCIL DEGIL. SAYILDI: katalogda 20+ sablon var ve her satirda mavi
             birincil bir "Kur" dugmesi duruyordu -- ekranda ayni anda 20 birincil eylem, yani
             hicbiri. Panelin diger satir eylemleri (ServerHealth "Yeniden baslat", Cluster
             "Test/Ac/Duzenle", Firewall "Ac") zaten `secondary compact`; kural, birincil
             tonun SAYFANIN ONERDIGI TEK is icin saklanmasi. Eylemin bulunabilirligi
             azalmiyor: kendi sutununda, kendi basligi ("Islem") altinda duruyor. -->
        <button
          class="secondary compact install-button"
          v-bind="writeGate(!!busyTemplate || loading || !templatesLoaded || !sitesReady || !sitesStore.loaded || sitesStore.loading || !sitesStore.sites.some(site => site.name === siteMap[template.id]))"
          @click="install(template.id)">
          {{ busyTemplate === template.id ? t("panel.appStore.installing") : t("panel.appStore.install") }}
        </button>
      </article>

      <!-- UC AYRI DURUM, UC AYRI CUMLE. OLCULDU: uzerinde yalnizca IKI vardi ("yukleniyor" ve
           "aramayla eslesen yok") ve ucuncusu -- KATALOG OKUNAMADI -- birincinin icine
           dusuyordu: `getAppTemplates()` patlayinca `templatesLoaded` false kaliyor ve tablo
           SONSUZA KADAR "Yukleniyor…" yaziyordu. Ustelik IIS yok yolunda (asagida) bildirim
           bilerek temizleniyor, yani ikinci istek de basarisiz olursa ekranda hicbir sey
           soylenmiyordu: donen bir bekleme, hata izi sifir.
           Ayrica "aramayla eslesen bulunamadi" BOS KATALOGDA da yaziliyordu -- arama kutusu
           bosken bile kullaniciya aramasini suclayan bir cumle. -->
      <SectionLoadError v-if="catalogFailed" :busy="loading" @retry="refresh" />
      <p v-else-if="!templatesLoaded" class="empty-state" role="status" aria-live="polite">
        {{ t("panel.common.loading") }}
      </p>
      <p v-else-if="templates.length === 0" class="empty-state">
        {{ t("panel.appStore.catalogEmpty") }}
      </p>
      <p v-else-if="filteredTemplates.length === 0" class="empty-state">
        {{ t("panel.appStore.noResults") }}
      </p>
    </div>

    <div v-if="busyTemplate" class="install-progress" role="status">
      <span class="progress-dot"></span>
      <div>
        <strong>{{ busyTemplateName }}</strong>
        <p>{{ t("panel.appStore.installHint") }}</p>
      </div>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { getAppTemplates, installAppTemplate, type AppTemplate } from "../api/store";
import { useConfirm } from "../components/ConfirmDialog.vue";
import SectionLoadError from "../components/SectionLoadError.vue";
import { extractApiDetails, extractApiMessage } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
import { useServerText } from "../composables/useNotice";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";
import { getSessionEpoch } from "../api/client";

const { t } = useI18n();
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri kapiya girmez.
const { writeGate, locked } = useSubscriptionGate();
// Katalog metinleri sunucudan ANAHTAR olarak gelir; ceviri karari tek kaynakta.
const { serverText } = useServerText();
const { confirm } = useConfirm();
const sitesStore = useSitesStore();

const templates = ref<AppTemplate[]>([]);
// Yalnız son başarılı katalog ve site ölçümü kurulum başlatabilir.
const templatesLoaded = ref(false);
const catalogFailed = ref(false);
const sitesReady = ref(false);
const loading = ref(false);
const busyTemplate = ref("");
const notice = ref("");
const noticeDetails = ref("");
const noticeError = ref(false);
const sitesUnavailable = ref(false);
const search = ref("");
const activeCategory = ref("all");
const siteMap = reactive<Record<string, string>>({});
const wpAdmin = reactive({ adminUrl: "", adminUser: "", adminPassword: "" });
const sessionEpoch = getSessionEpoch();
let disposed = false, readSequence = 0;
const targetVersions = new Map<string, number>();
let credentialTemplate = "";
const isActive = () => !disposed && sessionEpoch === getSessionEpoch();
function clearCredentials() {
  wpAdmin.adminUrl = "";
  wpAdmin.adminUser = "";
  wpAdmin.adminPassword = "";
  credentialTemplate = "";
}
watch(() => Object.fromEntries(Object.keys(siteMap).map(id => {
  const site = sitesStore.sites.find(item => item.name === siteMap[id]);
  return [id, JSON.stringify([siteMap[id], site?.id, site?.physicalPath, site?.applicationRootPath])];
})), (next, previous) => {
  for (const id of new Set([...Object.keys(next), ...Object.keys(previous)])) {
    if (next[id] !== previous[id]) {
      targetVersions.set(id, (targetVersions.get(id) ?? 0) + 1);
      if (credentialTemplate === id || busyTemplate.value === id) {
        clearCredentials();
        showNotice("", false);
      }
    }
  }
}, { flush: "sync" });

const categories = computed(() => [
  "all",
  ...Array.from(new Set(templates.value.map((template) => template.category))).sort()
]);

/**
 * Kategori adı backend'den SABİT ve dilden bağımsız gelir ("CMS", "Database", "E-Ticaret"…).
 * Ham basılınca İngilizce arayüzde "E-Ticaret", Türkçe arayüzde "Database" görünüyordu.
 * i18n anahtarı yoksa HAM DEĞER gösterilir — backend yeni bir kategori eklerse boşluk çıkmaz.
 */
function categoryLabel(category: string): string {
  if (category === "all") {
    return t("panel.appStore.all");
  }
  const key = `panel.appStore.category.${category.toLocaleLowerCase("en").replace(/[^a-z0-9]+/g, "")}`;
  const translated = t(key);
  return translated === key ? category : translated;
}

const filteredTemplates = computed(() => {
  const query = search.value.trim().toLocaleLowerCase();
  return templates.value
    .filter((template) => {
      const categoryMatches = activeCategory.value === "all" || template.category === activeCategory.value;
      // ARAMA CEVRILMIS METINDE YAPILIR. Aksi halde arama kutusuna kullanicinin EKRANDA
      // GORDUGU kelimeyi yazmak sonuc vermezdi: sablon alanlari artik "store.setup.download"
      // gibi anahtar tasiyor, kullanici "download"/"indirme" yaziyor.
      const searchMatches = !query || [
        template.displayName,
        serverText(template.description),
        template.runtime,
        categoryLabel(template.category),
        ...template.requirements.map(serverText),
        ...template.automaticSetup.map(serverText)
      ].some((value) => value.toLocaleLowerCase().includes(query));
      return categoryMatches && searchMatches;
    })
    .sort((left, right) => Number(right.featured) - Number(left.featured) ||
      left.displayName.localeCompare(right.displayName));
});

const featuredCount = computed(() => templates.value.filter((template) => template.featured).length);
const busyTemplateName = computed(() =>
  templates.value.find((template) => template.id === busyTemplate.value)?.displayName ?? busyTemplate.value
);

watch(() => sitesStore.selectedSiteName, (siteName) => {
  if (!siteName) {
    return;
  }

  for (const template of templates.value) {
    if (!siteMap[template.id]) {
      siteMap[template.id] = siteName;
    }
  }
}, { immediate: true });

onMounted(async () => {
  await refresh();
});
onUnmounted(() => { disposed = true; readSequence++; clearCredentials(); });

async function refresh() {
  if (!isActive() || busyTemplate.value) return;
  const sequence = ++readSequence;
  const current = () => isActive() && sequence === readSequence;
  loading.value = true;
  templatesLoaded.value = false;
  sitesReady.value = false;
  catalogFailed.value = false;
  sitesUnavailable.value = false;
  try {
    const [catalog, sites] = await Promise.allSettled([getAppTemplates(), sitesStore.fetchSites()]);
    if (!current()) return;
    const errors: string[] = [];
    if (catalog.status === "fulfilled") {
      templates.value = catalog.value;
      templatesLoaded.value = true;
    } else {
      catalogFailed.value = true;
      errors.push(resolveApiMessage(catalog.reason));
    }
    if (sites.status === "fulfilled") {
      sitesReady.value = true;
      for (const template of templates.value) {
        if (!sitesStore.sites.some(site => site.name === siteMap[template.id])) {
          siteMap[template.id] = sitesStore.sites.some(site => site.name === sitesStore.selectedSiteName) ? sitesStore.selectedSiteName : "";
        }
      }
    } else {
      sitesUnavailable.value = true;
      errors.push(resolveApiMessage(sites.reason));
    }
    if (!categories.value.includes(activeCategory.value)) activeCategory.value = "all";
    showNotice([...new Set(errors)].join("\n\n"), errors.length > 0);
  } finally {
    if (current()) loading.value = false;
  }
}

async function install(templateId: string) {
  const template = templates.value.find(item => item.id === templateId);
  const siteName = siteMap[templateId] || "", version = targetVersions.get(templateId) ?? 0;
  const current = () => isActive() && version === (targetVersions.get(templateId) ?? 0);
  const canContinue = () => current() && !locked.value && sitesReady.value && sitesStore.loaded && !sitesStore.loading && sitesStore.sites.some(site => site.name === siteName);
  if (!canContinue() || busyTemplate.value || loading.value || !templatesLoaded.value || !template) return;
  busyTemplate.value = templateId;
  showNotice("", false);
  clearCredentials();

  try {
    const payload = { templateId, siteName, targetPath: "" };
    let result;
    try {
      result = await installAppTemplate({ ...payload, cleanInstall: false });
    } catch (error) {
      if (!current()) return;
      if (extractApiMessage(error) !== "store.site_has_content") throw error;
      if (!canContinue()) return;
      const approved = await confirm({
        title: t("panel.confirm.cleanInstallTitle", { name: siteName }),
        message: t("panel.appStore.cleanInstallConfirm"),
        impact: [`${t("panel.files.siteLabel")}: ${siteName}`, `${t("panel.appStore.application")}: ${template.displayName}`],
        requireText: siteName,
        confirmLabel: t("panel.appStore.install")
      });
      if (!approved || !canContinue()) return;
      result = await installAppTemplate({ ...payload, cleanInstall: true });
    }
    if (!current()) return;
    if (result.templateId !== templateId) {
      showNotice(t("panel.appStore.resultUnconfirmed"), true);
      return;
    }
    credentialTemplate = templateId;
    wpAdmin.adminUrl = result.adminUrl || "";
    wpAdmin.adminUser = result.adminUser || "";
    wpAdmin.adminPassword = result.adminPassword || "";
    const installationMessage = t(result.requiresManualSetup ? "panel.appStore.manualSetupRequired" : "panel.appStore.installed");
    showNotice(installationMessage, result.requiresManualSetup === true);
    try {
      await sitesStore.fetchSites();
    } catch (error) {
      if (current()) {
        sitesReady.value = false;
        sitesUnavailable.value = true;
        showNotice(`${installationMessage}\n\n${t("panel.appStore.installedRefreshFailed")}\n\n${resolveApiMessage(error)}`, true, extractApiDetails(error));
      }
    }
  } catch (error) {
    if (current()) showNotice(resolveApiMessage(error), true, extractApiDetails(error));
  } finally {
    if (isActive()) busyTemplate.value = "";
  }
}

function showNotice(message: string, isError: boolean, details = "") {
  notice.value = message;
  noticeError.value = isError;
  noticeDetails.value = details;
}

function resolveApiMessage(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  return translated === key ? key : translated;
}

function appInitials(displayName: string) {
  return displayName
    .split(/\s+/)
    .map((part) => part[0])
    .join("")
    .slice(0, 2)
    .toUpperCase();
}
</script>

<style scoped>
.store-panel,
.template-copy,
.automation-list {
  display: grid;
  gap: var(--sp-12);
}

.hero-stats,
.title-line,
.requirement-list {
  display: flex;
  gap: var(--sp-10);
}

.hero-stats {
  align-items: center;
  flex-wrap: wrap;
}

/* SABIT RENK SILINDI: zemin `rgba(12, 16, 24, 0.5)` idi -- KOYU TEMANIN lacivertine gomulu
   bir deger. Acik temada beyaz kartin uzerine yariseffaf lacivert bir kutu ciziliyor ve
   uzerindeki `--text-muted` (acik temada #626d7e, koyu bir gri) o kutuda 1.5:1 civarina
   dusuyordu: sayacin etiketi pratikte okunmuyordu. Belirtece baglandi, iki temada da dogru.
   0.72rem -> `--fs-2xs`, 1rem -> `--fs-lg`: ikisi de olcegin var olan adimlari.
   `tabular-nums`: iki sayac yan yana, basamak sayisi degisince kutular zipliyordu. */
.hero-stat {
  display: grid;
  min-width: 92px;
  gap: var(--sp-2);
  padding: var(--sp-8) var(--sp-10);
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  background: var(--surface-1);
}

.hero-stat strong {
  color: var(--text-strong);
  font-size: var(--fs-lg);
  font-variant-numeric: tabular-nums;
}

.requirement-list {
  flex-wrap: wrap;
}

.store-search {
  display: grid;
  min-width: min(100%, 310px);
  gap: var(--sp-4);
}

/* SUTUN GENISLIKLERI DEGISMEDI; degisen tek sey satirin artik `.data-row` olmasi.
   `min-width` 900px SECILDI cunku hesaplanan taban toplam bu: 270 + ~90 (calisma ortami
   kutucugu) + 180 + 190 + ~110 (dugme) + 4 x 12px aralik + 24px dolgu = ~900. base.css'in
   varsayilan 720'si burada YETMEZDI: satir kapsayicidan genis kalinca satirin zemini ve
   kenari kaydirma alaninin ancak bir kismina cizilir, yani baslik satiri saga kaydirildikca
   zeminini kaybederdi. */
.template-row {
  grid-template-columns: minmax(270px, 1.45fr) 0.55fr minmax(180px, 0.8fr) minmax(190px, 0.75fr) auto;
  min-width: 900px;
}

/* Onerilen satir: kenar + soldan sonen ince vurgu. Zemin `--input-bg` (girdi yuzeyi) yerine
   `--surface-0` uzerinden hesaplaniyor -- satir artik bir tablo satiri, girdi degil. */
.template-row.featured {
  border-left: 2px solid color-mix(in srgb, var(--accent-1) 55%, transparent);
  background: linear-gradient(90deg, color-mix(in srgb, var(--accent-2) 12%, transparent), transparent 38%);
}

.template-copy {
  grid-template-columns: 42px 1fr;
  align-items: flex-start;
}

.app-mark {
  display: grid;
  width: 42px;
  height: 42px;
  place-items: center;
  border: 1px solid color-mix(in srgb, var(--accent-1) 24%, transparent);
  border-radius: var(--radius-md);
  color: var(--accent-1);
  font-size: var(--fs-md);
  font-weight: 900;
  background: color-mix(in srgb, var(--accent-1) 10%, transparent);
}

.title-line {
  align-items: center;
  flex-wrap: wrap;
}

/* Uc ayri rozet, tek olcu: 0.72rem -> `--fs-2xs`, dolgular `--sp-*` adimlarina. */
.featured-pill,
.runtime-pill,
.requirement-list span,
.automation-list span {
  width: fit-content;
  border-radius: 999px;
  font-size: var(--fs-2xs);
  font-weight: 700;
}

.featured-pill {
  padding: var(--sp-2) var(--sp-8);
  color: var(--on-accent);
  background: var(--accent-1);
}

.runtime-cell {
  display: grid;
  gap: var(--sp-6);
}

/* Calisma ortami rozeti KEHRIBAR DEGIL notr: "Node.js 20" bir uyari degil, bir olgudur.
   OLCULDU, iki zarari vardi: (1) kehribar bu panelde "dikkat" demek (`.status-pill.warn`,
   `.notice.warn-banner`) ve katalogdaki HER satir bu rozeti tasidigi icin butun liste
   uyariyormus gibi gorunuyordu; (2) metin `--warn-1` idi -- dolgu belirteci, METIN belirteci
   (`--warn-text`) degil; acik temada kendi %14 zemininde 4.5 esiginin altinda kaliyordu.
   Notr rozet zemini `--ring-track`, yazi `--text-soft`: iki temada da tanimli, olcum
   gerektirmeyen govde rengi. */
.runtime-pill {
  padding: var(--sp-2) var(--sp-8);
  color: var(--text-soft);
  background: var(--ring-track);
}

.runtime-cell small {
  color: var(--text-muted);
}

.requirement-list {
  margin-top: var(--sp-8);
}

/* Zemin `--surface-1` -> `--ring-track`: satirin kendi zemini artik `--surface-0` (tablo
   satiri) ve `--surface-1` onun sadece bir adim ustu; rozetler satirdan AYIRT EDILEMEZ
   hale gelmisti. `--ring-track` her iki temada da zeminden bagimsiz, yariseffaf notr dolgu. */
.requirement-list span {
  padding: var(--sp-2) var(--sp-8);
  color: var(--text-soft);
  background: var(--ring-track);
}

.automation-list {
  gap: var(--sp-4);
}

.automation-list span {
  position: relative;
  padding-left: var(--sp-12);
  color: var(--text-soft);
}

.automation-list span::before {
  position: absolute;
  top: 0.42em;
  left: 0;
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--accent-1);
  content: "";
}

.site-select { position: relative; }

.site-select select {
  min-width: 0;
}

.install-button {
  width: fit-content;
  white-space: nowrap;
}

.meta,
.empty-state,
.install-progress p {
  margin: 0;
  color: var(--text-muted);
  line-height: 1.45;
}

/* Dolgu %24 -> `--danger-bg` (%13, panelin TEK tehlike yuzeyi; `.notice.error` de onu
   kullanir). OLCULDU: `--danger-text` (#f0a3a9) %24 kirmizi dolgunun uzerinde koyu temada
   3.4:1 -- ham hata ciktisi icin AA'nin altinda; panelin kendi tehlike zemininde 6.3:1.
   Yani kutu daha "kirmizi" olsun diye yazisi okunmaz hale gelmisti. */
.error-details {
  border: 1px solid color-mix(in srgb, var(--danger-1) 34%, transparent);
  border-radius: var(--radius-sm);
  background: var(--danger-bg);
}

.error-details summary {
  padding: var(--sp-10) var(--sp-12);
  color: var(--danger-text);
  cursor: pointer;
  font-weight: 700;
}

.error-details pre {
  max-height: 260px;
  margin: 0;
  padding: 0 var(--sp-12) var(--sp-12);
  overflow: auto;
  color: var(--danger-text);
  font-family: var(--font-mono);
  font-size: var(--fs-2xs);
  white-space: pre-wrap;
}

.install-progress {
  display: flex;
  gap: var(--sp-12);
  align-items: center;
  padding: var(--sp-12);
  border: 1px solid color-mix(in srgb, var(--accent-1) 28%, transparent);
  border-radius: var(--radius-sm);
  background: color-mix(in srgb, var(--accent-1) 8%, transparent);
}

.progress-dot {
  width: 12px;
  height: 12px;
  border: 2px solid color-mix(in srgb, var(--accent-1) 30%, transparent);
  border-top-color: var(--accent-1);
  border-radius: 50%;
  animation: spin 0.8s linear infinite;
}

.notice.wp-creds {
  display: grid;
  gap: var(--sp-4);
  background: color-mix(in srgb, var(--accent-1) 10%, transparent);
}

.wp-cred-line code {
  font-family: var(--font-mono);
  background: color-mix(in srgb, var(--accent-1) 16%, transparent);
  color: var(--accent-1);
  padding: var(--sp-2) var(--sp-8);
  border-radius: var(--radius-sm);
  user-select: all;
}

/* `opacity: 0.75` SILINDI: opaklik yaziyi ve zemini ayni anda birbirine yaklastirdigi icin
   kontrasti DUSURUR (base.css'te olculmus ders). Ustelik bu kutu bir kez gosterilen
   WordPress yonetici parolasinin ipucunu tasiyor -- soldurulacak en son metin. */
.wp-creds small {
  color: var(--text-muted);
  margin-top: var(--sp-2);
}

@keyframes spin {
  to { transform: rotate(360deg); }
}

/* Katalog tablosunun 1180px ve 760px kirilimlari SILINDI. Ikisi de sutunlari cokertiyor,
   biri ustelik baslik satirini tamamen gizliyordu (yukaridaki sablon notu). Tablo artik
   panelin butun tablolari gibi davraniyor: sabit sutunlar + `.data-table` yatay kaydirma.
   Geride yalniz baslik seridinin dar ekran davranisi kaliyor ve onu base.css veriyor
   (`.module-head { align-items: flex-start }` 720px altinda). */
@media (max-width: 760px) {
  .store-search {
    width: 100%;
  }
}
</style>
