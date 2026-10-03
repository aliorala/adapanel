<template>
  <section class="panel proxy-panel">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.proxy.title") }}</h2>
        <p class="meta">{{ t("panel.proxy.subtitle") }}</p>
      </div>

      <button
        class="secondary compact"
        :disabled="!selectedSiteName || readLoading || busy"
        @click="refreshConfiguration">
        {{ t("panel.proxy.refresh") }}
      </button>
    </div>

    <div v-if="notice" :class="['notice', 'page-notice', 'proxy-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">
      <p>{{ notice }}</p>
      <button type="button" class="secondary notice-close" :aria-label="t('panel.common.dismissNotice')" @click="notice = ''">×</button>
    </div>

    <div class="module-toolbar">
      <div class="segmented-tabs" role="tablist">
        <button role="tab" :aria-selected="activeSection === 'proxy'" :class="{ active: activeSection === 'proxy' }" @click="activeSection = 'proxy'">{{ t("panel.proxy.configTitle") }}</button>
        <button role="tab" :aria-selected="activeSection === 'rewrite'" :class="{ active: activeSection === 'rewrite' }" @click="activeSection = 'rewrite'">{{ t("panel.proxy.rewriteTitle") }}</button>
      </div>
      <!-- Etiketsiz bir <label> sarmalayici SEÇİCİYE AD VERMEZ: ekran okuyucu burayi "acilir
           liste, bos" diye okuyordu. Gorunur bir etiket arac cubugunu iki satira cikaracagi icin
           ad `aria-label` ile veriliyor. `.compact-select` (base.css) da bu yuzden var: aksi hâlde
           global `select { width: 100% }` kutuyu arac cubugunun TAMAMINA geriyordu. -->
      <select v-model="selectedSiteName" class="compact-select" :aria-label="t('panel.php.selectSite')">
        <option value="">{{ t("panel.php.selectSite") }}</option>
        <option v-for="site in sitesStore.sites" :key="site.name" :value="site.name">
          {{ site.name }}
        </option>
      </select>
    </div>

    <!-- OKUNAMADI, YUKLENIYOR'DAN AYRI.
         OLCULDU: `fetchConfiguration` patladiginda `currentConfiguration` null KALIYOR ve
         asagidaki kosul hep dogru oluyordu -- ekran SONSUZA KADAR "Yukleniyor…" yaziyordu.
         Ustteki kirmizi serit sebebi soyluyor ama sayfanin govdesi hâlâ "bekle" diyordu ve
         kullanicinin CIKIS YOLU yoktu: yenileme dugmesi sayfanin ta tepesinde, uc satir
         yukarida. `SectionLoadError` hem durumu duzeltiyor hem yerinde bir "Tekrar dene"
         veriyor. Mesaji TEKRARLAMIYOR: ayni cumle uc satir yukarida zaten yaziyor. -->
    <SectionLoadError
      v-if="selectedSiteName && (configFailed || (!measured && !!currentConfiguration && !readLoading && !busy))"
      :busy="readLoading || busy"
      @retry="refreshConfiguration" />

    <!-- Yapılandırma sunucudan gelmeden özet "devre dışı", form da varsayılan değerlerle
         görünüyordu; kullanıcı proxy'yi kapalı sanıp yanlış karar veriyordu. -->
    <p v-if="selectedSiteName && !currentConfiguration && !configFailed" class="empty-state" role="status" aria-live="polite">
      {{ t("panel.common.loading") }}
    </p>

    <template v-else-if="selectedSiteName && currentConfiguration">
      <div class="summary-strip">
        <article class="summary-item">
          <span class="tile-label">{{ t("panel.proxy.arrStatus") }}</span>
          <strong>{{ currentConfiguration?.isArrEnabled ? t("panel.proxy.arrEnabled") : t("panel.proxy.arrDisabled") }}</strong>
        </article>

        <article class="summary-item">
          <span class="tile-label">{{ t("panel.proxy.proxyStatus") }}</span>
          <strong>{{ currentConfiguration?.isProxyEnabled ? t("panel.proxy.proxyEnabled") : t("panel.proxy.proxyDisabled") }}</strong>
        </article>

        <article class="summary-item">
          <span class="tile-label">{{ t("panel.proxy.preserveHostHeader") }}</span>
          <strong>{{ currentConfiguration?.preserveHostHeader ? t("panel.status.enabled") : t("panel.status.disabled") }}</strong>
        </article>

        <article class="summary-item">
          <span class="tile-label">{{ t("panel.proxy.responseHeaderRewrite") }}</span>
          <strong>{{ currentConfiguration?.reverseRewriteHostInResponseHeaders ? t("panel.status.enabled") : t("panel.status.disabled") }}</strong>
        </article>
      </div>

      <div class="proxy-columns">
        <section v-if="activeSection === 'proxy'" class="proxy-card">
          <div class="section-head">
            <div>
              <h3>{{ t("panel.proxy.configTitle") }}</h3>
              <p class="meta">{{ t("panel.proxy.configSubtitle") }}</p>
            </div>
            <!-- KAPALI PROXY BIR UYARI DEGIL. Ters vekil OPSIYONEL bir ozellik: panelin
                 sitelerinin buyuk cogunlugu onu hic kullanmaz. Rozet `warn` iken bu ekran
                 kapali olan HER sitede kehribar "dikkat" cizgisi gosteriyordu -- oysa
                 olculen sey "bu sitede ters vekil yok", yani bir OLGU. Ayni karar bu
                 dosyanin asagisinda kural sayaci icin, AppStoreView'de calisma ortami
                 rozeti icin zaten verilmisti; burada atlanmisti. Etkin = `ok` KALDI:
                 o gercekten dogrulanmis, calisan bir yapilandirmadir. -->
            <span :class="['status-pill', { ok: currentConfiguration?.isProxyEnabled }]">
              {{ currentConfiguration?.isProxyEnabled ? t("panel.status.enabled") : t("panel.status.disabled") }}
            </span>
          </div>

          <label>
            <span>{{ t("panel.proxy.destinationUrl") }}</span>
            <input v-model="proxyForm.destinationUrl" placeholder="http://127.0.0.1:3000" />
          </label>

          <label>
            <span>{{ t("panel.proxy.mountPath") }}</span>
            <input v-model="proxyForm.mountPath" placeholder="/" />
          </label>

          <label class="checkbox">
            <input v-model="proxyForm.enableHtmlLinkRewrite" type="checkbox" />
            <span>{{ t("panel.proxy.htmlLinkRewrite") }}</span>
          </label>

          <div class="action-row">
            <button
              v-bind="writeGate(!canWrite || busy || !proxyForm.destinationUrl.trim())"
              @click="saveProxy">
              {{ proxyStore.savingSiteName === selectedSiteName ? t("panel.proxy.saving") : t("panel.proxy.save") }}
            </button>

            <button
              class="danger"
              v-bind="writeGate(!canWrite || busy || !currentConfiguration?.isProxyEnabled)"
              @click="removeProxy">
              {{ proxyStore.removingSiteName === selectedSiteName ? t("panel.proxy.removing") : t("panel.proxy.remove") }}
            </button>
          </div>

          <dl class="detail-grid">
            <div>
              <dt>{{ t("panel.proxy.destinationUrl") }}</dt>
              <dd>{{ currentConfiguration?.destinationUrl || "-" }}</dd>
            </div>
            <div>
              <dt>{{ t("panel.proxy.mountPath") }}</dt>
              <dd>{{ currentConfiguration?.mountPath || "/" }}</dd>
            </div>
            <div>
              <dt>{{ t("panel.proxy.webConfig") }}</dt>
              <dd>{{ currentConfiguration?.webConfigPath || "-" }}</dd>
            </div>
            <div>
              <dt>{{ t("panel.proxy.htmlLinkRewrite") }}</dt>
              <dd>{{ currentConfiguration?.hasHtmlLinkRewrite ? t("panel.status.enabled") : t("panel.status.disabled") }}</dd>
            </div>
          </dl>
        </section>

        <section v-if="activeSection === 'rewrite'" class="proxy-card">
          <div class="section-head">
            <div>
              <h3>{{ t("panel.proxy.rewriteTitle") }}</h3>
              <p class="meta">{{ t("panel.proxy.rewriteSubtitle") }}</p>
            </div>
            <!-- Bir SAYI durum degildir: kural sayisi kehribar "muted" rozetiyle ciziliyordu ve
                 "dikkat" gibi okunuyordu. Notr rozet (`.status-pill` tonsuz) dogru rutbe. -->
            <span class="status-pill">{{ currentConfiguration?.rewriteRules.length ?? 0 }}</span>
          </div>

          <label>
            <span>{{ t("panel.proxy.ruleName") }}</span>
            <input v-model="rewriteForm.name" />
          </label>

          <label>
            <span>{{ t("panel.proxy.matchPattern") }}</span>
            <input v-model="rewriteForm.matchPattern" placeholder="^legacy/(.*)$" />
          </label>

          <label>
            <span>{{ t("panel.proxy.rewriteUrl") }}</span>
            <input v-model="rewriteForm.rewriteUrl" placeholder="/archive/{R:1}" />
          </label>

          <div class="toggle-grid">
            <label class="checkbox">
              <input v-model="rewriteForm.stopProcessing" type="checkbox" />
              <span>{{ t("panel.proxy.stopProcessing") }}</span>
            </label>

            <label class="checkbox">
              <input v-model="rewriteForm.appendQueryString" type="checkbox" />
              <span>{{ t("panel.proxy.appendQueryString") }}</span>
            </label>

            <label class="checkbox">
              <input v-model="rewriteForm.ignoreCase" type="checkbox" />
              <span>{{ t("panel.proxy.ignoreCase") }}</span>
            </label>
          </div>

          <!-- Örnek değerler formdan çıkınca (bkz. REWRITE_FORM_BOS) üç alan da boş başlıyor;
               düğme artık BOŞ kural göndermiyor. Öncesinde bunu "dolu varsayılanlar" gizliyordu. -->
          <button
            v-bind="writeGate(!canWrite || busy || !rewriteFormDolu)"
            @click="saveRewriteRule">
            {{ proxyStore.rewritingSiteName === selectedSiteName ? t("panel.proxy.savingRule") : t("panel.proxy.saveRule") }}
          </button>

          <div v-if="currentConfiguration?.rewriteRules.length" class="rule-list">
            <article v-for="rule in currentConfiguration.rewriteRules" :key="rule.name" class="rule-item">
              <!-- `<strong>` -> `<h4>`: panelde bolum basligi olmayan SON yerdi. Her kural kendi
                   `<article>`i ve altinda bir `<dl>` tasiyor, yani gercekten bir basliktir; kalin
                   metin oldugu surece ekran okuyucunun baslik listesinde gorunmuyordu ve merdiven
                   h2 > h3'ten sonra govde metnine dusuyordu. Duzey h4: sayfa h2, "Yeniden yazma
                   kurallari" h3, her kural h4. -->
              <div class="section-head">
                <h4>{{ rule.name }}</h4>
                <span class="status-pill">{{ rule.stopProcessing ? t("panel.proxy.stopProcessing") : t("panel.proxy.passThrough") }}</span>
              </div>
              <dl class="detail-grid">
                <div>
                  <dt>{{ t("panel.proxy.matchPattern") }}</dt>
                  <dd>{{ rule.matchPattern }}</dd>
                </div>
                <div>
                  <dt>{{ t("panel.proxy.rewriteUrl") }}</dt>
                  <dd>{{ rule.rewriteUrl }}</dd>
                </div>
                <div>
                  <dt>{{ t("panel.proxy.appendQueryString") }}</dt>
                  <dd>{{ rule.appendQueryString ? t("panel.status.enabled") : t("panel.status.disabled") }}</dd>
                </div>
                <div>
                  <dt>{{ t("panel.proxy.ignoreCase") }}</dt>
                  <dd>{{ rule.ignoreCase ? t("panel.status.enabled") : t("panel.status.disabled") }}</dd>
                </div>
              </dl>
            </article>
          </div>

          <p v-else class="meta">
            {{ t("panel.proxy.noRules") }}
          </p>
        </section>
      </div>
    </template>

    <p v-else-if="!selectedSiteName" class="meta">
      {{ t("panel.proxy.selectSiteHint") }}
    </p>
  </section>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from "vue";
