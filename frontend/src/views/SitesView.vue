<template>
  <section class="panel">
    <div class="sites-header">
      <div>
        <p class="section-kicker">{{ t("panel.sites.management") }}</p>
        <h2>{{ t("panel.sites.title") }}</h2>
        <p class="meta">{{ t("panel.sites.subtitle") }}</p>
      </div>

      <div class="sites-actions">
        <button class="secondary compact" :disabled="sitesStore.loading" @click="refreshSites">
          {{ t("panel.sites.refresh") }}
        </button>
        <button v-if="isAdmin" class="secondary compact" :disabled="sitesStore.importing" @click="importExistingSites">
          {{ t("panel.sites.import") }}
        </button>
        <!-- TEK BIRINCIL EYLEM. Dugme ACIKKEN "Iptal" yazmasina ragmen birincil (mavi gradyan)
             ciziliyordu; form acikken sayfada IKI mavi dugme oluyordu ("Iptal" ustte,
             "Siteyi olustur ve yayinla" altta) ve goz once ustteki iptali secip formu kapatiyordu.
             Acikken ikincile duser: sayfanin tek birincil eylemi her zaman gonderme dugmesidir. -->
        <button
          v-if="canCreateSite"
          :class="['compact', { secondary: showCreateForm }]"
          v-bind="writeGate()"
          @click="showCreateForm = !showCreateForm">
          {{ showCreateForm ? t("panel.sites.cancelCreate") : t("panel.sites.create") }}
        </button>
      </div>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">
      {{ notice }}
    </p>

    <section v-if="generatedCredentials.length" class="credentials-card">
      <div>
        <h3>{{ t("panel.sites.credentials.title") }}</h3>
        <p class="meta">{{ t("panel.sites.credentials.hint") }}</p>
      </div>
      <div class="credentials-list">
        <div v-for="credential in generatedCredentials" :key="credential.service" class="credential-row">
          <strong>{{ credential.service }}</strong>
          <code>{{ credential.userName }}</code>
          <code>{{ credential.password }}</code>
          <button class="secondary compact" type="button" @click="copyCredential(credential)">
            {{ t("panel.sites.credentials.copy") }}
          </button>
        </div>
      </div>
    </section>

    <!--
      OTOMATIK HAZIRLIK RAPORU ARTIK GORUNUYOR.

      OLCULDU: `defaultsReport` uc ayri yerde dolduruluyordu (site olusturma, ice aktarma,
      elle hazirlama) ama SABLONDA HIC OKUNMUYORDU; ona ait `.default-chip` kurallari da
      SiteDetailView'de olu duruyordu. Yani panel "SSL hazirlanamadi" bilgisini SUNUCUDAN
      ALIYOR ve kullaniciya HIC soylemiyordu — tam da yeni site kurulduktan sonra, kullanicinin
      neyin eksik kaldigini bilmesi gereken anda. Kismi basari gizlenmez, duzenlenir.

      Durum YALNIZ RENKLE anlatilmaz: iki grup AYRI BASLIK altinda ("Hazir" / "Hazirlanamadi"),
      boylece renk ayirt edemeyen kullanici da hangi ozelligin dustugunu okuyabiliyor.
    -->
    <section v-if="defaultsReport" class="defaults-card">
      <div class="section-head">
        <h3>{{ t("panel.sites.defaultsTitle") }}</h3>
      </div>
      <p class="meta">{{ defaultsSummary }}</p>

      <div v-if="defaultsReport.readyFeatures.length > 0" class="defaults-grid">
        <span class="tile-label">{{ t("panel.sites.defaultsReadyLabel") }}</span>
        <span v-for="feature in defaultsReport.readyFeatures" :key="`ready-${feature}`" class="default-chip ready">
          {{ feature }}
        </span>
      </div>

      <div v-if="defaultsReport.failedFeatures.length > 0" class="defaults-grid">
        <span class="tile-label">{{ t("panel.sites.defaultsFailedLabel") }}</span>
        <span v-for="feature in defaultsReport.failedFeatures" :key="`failed-${feature}`" class="default-chip failed">
          {{ feature }}
        </span>
      </div>
    </section>

    <form v-if="showCreateForm && canCreateSite" class="create-card" @submit.prevent="submitCreateSite">
      <div class="create-card-head">
        <div>
          <h3>{{ t("panel.sites.createTitle") }}</h3>
          <p class="meta">{{ t("panel.sites.createHint") }}</p>
        </div>
        <!-- Panelin ORTAK rozeti (`.status-pill`) kullanilir; bu ekranin kendine ait `.ready-chip`i
             vardi ve vurgu MAVISI ile ciziliyordu, yani bir bilgi etiketi tabloların "calisiyor"
             rozetinden daha baskin duruyordu. -->
        <span class="status-pill">{{ t("panel.sites.starterIncluded") }}</span>
      </div>

      <div class="site-create-grid">
        <label class="domain-field">
          <span>{{ t("panel.sites.domainName") }}</span>
          <input v-model.trim="createForm.hostName" maxlength="255" placeholder="example.com" required />
          <small class="field-hint">{{ t("panel.sites.domainNameHint") }}</small>
        </label>

        <aside class="automatic-plan">
          <strong>{{ t("panel.sites.automatic.title") }}</strong>
          <span>{{ t("panel.sites.automatic.runtime") }}</span>
          <span>{{ t("panel.sites.automatic.dns") }}</span>
          <span>{{ t("panel.sites.automatic.services") }}</span>
        </aside>

        <template v-if="showAdvanced">
          <label>
            <span>{{ t("panel.sites.name") }}</span>
            <input v-model="createForm.name" minlength="2" maxlength="128" required />
            <small class="field-hint">{{ t("panel.sites.internalNameHint") }}</small>
          </label>

          <label v-if="isAdmin">
            <span>{{ t("panel.sites.physicalPath") }}</span>
            <input v-model="createForm.physicalPath" required @input="physicalPathEdited = true" />
          </label>

          <label>
            <span>{{ t("panel.sites.runtimeProfile") }}</span>
            <select v-model="createForm.runtimeProfile">
              <option value="auto">{{ t("panel.sites.runtime.auto") }}</option>
              <option value="static">{{ t("panel.sites.runtime.static") }}</option>
              <option value="node">{{ t("panel.sites.runtime.node") }}</option>
              <option value="python">{{ t("panel.sites.runtime.python") }}</option>
            </select>
            <small>{{ t("panel.sites.runtimeHint") }}</small>
          </label>

          <label v-if="createForm.runtimeProfile === 'auto'">
            <span>{{ t("panel.sites.phpVersion") }}</span>
            <select v-model="createForm.runtimeComponentId">
              <option value="">{{ t("panel.sites.runtimeNewest") }}</option>
              <option v-for="component in phpComponents" :key="component.id" :value="component.id">
                {{ component.displayName }}
              </option>
            </select>
          </label>

          <label v-if="createForm.runtimeProfile === 'node' || createForm.runtimeProfile === 'python'">
            <span>{{ t("panel.apps.startupFile") }}</span>
            <input v-model="createForm.startupFile" />
          </label>

        </template>
      </div>

      <button type="button" class="secondary compact advanced-toggle" @click="showAdvanced = !showAdvanced">
        {{ showAdvanced ? t("panel.sites.hideAdvanced") : t("panel.sites.showAdvanced") }}
      </button>

      <div class="form-surface companion-section">
        <label class="checkbox">
          <input v-model="createForm.createDatabase" type="checkbox" />
          <span>{{ t("panel.sites.companion.createDb") }}</span>
        </label>
        <div v-if="createForm.createDatabase" class="form-grid companion-grid">
          <label>
            <span class="field-label">{{ t("panel.database.databaseName") }}</span>
            <input v-model="createForm.databaseName" :placeholder="t('panel.database.optional')" />
          </label>
          <label>
            <span class="field-label">{{ t("panel.database.userName") }}</span>
            <input v-model="createForm.databaseUserName" :placeholder="t('panel.database.optional')" />
          </label>
          <label>
            <span class="field-label">{{ t("panel.database.userPassword") }}</span>
            <PasswordField v-model="createForm.databasePassword" :placeholder="t('panel.database.optional')" />
          </label>
        </div>
      </div>

      <div class="create-actions">
        <p class="meta">{{ t("panel.sites.automatic.submitHint") }}</p>
        <button type="submit" v-bind="writeGate(sitesStore.creating)">
          {{ sitesStore.creating ? t("panel.sites.creating") : t("panel.sites.createAndPublish") }}
        </button>
      </div>
    </form>

    <section class="domain-list">
      <div class="section-head">
        <h3>{{ t("panel.sites.title") }}</h3>
        <!-- Arama SAYFALAMADAN ÖNCE uygulanır: kullanıcı bütün listede arar, sayfalama yalnız
             görüneni sınırlar. Aksi halde 3. sayfadaki site aranınca bulunamıyor sanılırdı. -->
        <input
          v-model="siteSearch"
          class="site-search"
          type="search"
          :placeholder="t('panel.sites.searchPlaceholder')" />
        <span class="meta">{{ filteredSites.length }} / {{ sitesStore.sites.length }}</span>
      </div>

      <div v-if="preview" class="preview-card">
        <div class="preview-card-head">
          <strong>{{ t("panel.sites.previewReady") }}</strong>
          <button class="secondary compact" @click="preview = null">{{ t("panel.sites.previewClose") }}</button>
        </div>
        <p v-if="preview.url" class="preview-line">
          <!-- `secondary` EKLENDI: bu bag daha once yalnizca `link-btn compact` tasiyordu ve
               `.link-btn` tanimi `.domain-row-actions` altina hapsedilmisti — yani onizleme
               kartindaki "Ac" HICBIR dugme bicimi almiyor, kalin bir duz baglanti olarak
               ciziliyordu. Ayni isi yapan satir icindeki dugmeyle ayni gorunmeli. -->
          <a class="secondary compact link-btn" :href="preview.url" target="_blank" rel="noopener">{{ t("panel.sites.previewOpenLink") }} ↗</a>
          <a class="preview-url" :href="preview.url" target="_blank" rel="noopener">{{ preview.url }}</a>
        </p>
        <p class="meta">{{ t("panel.sites.previewHostsHint") }}</p>
        <code v-if="preview.hostsLine" class="preview-hosts">{{ preview.hostsLine }}</code>
      </div>

      <div v-if="filteredSites.length > 0" class="domain-rows">
        <article
          v-for="site in pager.items.value"
          :key="site.id"
          :class="['domain-row', { active: site.name === sitesStore.selectedSiteName }]">
          <div class="domain-row-main" @click="openTool(site.name, 'pano')">
            <span class="domain-glyph" aria-hidden="true">WWW</span>
            <div class="domain-id-copy">
              <!-- Uc satir da tek satira KIRPILIYOR (ellipsis). Kirpilan metnin tamami `title`da
                   durmazsa uzun alan adi / derin dosya yolu ekranda OKUNAMAZ hale gelir; kirpma
                   bir bicim karari, bilgiyi silme karari degildir. -->
              <strong :title="site.name">{{ site.name }}</strong>
              <a
                class="domain-url"
                :href="siteUrl(site)"
                :title="siteUrl(site)"
                target="_blank"
                rel="noopener"
                @click.stop>{{ siteUrl(site) }} ↗</a>
              <span class="domain-path" :title="site.physicalPath">{{ site.physicalPath }}</span>
            </div>
          </div>

          <!-- PANELIN ORTAK DURUM ROZETI. Bu ekran kendi `.pill` sozlugunu kullaniyordu ve
               "calisiyor" durumunu VURGU MAVISI ile ciziyordu; ayni ailedeki UsersView ve
               SiteBackupView ise ortak `.status-pill` ile yesil/kehribar kullaniyor. Yani ayni
               panelde "iyi durum" bir ekranda mavi, digerinde yesildi. Renk zaten tek kanal
               degil (durum metni rozetin icinde yaziyor); degisen yalnizca hangi sozlugun
               kullanildigi. -->
          <span :class="['status-pill', isRunning(site) ? 'ok' : 'warn']">
            {{ translateState(site.state) }}
          </span>

          <!--
            KUTUCUKLAR ARTIK IKON TASIYOR, KISALTMA DEGIL. Olculdu (canli 0.8.0): damgalar
            (FL, P, SSL, SQL, FT, DNS) **8.1px** ciziliyordu ve besi ayri bir kisaltma dilinden
            geliyordu. Tam ad kaybolmadi, tersine ERISILEBILIR oldu: `title` fareyle, yeni eklenen
            `aria-label` ise ekran okuyucuyla verir (eskiden dugmenin erisilebilir adi "FL" idi).
          -->
          <nav class="domain-tools" :aria-label="site.name">
            <button
              v-for="tool in quickTools"
              :key="tool.id"
              class="tool-chip"
              :title="tool.label"
              :aria-label="tool.label"
              @click="openTool(site.name, tool.id)">
              <svg class="chip-icon" viewBox="0 0 24 24" aria-hidden="true" v-html="siteToolIcon(tool.id)"></svg>
            </button>
          </nav>

          <div class="domain-row-actions">
            <!--
              "SİTEYİ AÇ" SATIRDA İKİ KEZ DURUYORDU.

              ÖLÇÜLDÜ: satırın solundaki `.domain-url` bağlantısı ile buradaki düğme AYNI adrese
              (`siteUrl(site)`), AYNI şekilde (`target="_blank"`) gidiyordu; aralarında yalnızca
              rozet ve araç simgeleri vardı. Aynı işi yapan iki denetim, ekranda "bunlar farklı
              şeyler" izlenimi verir.

              KALDIRILAN DÜĞME DEĞİL, TEKRAR: adres bağlantısı yerinde duruyor ve artık dış
              bağlantı olduğu ok işaretiyle görünür. Düğme biçimi seçilmedi çünkü adresin
              KENDİSİ zaten en doğru etikettir — kullanıcı nereye gideceğini okuyarak görür.
            -->
            <button
              class="secondary compact"
              :disabled="previewLoading === site.name || sitesStore.busySiteName === site.name"
              @click="handlePreview(site.name)">
              {{ previewLoading === site.name ? t("panel.common.loading") : t("panel.sites.preview") }}
            </button>
            <button
              v-if="!isRunning(site)"
              class="secondary compact"
              v-bind="writeGate(sitesStore.busySiteName === site.name)"
              @click="handleStartSite(site.name)">
              {{ t("panel.sites.start") }}
            </button>
            <button
              v-else
              class="secondary compact"
              v-bind="writeGate(sitesStore.busySiteName === site.name)"
              @click="handleStopSite(site.name)">
              {{ t("panel.sites.stop") }}
            </button>
            <button
              class="danger compact"
              v-bind="writeGate(sitesStore.busySiteName === site.name)"
              @click="handleDeleteSite(site.name)">
              {{ t("panel.sites.delete") }}
            </button>
          </div>
        </article>
      </div>

      <!-- "Hiç site yok" ile "aramaya uyan site yok" AYRI şeyler: ikincisinde kurulum daveti
           göstermek, var olan siteleri kaybolmuş gibi gösterirdi. -->
      <p v-else-if="sitesStore.sites.length > 0" class="empty-state">{{ t("panel.sites.searchEmpty") }}</p>

      <!-- Liste GELMEDEN kurulum daveti çiziliyordu: sunucuda siteler VARKEN kullanıcı
           "hiç siteniz yok" okuyordu. Davet artık ancak liste BAŞARIYLA okunduktan sonra çıkar
           (bkz. stores/sites.ts `loaded`). -->
      <p v-else-if="!sitesStore.loaded" class="empty-state" role="status" aria-live="polite">
        {{ t("panel.common.loading") }}
      </p>

      <div v-else class="empty-onboard">
        <svg viewBox="0 0 24 24" class="empty-icon" aria-hidden="true">
          <circle cx="12" cy="12" r="9" />
          <path d="M3.5 12h17M12 3c2.5 2.5 2.5 15.5 0 18M12 3c-2.5 2.5-2.5 15.5 0 18" />
        </svg>
        <h3>{{ t("panel.sites.noSites") }}</h3>
        <p class="meta">{{ t("panel.sites.noSitesHint") }}</p>
        <button v-if="canCreateSite" class="compact" v-bind="writeGate()" @click="showCreateForm = true">{{ t("panel.sites.create") }}</button>
      </div>

      <TablePager :state="pager" />
    </section>

  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import PasswordField from "../components/PasswordField.vue";