import { isCancel } from "axios";
import { useI18n } from "vue-i18n";
import { useProxyStore } from "../stores/proxy";
import { useSitesStore } from "../stores/sites";
import { useConfirm } from "../components/ConfirmDialog.vue";
import SectionLoadError from "../components/SectionLoadError.vue";
import { useNotice } from "../composables/useNotice";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";
import { useAuthStore } from "../stores/auth";
import { getSessionEpoch } from "../api/client";
import type { ReverseProxyConfiguration, SaveReverseProxyPayload, SaveRewriteRulePayload } from "../api/proxy";

const { t } = useI18n();
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri kapiya girmez.
const { writeGate, locked } = useSubscriptionGate();
const authStore = useAuthStore();
const proxyStore = useProxyStore();
const sitesStore = useSitesStore();
const { confirm } = useConfirm();
const { notice, noticeError, showNotice, resolveApiMessage } = useNotice();
const activeSection = ref<"proxy" | "rewrite">("proxy");
// Seçili sitenin yapılandırması OKUNAMADI mı. Yalnız "null" bakmak yetmiyordu: null hem
// "henüz gelmedi" hem "gelemedi" demekti ve ekran ikisini de "Yükleniyor…" diye çiziyordu.
const configFailed = ref(false);
const session = getSessionEpoch();
let disposed = false, generation = 0, readRequest = 0, proxyRevision = 0, rewriteRevision = 0;
const measured = ref(false), readLoading = ref(false), actionBusy = ref(false);
const isActive = () => !disposed && session === getSessionEpoch();
onBeforeUnmount(() => { disposed = true; generation++; readRequest++; });
const busy = computed(() => actionBusy.value || !!proxyStore.savingSiteName || !!proxyStore.removingSiteName || !!proxyStore.rewritingSiteName);
const canWrite = computed(() => isActive() && !locked.value && measured.value && proxyStore.measured[selectedSiteName.value] === true &&
  authStore.session?.isAuthenticated === true && ["Administrator", "Reseller"].includes(authStore.session.user?.role ?? ""));