import type { SiteBindingPayload, SiteDefaultsResult, SiteRecord, SitePreview } from "../api/sites";
import { getSiteDeletionPlan, getSitePreview } from "../api/sites";
import { getPanelPreferences } from "../api/panelSettings";
import { useConfirm } from "../components/ConfirmDialog.vue";
import TablePager from "../components/TablePager.vue";
import { usePagination } from "../composables/usePagination";
import { extractApiDetails, extractApiMessage, useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
import { copyToClipboard } from "../utils/clipboard";
import { looksLikeMessageKey, splitCompositeMessage } from "../utils/apiError";
import { siteToolIcon } from "../utils/siteToolIcons";
import { useSoftwareStore } from "../stores/software";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";

const { t } = useI18n();
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri kapiya girmez.
const { writeGate } = useSubscriptionGate();
const { confirm } = useConfirm();
const sitesStore = useSitesStore();
const softwareStore = useSoftwareStore();
const authStore = useAuthStore();
// Site oluşturma/içe aktarma yalnız admin (bayi/müşteri mevcut sitelerini yönetir, oluşturamaz).
const isAdmin = computed(() => authStore.session?.user?.role === "Administrator");
// Site oluşturma: admin + bayi + müşteri (backend kota + kapsam dayatır). Import yalnız admin.
const canCreateSite = computed(() => {
  const role = authStore.session?.user?.role;
  return role === "Administrator" || role === "Reseller" || role === "Customer";
});

// profesyonel panel benzeri domain kartlarindaki hizli eylemler -> ilgili site araci sekmesine git.
const emit = defineEmits<{ (event: "open-tool", payload: { siteName: string; tool: string }): void }>();

// Site listesi: ada, fiziksel yola ve bağlama host adlarına göre aranır; sonra sayfalanır
// (bkz. composables/usePagination.ts). Yüzlerce site tek seferde DOM'a basılıyordu.
const siteSearch = ref("");
const filteredSites = computed(() => {
  const query = siteSearch.value.trim().toLowerCase();
  if (!query) {
    return sitesStore.sites;
  }
  return sitesStore.sites.filter((site) =>
    [site.name, site.physicalPath, ...site.bindings.map((binding) => binding.hostName)]
      .some((value) => (value ?? "").toLowerCase().includes(query)));
});
const pager = usePagination(filteredSites);
watch(siteSearch, () => pager.reset());

const quickTools = computed(() => [
  { id: "hosting", label: t("panel.sites.tools.hosting") },
  { id: "files", label: t("panel.sites.tools.files") },
  { id: "php", label: t("panel.sites.tools.php") },
  { id: "mail", label: t("panel.sites.tools.mail") },
  { id: "ssl", label: t("panel.sites.tools.ssl") },
  // Veritabanları (admin-only): site iş alanında DB ekle/sil + phpMyAdmin (SiteDatabasesView).
  ...(isAdmin.value ? [{ id: "webdb", label: t("panel.sites.tools.domaindb") }] : []),
  { id: "ftp", label: t("panel.sites.tools.ftp") },
  { id: "dns", label: t("panel.sites.tools.dns") }
]);

function openTool(siteName: string, tool: string) {
  sitesStore.selectSite(siteName);
  emit("open-tool", { siteName, tool });
}

function isRunning(site: SiteRecord): boolean {
  return site.state.toLowerCase() === "started";
}

function siteUrl(site: SiteRecord): string {
  // nip.io önizleme bağlamalarını ATLA → asıl domain gösterilsin (önizleme host'u "Siteyi aç"ı ele geçirmesin).
  const binding = site.bindings.find((item) => item.hostName && !item.hostName.toLowerCase().endsWith(".nip.io")) ?? site.bindings[0];
  if (!binding) {
    return "#";
  }
  const proto = (binding.protocol || "http").toLowerCase();
  const host = binding.hostName || window.location.hostname;
  const standard = (proto === "http" && binding.port === 80) || (proto === "https" && binding.port === 443);
  const portPart = standard ? "" : `:${binding.port}`;
  return `${proto}://${host}${portPart}`;
}

const notice = ref("");
const noticeError = ref(false);
const defaultsReport = ref<SiteDefaultsResult | null>(null);

/**
 * Otomatik hazirlik raporunun tek cumlelik ozeti.
 *
 * MEVCUT anahtarlar kullanilir (`defaultsPrepared` / `defaultsPartial`): bunlar zaten
 * `handlePrepareDefaults` bildiriminde basiliyordu, yani metin YENI degil — yalnizca artik
 * kaybolan bildirimden sonra da ekranda kaliyor.
 */
const defaultsSummary = computed(() => {
  const report = defaultsReport.value;
  if (!report) {
    return "";
  }

  return report.isComplete
    ? t("panel.sites.defaultsPrepared")
    : t("panel.sites.defaultsPartial", { features: report.failedFeatures.join(", ") });
});
const generatedCredentials = ref<Array<{ service: string; userName: string; password: string }>>([]);
const showCreateForm = ref(false);
const showAdvanced = ref(false);
const physicalPathEdited = ref(false);
const defaultSiteRoot = ref("C:\\inetpub\\wwwroot");
const createForm = reactive({
  name: "",
  physicalPath: "C:\\inetpub\\wwwroot\\new-site",
  applicationPoolName: "",
  hostName: "",
  ipAddress: "*",
  port: 80,
  protocol: "http",
  autoStart: true,
  runtimeProfile: "auto",
  runtimeComponentId: "",
  installRuntimeIfMissing: true,
  startupFile: "",
  provisionDefaultServices: true,
  createDatabase: false,
  databaseEngine: "",
  databaseName: "",
  databaseUserName: "",
  databasePassword: "",
  createMailDomain: false,
  createFtpUser: false,
  ftpUserName: "",
  ftpPassword: ""
});

const phpComponents = computed(() =>
  (softwareStore.catalog?.manifest ?? []).filter((component) => component.id.startsWith("php-")));

watch(() => createForm.hostName, (hostName) => {
  const name = sanitizeSiteName(hostName);
  createForm.name = name;
  if (!physicalPathEdited.value) {
    createForm.physicalPath = `${defaultSiteRoot.value}\\${sanitizeSiteName(name || "new-site")}`;
  }
});

watch(() => createForm.runtimeProfile, (profile) => {
  createForm.runtimeComponentId = "";
  createForm.startupFile = profile === "node" ? "server.js" : profile === "python" ? "app.py" : "";
});

watch(() => sitesStore.selectedSiteName, () => {
  defaultsReport.value = null;
});

onMounted(async () => {
  await getPanelPreferences()
    .then((settings) => {
      defaultSiteRoot.value = settings.defaultSiteRoot.replace(/[\\/]+$/, "");
      createForm.runtimeProfile = settings.defaultRuntimeProfile || "auto";
      createForm.installRuntimeIfMissing = settings.installRuntimeIfMissing;
      if (!physicalPathEdited.value) {
        createForm.physicalPath = `${defaultSiteRoot.value}\\${sanitizeSiteName(createForm.name || "new-site")}`;
      }
    })
    .catch(() => undefined);

  if (!softwareStore.catalog && !softwareStore.loading) {
    await softwareStore.fetchCatalog().catch(() => undefined);
  }

  if (sitesStore.sites.length > 0 || sitesStore.loading) {
    return;
  }

  try {
    await sitesStore.fetchSites();
  } catch (error) {
    showSiteLoadError(error);
  }
});

async function refreshSites() {
  try {
    await sitesStore.fetchSites();
    showNotice(t("panel.sites.refreshed"), false);
  } catch (error) {
    showSiteLoadError(error);
  }
}

async function importExistingSites() {
  try {
    await sitesStore.importExistingSites();
    if (sitesStore.selectedSiteName) {
      defaultsReport.value = await sitesStore.prepareSiteDefaults(sitesStore.selectedSiteName).catch(() => null);
    }
    showNotice(t("panel.sites.imported"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function handleAddSubdomain(label: string) {
  const parent = sitesStore.selectedSiteName;
  if (!parent || !label) {
    return;
  }
  const host = `${label}.${parent}`.toLowerCase();
  try {
    await sitesStore.createSite({
      name: host,
      hostName: host,
      physicalPath: `${defaultSiteRoot.value}\\${host}`,
      applicationPoolName: "",
      ipAddress: "*",
      port: 80,
      protocol: "http",
      autoStart: true,
      runtimeProfile: "auto",
      runtimeComponentId: "",
      installRuntimeIfMissing: true,
      startupFile: "",
      provisionDefaultServices: false
    });
    await sitesStore.fetchSites().catch(() => undefined);
    showNotice(t("panel.sites.subdomainCreated", { host }), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function submitCreateSite() {
  try {
    const requestedPort = createForm.port;
    const result = await sitesStore.createSite({ ...createForm });
    const site = result.site;
    generatedCredentials.value = result.generatedCredentials ?? [];
    defaultsReport.value = await sitesStore.prepareSiteDefaults(site.name).catch(() => null);
    const createdPort = site.bindings[0]?.port ?? requestedPort;
    createForm.name = "";
    createForm.hostName = "";
    createForm.applicationPoolName = "";
    createForm.ipAddress = "*";
    createForm.port = 80;
    createForm.runtimeProfile = "auto";
    createForm.runtimeComponentId = "";
    createForm.installRuntimeIfMissing = true;
    createForm.startupFile = "";
    createForm.provisionDefaultServices = true;
    createForm.createDatabase = false;
    createForm.createMailDomain = false;
    createForm.createFtpUser = false;
    createForm.ftpUserName = "";
    createForm.ftpPassword = "";
    createForm.physicalPath = `${defaultSiteRoot.value}\\new-site`;
    physicalPathEdited.value = false;
    showCreateForm.value = false;

    const base = createdPort !== requestedPort
      ? t("panel.sites.createdWithPort", { port: createdPort })
      : t("panel.sites.created");
    const extras = result.created.length
      ? ` ${t("panel.sites.companion.created")}: ${result.created.join(", ")}`
      : "";
    if (result.warnings.length) {
      showNotice(`${base}${extras} — ${t("panel.sites.companion.warnings")}: ${result.warnings.map(warningLine).join(" • ")}`, true);
    } else {
      showNotice(`${base}${extras}`, false);
    }
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function handleStartSite(siteName: string) {
  try {
    await sitesStore.startSite(siteName);
    showNotice(t("panel.sites.started"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

// Domainsiz önizleme: linki kartta gösterir (window.open await sonrası tarayıcıca engellenir → kullanıcı KARTTAKİ
// linke tıklar). previewLoading = işlenen site adı (buton "Yükleniyor" gösterir).
const preview = ref<SitePreview | null>(null);
const previewLoading = ref("");
async function handlePreview(siteName: string) {
  previewLoading.value = siteName;
  preview.value = null;
  try {
    preview.value = await getSitePreview(siteName);
    showNotice(
      preview.value.method === "nipio" ? t("panel.sites.previewOpened") : t("panel.sites.previewHostsOnly"),
      preview.value.method !== "nipio");
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    previewLoading.value = "";
  }
}

async function handlePrepareDefaults(siteName: string) {
  try {
    const result = await sitesStore.prepareSiteDefaults(siteName);
    defaultsReport.value = result;
    showNotice(
      result.isComplete
        ? t("panel.sites.defaultsPrepared")
        : t("panel.sites.defaultsPartial", { features: result.failedFeatures.join(", ") }),
      !result.isComplete);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function handleStopSite(siteName: string) {
  try {
    await sitesStore.stopSite(siteName);
    showNotice(t("panel.sites.stopped"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function handleDeleteSite(siteName: string) {
  // profesyonel paneller gibi: silmeden önce siteye bağlı tüm kaynakları göster, sonra cascade sil.
  let lines: string[] = [];
  try {
    const plan = await getSiteDeletionPlan(siteName);
    const items: string[] = [];
    if (plan.mailDomains.length) {
      items.push(`${t("panel.sites.deletePlan.mail")}: ${plan.mailDomains.join(", ")}`);
    }
    if (plan.databases.length) {
      items.push(`${t("panel.sites.deletePlan.databases")}: ${plan.databases.join(", ")}`);
    }
    if (plan.ftpUsers.length) {
      items.push(`${t("panel.sites.deletePlan.ftp")}: ${plan.ftpUsers.join(", ")}`);
    }
    if (plan.hasDnsZone) {
      items.push(t("panel.sites.deletePlan.dns"));
    }
    if (plan.hasReverseProxy) {
      items.push(t("panel.sites.deletePlan.proxy"));
    }
    if (plan.hasDiskQuota) {
      items.push(t("panel.sites.deletePlan.quota"));
    }
    if (plan.hasGitDeployment) {
      items.push(t("panel.sites.deletePlan.git"));
    }
    if (plan.customerAssignments > 0) {
      items.push(`${t("panel.sites.deletePlan.customers")}: ${plan.customerAssignments}`);
    }
    // Zamanlanmış görevler ayrıca yazılır: bunlar site silinse de tetiklenmeye devam eden
    // gerçek Windows görevleridir, yani kullanıcının en çok şaşıracağı kalem budur.
    if (plan.scheduledTasks > 0) {
      items.push(`${t("panel.sites.deletePlan.scheduledTasks")}: ${plan.scheduledTasks}`);
    }
    items.push(`${t("panel.sites.deletePlan.files")}: ${plan.applicationRootPath || plan.physicalPath}`);
    lines = items;
  } catch {
    /*
     * Plan OKUNAMADI — bu, "silinecek bir şey yok" DEMEK DEĞİLDİR.
     *
     * Eskiden liste sessizce BOŞ kalıyordu ve kullanıcı, etkilenecek kalem olmadığını sanarak
     * site adını yazıp onaylıyordu; oysa silme yine de veritabanlarını, posta alanlarını ve
     * zamanlanmış görevleri düşürüyordu. Onay diyaloğunun boş "etkilenecekler" kutusu burada
     * YALAN söylüyordu. Artık listenin okunamadığı açıkça yazılır.
     */
    lines = [t("panel.sites.deletePlan.unavailable")];
  }

  // Yüksek riskli onay: kullanıcı devam etmek için site adını birebir yazmalı.
  await confirm({
    title: t("panel.confirm.deleteSiteTitle", { name: siteName }),
    message: t("panel.sites.confirmDelete"),
    impact: lines,
    requireText: siteName,
    action: async () => {
      try {
        // Cascade silme: yalnızca SAHİPLİĞİ DOĞRULANMIŞ veritabanları düşer (sahiplik kaydı ya da
        // açık site↔DB bağı). Adı siteye benzeyen ama sahipliği doğrulanamayan veritabanları
        // silinmez; sonuçtaki uyarılarda bildirilir ve aşağıda kullanıcıya gösterilir.
        // deleteDatabases burada açıkça true: kullanıcı planı gördü ve site adını yazarak onayladı.
        // Sunucu varsayılanı bilerek false — panel dışı çağıranlar (MCP, API anahtarı, betikler)
        // istemeden veritabanı düşürmesin.
        const result = await sitesStore.deleteSite(siteName, { deleteDatabases: true });
        // ÖLÇÜLEMEYEN SONUÇ, "temizlenemedi" BAŞLIĞIYLA GÖSTERİLMEZ: o başlık da kesin bir
        // iddiadır ve sunucu tam olarak bu iddiayı kuramadığını bildiriyor. Kullanıcıya
        // bilmediğimizi ve klasörü elle kontrol etmesi gerektiğini söyleyen ayrı bir başlık var.
        if (result && result.filesDeletionMeasured === false) {
          showNotice(`${t("panel.sites.deletedFilesUnmeasured")}: ${result.warnings.join(" • ")}`, true);
        } else if (result && result.warnings.length) {
          showNotice(`${t("panel.sites.deletedWithWarnings")}: ${result.warnings.join(" • ")}`, true);
        } else {
          showNotice(t("panel.sites.deleted"), false);
        }
      } catch (error) {
        showNotice(resolveApiMessage(error), true);
      }
    },
  });
}

async function handleAddBinding(payload: { siteName: string; binding: SiteBindingPayload }) {
  try {
    await sitesStore.addBinding(payload.siteName, payload.binding);
    showNotice(t("panel.sites.bindingAdded"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

/**
 * Bağlantı kaldırma ONAYSIZ YAPILMAZ.
 *
 * Denetimde bulundu: bu işlem tek tıkla siteyi bir adresten YAYINDAN KALDIRIYORDU — hemen
 * yanındaki "siteyi sil" ise kullanıcıya site adını harfi harfine yazdırıyor. Yıkıcılık farkı
 * onay farkını haklı çıkarmıyordu: ziyaretçi açısından sonuç aynı, site o adreste yok.
 * Yüksek riskli onay (metin yazdırma) BURADA kullanılmadı; geri alınabilir bir işlem için
 * o sürtünme, kullanıcıyı onayları okumadan geçmeye alıştırır.
 */
async function handleRemoveBinding(payload: { siteName: string; binding: SiteBindingPayload }) {
  await confirm({
    title: t("panel.sites.removeBinding"),
    message: t("panel.sites.confirmRemoveBinding"),
    action: async () => {
      try {
        await sitesStore.removeBinding(payload.siteName, payload.binding);
        showNotice(t("panel.sites.bindingRemoved"), false);
      } catch (error) {
        showNotice(resolveApiMessage(error), true);
      }
    }
  });
}

function translateState(state: string): string {
  const key = `panel.sites.runtime.${state.toLowerCase()}`;
  const translated = t(key);
  return translated === key ? state : translated;
}

/**
 * Site oluşturma/silme uyarı satırını ÇEVRİLEBİLİR hâle getirir.
 *
 * NEDEN: `SiteCreationResultDto.Warnings` satırları ekrana HAM basılıyordu. Arka uç artık DNS
 * yayınlanmama durumunu `"anahtar — ayrıntı"` biçiminde gönderiyor (bkz.
 * `SitesController.TryEnsureSubdomainRecordAsync`); çevirmezsek kullanıcı kırmızı şeritte
 * birebir `dns.not_published_no_server` yazısını görürdü — panelin `MessageKeyCoverageTests`
 * nöbetçisinin tam olarak önlemeye çalıştığı şey.
 *
 * Eski satırlar bozulmaz: `splitCompositeMessage` anahtar biçimine uymayan metni bölmeden geri
 * verir, o da olduğu gibi basılır.
 */
function warningLine(raw: string): string {
  const { key, detail } = splitCompositeMessage(raw);
  if (!looksLikeMessageKey(key)) {
    return raw;
  }

  const translated = t(key);
  const text = translated === key ? key : translated;
  return detail ? `${text} (${detail})` : text;
}

function resolveApiMessage(error: unknown): string {
  const key = extractApiMessage(error);
  const translated = t(key);
  const message = translated === key ? key : translated;
  const details = extractApiDetails(error);
  return details ? `${message}\n\n${details}` : message;
}

function showSiteLoadError(error: unknown) {
  const key = extractApiMessage(error);
  if (key === "sites.iis_not_available" || key === "sites.iis_install_required") {
    showNotice(t("panel.sites.iisPreparationHint"), false);
    return;
  }

  showNotice(resolveApiMessage(error), true);
}

function sanitizeSiteName(value: string): string {
  return value.trim().toLowerCase().replace(/[^a-z0-9._-]+/g, "-") || "new-site";
}

function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}

/**
 * Kimlik bilgilerini panoya kopyalar.
 *
 * Sonuç ÖLÇÜLÜR: `navigator.clipboard` yalnızca güvenli bağlamda vardır ve panel daha sertifika
 * almadan — yani tam da yeni sitenin parolasının gösterildiği anda — düğme sessizce ölüyordu.
 * Artık yedek yol denenir; o da olmazsa "kopyalandı" DENMEZ, ne yapılacağı yazılır.
 */
async function copyCredential(credential: { service: string; userName: string; password: string }) {
  const metin = `${credential.service}\n${t("panel.database.userName")}: ${credential.userName}\n${t("panel.form.password")}: ${credential.password}`;
  const oldu = await copyToClipboard(metin);
  showNotice(oldu ? t("panel.sites.credentials.copied") : t("panel.common.copyFailed"), !oldu);
}
</script>

<style scoped>
/* `.sites-panel` KALDIRILDI: kok oge zaten `.panel` ve `.panel` ayni izgarayi `--sp-12`
   araligiyla veriyordu. Yerel kopya yalnizca araligi 14px'e cekiyordu, yani bu ekran
   panelin geri kalanindan 2px farkli bir ritimle nefes aliyordu — gorunmez ama her
   bolumde birikiyordu. */
.create-card,
.site-create-grid,
.credentials-card,
.defaults-card,
.credentials-list {
  display: grid;
  gap: var(--sp-12);
}

.sites-header,
.sites-actions,
.create-card-head,
.create-actions {
  display: flex;
  gap: var(--sp-12);
}

.sites-header {
  justify-content: space-between;
  align-items: flex-start;
}

.site-search {
  /* Icerik olcusu: "example.com" ve tipik alan adlarini kirpmadan gosteren taban/tavan. */
  flex: 1 1 200px;
  max-width: 320px;
  min-width: 0;
}

.sites-actions {
  align-items: center;
  flex-wrap: wrap;
}

.sites-actions button {
  width: auto;
}

.site-create-grid {
  grid-template-columns: minmax(0, 1.35fr) minmax(260px, 0.65fr);
}

.create-card {
  padding: var(--sp-16);
  border: 1px solid color-mix(in srgb, var(--accent-1) 20%, transparent);
  border-radius: var(--radius-lg);
  background:
    linear-gradient(120deg, color-mix(in srgb, var(--accent-2) 13%, transparent), transparent 42%),
    var(--surface-2);
}

.create-card-head,
.create-actions {
  justify-content: space-between;
  align-items: center;
}

/* `min-width: 190px` KALDIRILDI: olcek disi bir sayiydi ve zaten gereksizdi — bu dugmenin
   metni ("Siteyi olustur ve yayinla" / "Create and publish site") kendi basina 190px'i
   asiyor, yani kural hicbir sey yapmayip yalnizca dar dilde bosluk uretiyordu. */
.create-actions button {
  width: auto;
}

.automatic-plan,
.credentials-card,
.defaults-card {
  padding: var(--sp-12) var(--sp-16);
  border: 1px solid color-mix(in srgb, var(--accent-1) 18%, var(--panel-border));
  border-radius: var(--radius-md);
  background: color-mix(in srgb, var(--accent-1) 6%, var(--surface-1));
}

.automatic-plan {
  display: grid;
  gap: var(--sp-6);
}

.automatic-plan span::before {
  content: "✓";
  margin-right: var(--sp-6);
  color: var(--accent-1);
  font-weight: 800;
}

.credentials-card {
  border-color: color-mix(in srgb, var(--warn-1) 30%, var(--panel-border));
}

.credential-row {
  display: grid;
  grid-template-columns: minmax(140px, 0.8fr) minmax(120px, 1fr) minmax(160px, 1.2fr) auto;
  gap: var(--sp-10);
  align-items: center;
  padding: var(--sp-10) 0;
  border-top: 1px solid var(--panel-border);
}

.credential-row code {
  overflow: hidden;
  text-overflow: ellipsis;
  /* Kullanici adi/parola sutunlari alt alta dizilir; degisken genislikli rakamlar
     sutunu tirtikli gosteriyordu. */
  font-variant-numeric: tabular-nums;
}

/* Otomatik hazirlik raporu: etiket + cipler tek satirda akar. */
.defaults-grid {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--sp-6);
}

.default-chip {
  display: inline-flex;
  align-items: center;
  padding: var(--sp-2) var(--sp-10);
  border: 1px solid var(--panel-border);
  border-radius: 999px;
  background: var(--ring-track);
  color: var(--text-soft);
  font-size: var(--fs-2xs);
}

.default-chip.ready {
  border-color: color-mix(in srgb, var(--ok-1) 30%, transparent);
  background: var(--ok-bg);
  color: var(--ok-text);
}

.default-chip.failed {
  border-color: color-mix(in srgb, var(--warn-1) 30%, transparent);
  background: var(--warn-bg);
  color: var(--warn-text);
}

.domain-list {
  display: grid;
  gap: var(--sp-12);
}

.empty-onboard {
  display: grid;
  justify-items: center;
  gap: var(--sp-8);
  padding: var(--sp-28) var(--sp-20);
  text-align: center;
  border: 1px dashed var(--panel-border);
  border-radius: var(--radius-lg);
  background: var(--surface-1);
}

.empty-onboard h3 {
  margin: var(--sp-6) 0 0;
}

.empty-onboard .empty-icon {
  width: 44px;
  height: 44px;
  fill: none;
  stroke: var(--accent-1);
  stroke-width: 1.4;
  stroke-linecap: round;
  stroke-linejoin: round;
  opacity: 0.85;
}

.empty-onboard button {
  margin-top: 8px;
}

/* profesyonel panel benzeri tam genişlikte satır listesi (kare kartlar yerine). */
.domain-rows {
  display: flex;
  flex-direction: column;
  gap: var(--sp-8);
}

.domain-row {
  display: flex;
  align-items: center;
  gap: var(--sp-16);
  padding: var(--sp-12) var(--sp-16);
  border-radius: var(--radius-lg);
  border: 1px solid var(--panel-border);
  background: var(--surface-1);
  transition: border-color 0.15s ease, box-shadow 0.15s ease;
}

.domain-row:hover {
  border-color: color-mix(in srgb, var(--accent-1) 30%, transparent);
}

.domain-row.active {
  border-color: color-mix(in srgb, var(--accent-1) 45%, transparent);
  box-shadow: 0 0 0 1px color-mix(in srgb, var(--accent-1) 30%, transparent);
}

.domain-row-main {
  display: flex;
  align-items: center;
  gap: 12px;
  min-width: 0;
  flex: 1 1 280px;
  cursor: pointer;
}

.domain-path {
  color: var(--text-muted);
  /* 0.72rem = 9.72px idi — olcek tabani `--fs-2xs` (10.53px); altina inen kural yok. */
  font-size: var(--fs-2xs);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.domain-row-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  flex-shrink: 0;
}

/* `.domain-row-actions` ÖNEKİ KALDIRILDI. Satırdaki tekrarlı "Siteyi aç" bağlantısı
   kalkınca bu kural HİÇBİR ögeyi boyamıyordu; aynı sınıfı taşıyan önizleme kartındaki
   bağlantı ise zaten kuralın dışındaydı (bkz. o satırdaki yorum: sınıf `.domain-row-actions`
   altına hapsedildiği için `secondary` eklenerek telafi edilmişti). Kural artık sınıfın
   kendisine bağlı, yani tek bir tanım iki yeri de kapsıyor. */
.link-btn {
  display: inline-flex;
  align-items: center;
  text-decoration: none;
}

.domain-glyph {
  display: grid;
  place-items: center;
  width: 38px;
  height: 38px;
  border-radius: 10px;
  background: color-mix(in srgb, var(--accent-1) 14%, transparent);
  color: var(--accent-1);
  /* 0.62rem = 8.37px idi — olcek tabani `--fs-2xs` (10.53px); altina inen kural yok. */
  font-size: var(--fs-2xs);
  font-weight: 800;
  letter-spacing: 0.04em;
  flex-shrink: 0;
}

.domain-id-copy {
  display: grid;
  gap: 2px;
  min-width: 0;
}

.domain-id-copy strong {
  font-size: 0.96rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.domain-url {
  color: var(--accent-1);
  font-size: 0.78rem;
  text-decoration: none;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.domain-url:hover {
  text-decoration: underline;
}

/* Hızlı araç simgeleri — satırda kompakt ikon dizisi (profesyonel panel benzeri). */
.domain-tools {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  flex-shrink: 0;
}

.domain-tools .tool-chip {
  display: inline-grid;
  place-items: center;
  width: 32px;
  height: 32px;
  border-radius: 8px;
  border: 1px solid var(--panel-border);
  background: var(--surface-2);
  color: var(--text-strong);
  cursor: pointer;
}

.domain-tools .tool-chip:hover {
  border-color: color-mix(in srgb, var(--accent-1) 40%, transparent);
  color: var(--accent-1);
}

/* 8.1px'lik harf damgasının yerini alan ikon. 17px = kenar menüdeki `.nav-icon` ile AYNI
   ölçü; panelde tek bir ikon dili var. Çizgi kalınlığı da oradan (1.7) alındı. */
.chip-icon {
  width: 17px;
  height: 17px;
  fill: none;
  stroke: var(--accent-1);
  stroke-width: 1.7;
  stroke-linecap: round;
  stroke-linejoin: round;
}

@media (max-width: 900px) {
  .domain-row {
    flex-wrap: wrap;
  }
  .domain-row-main {
    flex: 1 1 100%;
  }
}

.meta {
  color: var(--text-muted);
}

.meta {
  margin: 0;
}

.pill {
  display: inline-flex;
  gap: 6px;
  align-items: center;
  border-radius: 999px;
  padding: 4px 10px;
  background: color-mix(in srgb, var(--accent-1) 14%, transparent);
  color: var(--accent-1);
  font-size: 0.82rem;
  font-weight: 700;
}

.pill.stopped {
  background: color-mix(in srgb, var(--warn-1) 12%, transparent);
  color: var(--warn-text);
}

.state-dot {
  width: 6px;
  height: 6px;
  border-radius: 999px;
  background: currentColor;
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
  gap: 8px;
}

/* bkz. base.css: onay kutusu/radyo ve dugmeler gerilmez. */
input:not([type="checkbox"]):not([type="radio"]) {
  width: 100%;
  border-radius: var(--radius-sm);
  font: inherit;
}

button {
  border-radius: var(--radius-sm);
  font: inherit;
}

/* `:deep()` ZORUNLU: bu gorunum alan yuksekligini base.css'ten BUYUK tutuyor (12/14 dolgu).
   Kapsamli CSS bir bilesenin ic elemanlarina gecmedigi icin PasswordField'in icindeki alan
   olcusuz kalir ve komsu alanlardan ~12px kisa gorunurdu -- ayni izgarada iki farkli yukseklik.
   Kural yalnizca bu gorunume ozgu olculeri tasir; goster/gizle davranisi bilesende kalir. */
input,
:deep(.password-field input) {
  border: 1px solid var(--panel-border);
  padding: 12px 14px;
  background: var(--input-bg);
  color: var(--text-strong);
}

button.compact {
  padding: 10px 12px;
}

button:disabled {
  opacity: 0.65;
  cursor: wait;
}

@media (max-width: 1080px) {
  .site-create-grid,
  .sites-columns,
  .credential-row {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 760px) {
  .sites-header,
  .sites-actions,
  .section-head,
  .site-item-top,
  .create-card-head,
  .create-actions {
    flex-direction: column;
    align-items: flex-start;
  }

  .create-actions button {
    width: 100%;
  }

  .sites-actions {
    width: 100%;
  }

  .sites-actions button {
    width: 100%;
  }

  /* Simgeler 32px'ti — parmak hedefi olarak küçük. */
  .domain-tools .tool-chip {
    width: 38px;
    height: 38px;
    min-height: 38px;
  }
}

/*
  Telefon: site listesi olay akışının 2. adımı ("siteyi başlat"). Satır kart düzenine geçer:
  1) kimlik (ad + adres + yol) 2) durum rozeti 3) eylemler 2x2 ızgarada, tam genişlikte.
*/
@media (max-width: 620px) {
  .domain-row {
    gap: 10px;
    padding: 12px;
  }

  .domain-row-main {
    flex: 1 1 100%;
  }

  .pill {
    align-self: flex-start;
  }

  /*
    Hızlı araç simgeleri (⚙ FL P @ SSL FT DNS) gizlenir: etiketsiz 8 simge telefonda hem
    okunmuyor hem de yanlış basmaya davet ediyor. Hepsi kaybolmuyor — satıra dokunmak
    site iş alanını açıyor ve oradaki araç listesi aynı araçları ADIYLA veriyor.
  */
  .domain-tools {
    display: none;
  }

  .domain-row-actions {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 8px;
    width: 100%;
  }

  .domain-row-actions > * {
    justify-content: center;
    width: 100%;
    min-height: 42px;
    text-align: center;
  }

  /* Dar ekran kuralı KALDIRILDI: `.domain-row-actions` içinde artık `.link-btn` taşıyan bir
     öge yok (tekrarlı "Siteyi aç" bağlantısı kalktı), yani kural ölü kalmıştı. Önizleme
     kartındaki bağlantı bu kırılımda da `secondary compact` görünümünü koruyor. */

  .domain-glyph {
    width: 34px;
    height: 34px;
  }

  .credentials-card,
  .preview-card {
    padding: 12px;
  }

  .preview-card-head {
    flex-wrap: wrap;
  }
}

.preview-card {
  display: grid;
  gap: 8px;
  padding: 14px 16px;
  margin-bottom: 14px;
  border: 1px solid var(--panel-border);
  border-radius: 10px;
  background: color-mix(in srgb, var(--accent-1) 8%, transparent);
}
.preview-card-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}
.preview-line a {
  font-weight: 600;
  word-break: break-all;
}
.preview-hosts {
  display: block;
  padding: 8px 10px;
  border-radius: 6px;
  background: var(--surface-0);
  border: 1px solid var(--panel-border);
  font-family: ui-monospace, "Cascadia Code", Consolas, monospace;
  font-size: 0.82rem;
  white-space: pre-wrap;
  word-break: break-all;
  user-select: all;
}
</style>