const selectedSiteName = computed({
  get: () => sitesStore.selectedSiteName,
  set: (value: string) => sitesStore.selectSite(value)
});

const currentConfiguration = computed(() =>
  selectedSiteName.value ? proxyStore.getConfiguration(selectedSiteName.value) : null);

const proxyForm = reactive({
  destinationUrl: "",
  mountPath: "/",
  enableHtmlLinkRewrite: true
});
let cleanProxyDraft = JSON.stringify(proxyForm);
function context() {
  const site = selectedSiteName.value, version = generation, actor = authStore.session?.user?.id, role = authStore.session?.user?.role;
  return { site, current: () => isActive() && generation === version && selectedSiteName.value === site && authStore.session?.user?.id === actor && authStore.session?.user?.role === role };
}
function applyConfiguration(configuration: ReverseProxyConfiguration) {
  Object.assign(proxyForm, { destinationUrl: configuration.destinationUrl, mountPath: configuration.mountPath || "/", enableHtmlLinkRewrite: configuration.hasHtmlLinkRewrite });
  cleanProxyDraft = JSON.stringify(proxyForm);
}

/*
 * ÖRNEK DEĞERLER FORMDAN ÇIKARILDI — bu bir görünüm düzeltmesi değil, VERİ RİSKİYDİ.
 *
 * ÖLÇÜLDÜ: `matchPattern` ve `rewriteUrl` alanları GERÇEK başlangıç değeri taşıyordu
 * (`^legacy/(.*)$` ve `/archive/{R:1}`) ve aynı iki metin zaten `placeholder` olarak da
 * yazılıydı. Yani örnek İKİ KEZ vardı: biri gri ipucu (doğru yer), biri kutunun içinde gerçek
 * içerik (yanlış yer). Kullanıcı yalnız bir ad yazıp "Kuralı kaydet"e bastığında, sitesine hiç
 * istemediği bir yeniden yazma kuralı kurulurdu — `/legacy/...` isteklerini `/archive/...`'e
 * çeviren, çalışan bir kural. Onay diyaloğu da yok, çünkü kural EKLEMEK yıkıcı sayılmıyor.
 *
 * Boş başlangıç, örneği `placeholder`da bırakır: gri metin kaydedilmez.
 *
 * ANAHTARLAR (`stopProcessing`/`appendQueryString`/`ignoreCase`) BİLEREK `true` KALDI: onlar
 * örnek değil, IIS'in yeniden yazma kuralları için makul varsayılanları ve boş bırakılamıyorlar.
 */
const REWRITE_FORM_BOS = {
  name: "",
  matchPattern: "",
  rewriteUrl: "",
  stopProcessing: true,
  appendQueryString: true,
  ignoreCase: true
};

const rewriteForm = reactive({ ...REWRITE_FORM_BOS });

// Bir yeniden yazma kuralı ADSIZ, DESENSİZ ya da HEDEFSİZ anlamlı değildir; üçü de zorunlu.
const rewriteFormDolu = computed(() =>
  rewriteForm.name.trim().length > 0
  && rewriteForm.matchPattern.trim().length > 0
  && rewriteForm.rewriteUrl.trim().length > 0);

onMounted(async () => {
  try {
    if (sitesStore.sites.length === 0 && !sitesStore.loading) {
      await sitesStore.fetchSites();
    }
  } catch (error) {
    if (isActive() && !isCancel(error)) showNotice(resolveApiMessage(error), true);
  }
});

watch(proxyForm, () => { proxyRevision++; }, { deep: true, flush: "sync" });
watch(rewriteForm, () => { rewriteRevision++; }, { deep: true, flush: "sync" });
watch(selectedSiteName, site => {
  if (!isActive()) return;
  generation++; readRequest++; measured.value = false; readLoading.value = false; configFailed.value = false; notice.value = "";
  Object.assign(proxyForm, { destinationUrl: "", mountPath: "/", enableHtmlLinkRewrite: true });
  Object.assign(rewriteForm, REWRITE_FORM_BOS); cleanProxyDraft = JSON.stringify(proxyForm);
  const cached = proxyStore.getConfiguration(site); if (cached) applyConfiguration(cached);
  if (site) void loadConfiguration();
}, { immediate: true, flush: "sync" });

async function refreshConfiguration() {
  if (!isActive() || busy.value || readLoading.value || !selectedSiteName.value) return;
  await loadConfiguration(true);
}
async function loadConfiguration(announce = false) {
  const target = context(), request = ++readRequest, revision = proxyRevision;
  const clean = cleanProxyDraft === JSON.stringify(proxyForm), current = () => target.current() && request === readRequest;
  readLoading.value = true;
  try {
    const configuration = await proxyStore.fetchConfiguration(target.site);
    if (!current()) return;
    measured.value = true; configFailed.value = false;
    if (clean && proxyRevision === revision) applyConfiguration(configuration);
    if (announce) showNotice(t("panel.proxy.refreshed"), false);
  } catch (error) {
    if (current() && !isCancel(error)) { measured.value = false; configFailed.value = true; showNotice(resolveApiMessage(error), true); }
  } finally { if (current()) readLoading.value = false; }
}

function beginWrite() { actionBusy.value = true; readRequest++; readLoading.value = false; notice.value = ""; }
function unconfirmed(site: string) {
  measured.value = false; proxyStore.measured[site] = false; configFailed.value = true;
  showNotice(t("panel.proxy.resultUnconfirmed"), true);
}
function proxyMatches(configuration: ReverseProxyConfiguration, submitted: SaveReverseProxyPayload): boolean {
  try {
    const url = (value: string) => { const normalized = new URL(value.trim()); normalized.search = ""; normalized.hash = ""; return normalized.href.replace(/\/+$/, ""); };
    let mount = submitted.mountPath.trim() || "/"; if (!mount.startsWith("/")) mount = `/${mount}`; mount = mount.replace(/\/+$/, "") || "/";
    return configuration.isArrEnabled && configuration.isProxyEnabled && url(configuration.destinationUrl) === url(submitted.destinationUrl) &&
      configuration.mountPath === mount && configuration.hasHtmlLinkRewrite === (mount !== "/" && submitted.enableHtmlLinkRewrite);
  } catch { return false; }
}
function rewriteMatches(configuration: ReverseProxyConfiguration, submitted: SaveRewriteRulePayload) {
  return configuration.rewriteRules.some(rule => rule.name === submitted.name.trim() && rule.matchPattern === submitted.matchPattern.trim() && rule.rewriteUrl === submitted.rewriteUrl.trim() &&
    rule.stopProcessing === submitted.stopProcessing && rule.appendQueryString === submitted.appendQueryString && rule.ignoreCase === submitted.ignoreCase);
}
async function saveProxy() {
  if (!canWrite.value || busy.value || !proxyForm.destinationUrl.trim()) return;
  const target = context(), submitted = { ...proxyForm }, revision = proxyRevision; beginWrite();
  try {
    const result = await proxyStore.saveProxy(target.site, submitted);
    if (!target.current()) return;
    if (!proxyMatches(result, submitted)) { unconfirmed(target.site); return; }
    measured.value = true; configFailed.value = false;
    if (proxyRevision === revision) applyConfiguration(result);
    showNotice(t("proxy.proxy_enabled"), false);
  } catch (error) {
    if (target.current() && !isCancel(error)) { measured.value = false; configFailed.value = true; showNotice(resolveApiMessage(error), true); }
  } finally { actionBusy.value = false; }
}

async function removeProxy() {
  if (!canWrite.value || busy.value || !currentConfiguration.value?.isProxyEnabled) return;
  const target = context(), revision = proxyRevision, destination = currentConfiguration.value.destinationUrl; beginWrite();
  try {
    await confirm({
      title: t("panel.proxy.confirmRemoveTitle"),
      message: t("panel.proxy.confirmRemoveMessage", { name: target.site }),
      impact: [target.site, destination].filter(Boolean), confirmLabel: t("panel.proxy.remove"),
      action: async () => {
        if (!target.current() || !canWrite.value) return;
        try {
          const result = await proxyStore.removeProxy(target.site);
          if (!target.current()) return;
          if (result.isProxyEnabled || result.hasHtmlLinkRewrite || result.destinationUrl) { unconfirmed(target.site); return; }
          measured.value = true; configFailed.value = false;
          if (proxyRevision === revision) applyConfiguration(result);
          showNotice(t("proxy.proxy_removed"), false);
        } catch (error) {
          if (target.current() && !isCancel(error)) { measured.value = false; configFailed.value = true; showNotice(resolveApiMessage(error), true); }
        }
      }
    });
  } finally { actionBusy.value = false; }
}

async function saveRewriteRule() {
  if (!canWrite.value || busy.value || !rewriteFormDolu.value) return;
  const target = context(), submitted = { ...rewriteForm }, revision = rewriteRevision; beginWrite();
  try {
    const result = await proxyStore.saveRewriteRule(target.site, submitted);
    if (!target.current()) return;
    if (!rewriteMatches(result, submitted)) { unconfirmed(target.site); return; }
    measured.value = true; configFailed.value = false;
    if (rewriteRevision === revision) Object.assign(rewriteForm, REWRITE_FORM_BOS);
    showNotice(t("proxy.rewrite_rule_saved"), false);
  } catch (error) {
    if (target.current() && !isCancel(error)) { measured.value = false; configFailed.value = true; showNotice(resolveApiMessage(error), true); }
  } finally { actionBusy.value = false; }
}

</script>

<style scoped>
.proxy-notice { display: grid; grid-template-columns: minmax(0, 1fr) auto; align-items: start; gap: var(--sp-8); }
.proxy-notice p { margin: 0; white-space: pre-wrap; overflow-wrap: anywhere; }
.notice-close { width: 28px; height: 28px; padding: 0; color: inherit; }
.proxy-panel,
.proxy-columns,
.proxy-card,
.detail-grid,
.rule-list,
.toggle-grid {
  display: grid;
  gap: var(--sp-12);
}

.action-row {
  display: flex;
  gap: var(--sp-12);
  align-items: center;
  flex-wrap: wrap;
}

/* TEK SUTUN — olculdu, iki sutun BOS YARIM ekran demekti.
   `.proxy-columns` iki sutunlu bir izgaraydi ama icindeki iki bolum `v-if="activeSection === …"`
   ile birbirini DISLIYOR: her an yalnizca BIRI cizilir. Sonuc, kartin ekranin solunda yarim
   genislikte durup sag yarinin bos kalmasiydi (1440px'te ~700px bos). Sekmeli bir yerlesimde
   dogru olan, gorunen tek karta tam genisligi vermektir. */
.proxy-columns {
  grid-template-columns: minmax(0, 1fr);
}

.detail-grid {
  grid-template-columns: repeat(2, minmax(0, 1fr));
  margin: 0;
}

.proxy-card,
.rule-item {
  display: grid;
  gap: var(--sp-12);
  padding: var(--sp-12);
  border-radius: var(--radius-md);
  border: 1px solid var(--panel-border);
  /* Iki durakli "gradyan" iki durakta da AYNI rengi tasiyordu (`surface-2` -> `surface-2`),
     yani duz bir dolgunun pahali yazilisiydi.
     `--surface-2` -> `--surface-1`: alti ekranin ic kart recetesi tek zemine baglandi
     (bkz. ServerHealthView'deki sayim; surface-1 uc ekranda zaten kullaniliyordu ve
     `--surface-0` olan `.panel` ile arasinda iki temada da gorunur bir adim var). */
  background: var(--surface-1);
}

/* `.meta` KOPYASI SILINDI: rengi base.css zaten veriyor (`.meta { color: var(--text-muted) }`).
   Burada kalan tek is paragrafin varsayilan dis boslugunu sifirlamak -- o base'de YOK ve
   silinirse "kural yok" cumlesi kartin icinde asagi kayardi. */
.detail-grid dt {
  color: var(--text-muted);
  font-size: var(--fs-xs);
}

.detail-grid div {
  display: grid;
  gap: var(--sp-4);
  min-width: 0;
}

.detail-grid dd,
.meta {
  margin: 0;
  word-break: break-word;
}

.detail-grid dd {
  color: var(--text-soft);
}

/* YIKICI EYLEM AYRI DURUR.
   Eskiden `flex: 1 1 0` ikisini de esit genislige geriyordu: "Kaydet" ile sitenin yonlendirmesini
   KESEN "Kaldir" ayni boyda, yan yana, ayni hizada duruyordu -- refleksle yanlisina basilacak
   bir dizilim. Artik kaydet solda kendi boyunda, kaldir `margin-left: auto` ile satirin karsi
   ucunda: konumu sabit ve arasinda gorunur bir bosluk var. */
.action-row .danger {
  margin-left: auto;
}

.toggle-grid {
  grid-template-columns: repeat(3, minmax(0, 1fr));
}

/* Izgara cocugu varsayilan olarak GERILIR (`justify-self: stretch`): tek basina duran
   "Kurali kaydet" dugmesi kartin tamamini kaplardi. Panelde dugme icerigi kadar genistir. */
.proxy-card > button {
  justify-self: start;
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
  /* --sp-8 -> --sp-6: `.form-grid label` ve `.stacked-field` (base.css) etiket-alan araligini
     6px veriyor. Bu ekran tek basina 8px kullandigi icin yan yana acilan iki sekmenin form
     ritmi birbirini tutmuyordu. */
  gap: var(--sp-6);
}

/*
 * BURADA ESKIDEN PANELIN GLOBAL FORM KURALLARINI EZEN BIR BLOK VARDI; SILINDI.
 *
 * Yazan sey suydu:
 *   input, textarea, select, button { width: 100%; … }
 *   input, textarea, select { border: 1px solid var(--panel-border); padding: 12px 14px; … }
 *
 * Uc olculmus zarari vardi:
 *  1) YUKSEKLIK: 12px dikey dolgu + 2px kenar + 19.575px satir = 43.6px. Panelin her yerinde
 *     alan yuksekligi 34px (base.css `input { min-height: 34px; padding: var(--sp-6) var(--sp-10) }`).
 *     Yani bu ekranin form alanlari komsu ekranlardan ~10px UZUNDU -- "her yer birbirine girdi"
 *     hissinin en olculebilir kaynagi.
 *  2) KENAR RENGI: `--panel-border` girdi zemininde 1.41:1 olculuyor; base.css tam bu yuzden ayri
 *     bir `--input-border` (3.02:1) belirteci tanimlar ve gerekcesini yazar. Bu blok o duzeltmeyi
 *     TEK EKRANDA geri aliyordu.
 *  3) `button { width: 100% }`: kapsamli oldugu icin (0,1,1) base.css'i eziyordu ve her dugmeyi
 *     kapsayicisi kadar geniyordu; ayrica base.css'teki `label.checkbox input` kuralinin yorumu
 *     bu satiri ISIMLE anmis ("ProxyView … input{width:100%} yaziyor") -- yani onay kutusu tuzagi
 *     da buradan besleniyordu. Kaynagi silmek, kacis kurallarina olan ihtiyaci da siliyor.
 */

@media (max-width: 900px) {
  .detail-grid,
  .toggle-grid {
    grid-template-columns: 1fr;
  }
}

/* 760 -> 720: base.css'in tek sutuna dustugu esik (ve `.panel` dolgusunun daraldigi nokta).
   Iki ayri esik, 720-760 arasinda yarim uygulanmis bir yerlesim uretiyordu. */
@media (max-width: 720px) {
  .segmented-tabs {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    width: 100%;
  }

  .segmented-tabs button {
    min-width: 0;
    white-space: normal;
  }

  .action-row {
    flex-direction: column;
    align-items: stretch;
  }

  /* Tek sutuna dusen satirda "auto" bosluk anlamsiz: dugmeler alt alta, tam genislikte. */
  .action-row .danger {
    margin-left: 0;
  }
}
</style>
