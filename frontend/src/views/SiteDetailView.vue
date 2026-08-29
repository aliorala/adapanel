<template>
  <section class="detail-card">
    <template v-if="site">
      <!-- Ortak `.section-head` devralindi: kapsamli `.detail-top` kopyasi ayni uc ozelligi
           yaziyordu, tek eksigi `flex-wrap` idi — dar panelde dort eylem dugmesi baslikla ayni
           satirda sikisiyordu.
           ALAN ADI TEKRARI KALDIRILDI: ust baglam cubugu (WebsiteWorkspaceView `.context-domain`)
           zaten alan adini bu blogun ~24px ustunde ve DAHA BUYUK yaziyor; ayni ad ayni ekranda
           iki kez duruyordu. Bilgi kaybolmuyor, TEKRAR kalkiyor. Yerine DURUM geldi (asagida). -->
      <div class="section-head">
        <div class="detail-heading">
          <h3>{{ t("panel.sites.detailTitle") }}</h3>
          <!-- DURUM ARTIK BURADA, tanim listesinin bir hucresinde degil: bir sitede en cok
               bakilan bilgi calisip calismadigidir ve baslikla ayni hizada olmali. Rozet
               RENGI TEK KANAL DEGIL — durumun cevrilmis ADINI da yazar, yani renk koru
               kullanici da okur. -->
          <span :class="['status-pill', isStarted ? 'ok' : 'warn']">{{ translateState(site.state) }}</span>
        </div>

        <!-- "Ac" dugmesi KALDIRILDI: ayni islevi ust baglam cubugundaki "Siteyi ac" baglantisi
             goruyor ve o baglanti BUTUN sekmelerde duruyor (bu dugme yalniz Pano'da). Ayrica
             baglanti gercek bir `<a>`: orta tikla yeni sekme, sag tikla adresi kopyala. Adres
             hesabindaki tek fark (onizleme host'unun atlanmasi) o ekranda duzeltildi. -->
        <div class="row-actions detail-actions">
          <button
            class="secondary compact"
            v-bind="writeGate(busySiteName === site.name || isStopped)"
            @click="$emit('start', site.name)">
            {{ t("panel.sites.start") }}
          </button>
          <button
            class="secondary compact"
            v-bind="writeGate(busySiteName === site.name || isStarted)"
            @click="$emit('stop', site.name)">
            {{ t("panel.sites.stop") }}
          </button>
          <!-- YIKICI EYLEM GORSEL OLARAK AYRI: "Sil" ile "Durdur" arasinda hicbir sinir yoktu,
               ikisi ayni boyda komsuydu. Ayrac, silmeyi seridin sonunda kendi bolgesinde tutar. -->
          <span class="action-divider" aria-hidden="true"></span>
          <button
            class="danger compact"
            v-bind="writeGate(busySiteName === site.name)"
            @click="$emit('delete', site.name)">
            {{ t("panel.sites.delete") }}
          </button>
        </div>
      </div>

      <!--
        BEŞ AYRI KONU, BEŞ AYRI BÖLÜM.

        ÖLÇÜLDÜ: künye, önizleme, araç ızgarası, alt alan adı formu ve adres bağlamaları TEK
        kutunun içinde, aralarında hiçbir ayraç olmadan alt alta duruyordu; alt başlıklar da
        gövde metniyle aynı ağırlıktaydı (`h4`nün boy kuralı base.css'te düzeltildi, ama
        aradaki SINIR yoktu). Göz nerede bir konunun bitip diğerinin başladığını göremiyordu.

        Her blok artık `.detail-section`; komşusundan bir üst çizgiyle ayrılıyor. Kutu
        sayısı ARTMIYOR (yeni kart çerçevesi yok), yalnız içerideki sınırlar görünür oldu.
      -->
      <section class="detail-section">
        <dl class="detail-grid">
          <div>
            <dt>{{ t("panel.sites.appPool") }}</dt>
            <dd>{{ site.applicationPoolName || "-" }}</dd>
          </div>
          <div>
            <dt>{{ t("panel.sites.physicalPath") }}</dt>
            <dd>{{ site.physicalPath }}</dd>
          </div>
          <div>
            <dt>{{ t("panel.sites.autoStart") }}</dt>
            <dd>{{ site.autoStart ? t("panel.status.enabled") : t("panel.status.disabled") }}</dd>
          </div>
        </dl>

        <!-- Domainsiz önizleme: iframe çoğu sitede (X-Frame-Options/CSP) engellendiğinden nip.io linkini yeni sekmede açtırırız. -->
        <!-- ÇERÇEVE KALKTI. Şerit tek bir düğme için kendi kenarlığı ve zemini olan bir kutu
             çiziyordu: içi neredeyse boş, ağırlığı ise araç ızgarasıyla aynı. Önizleme sitenin
             KÜNYESİNE ait ikincil bir eylemdir — künyenin altında, çerçevesiz bir satır olarak
             duruyor. Hiçbir düğme, bağlantı ya da hata satırı kaldırılmadı. -->
        <div v-if="primaryUrl" class="preview-strip">
          <span class="preview-label">{{ t("panel.sites.previewTitle") }}</span>
          <button class="ghost compact" type="button" :disabled="previewLoading" @click="loadPreview">
            {{ previewLoading ? t("panel.common.loading") : t("panel.sites.preview") }}
          </button>

          <template v-if="preview">
            <a class="preview-url" :href="preview.url" target="_blank" rel="noopener" :title="preview.url">
              {{ preview.url }} ↗
            </a>
            <button
              v-if="preview.hostsLine"
              class="ghost compact"
              type="button"
              :title="preview.hostsLine"
              @click="copyHostsLine">
              {{ hostsCopied ? t("panel.common.copied") : t("panel.sites.previewHostsHint") }}
            </button>
          </template>

          <span v-if="previewLoad.message.value" class="preview-error" role="alert">{{ previewLoad.message.value }}</span>
        </div>
      </section>

      <!-- profesyonel panel benzeri "domain araçları" ızgarası: özet altında tek tık kısayollar. -->
      <section class="detail-section tools-block">
        <div class="section-head">
          <h4>{{ t("panel.sites.toolsTitle") }}</h4>
        </div>
        <div class="tools-grid">
          <button
            v-for="tool in siteTools"
            :key="tool.id"
            type="button"
            class="tool-card"
            @click="$emit('open-tool', tool.id)">
            <!-- Harf damgasi (8.91px, "KOR"/"BK"/"IST" gibi uydurulmus kisaltmalar) yerine ikon.
                 Tam ad ZATEN altta yaziyor (`.tool-card-label`), yani kisaltmanin tasidigi bilgi
                 sifirdi; ikon o yeri anlamli dolduruyor. -->
            <svg class="tool-card-icon" viewBox="0 0 24 24" aria-hidden="true" v-html="siteToolIcon(tool.id)"></svg>
            <span class="tool-card-label">{{ tool.label }}</span>
          </button>
        </div>
      </section>

      <!-- Alt alan adı: <etiket>.<alanadı> için yeni yönetilen site + üst bölgeye A kaydı.
           ARTIK YALNIZ ADMIN DEĞİL. Kısıt "bayi/müşteri site oluşturamaz" gerekçesiyle konmuştu ama
           bu doğru değildi: site oluşturma ucu Customer ve Reseller'a da açık ve kota orada zaten
           uygulanıyor. Asıl risk başkasının alan adının altına girmekti; o, alan adı iddia
           denetiminde kapatıldı (bkz. CustomerAccess.EnsureDomainClaimAsync). Site sahibi kendi
           alan adının altına alt alan adı açabilir. -->
      <section class="detail-section subdomain-block">
        <div class="section-head">
          <h4>{{ t("panel.sites.subdomainTitle") }}</h4>
        </div>
        <form class="subdomain-form" @submit.prevent="submitSubdomain">
          <div class="subdomain-input">
            <input v-model.trim="subdomainLabel" placeholder="blog" pattern="[a-zA-Z0-9-]+" />
            <span>.{{ site.name }}</span>
          </div>
          <button type="submit" class="compact" v-bind="writeGate(!subdomainLabel || busySiteName === site.name)">
            {{ t("panel.sites.subdomainAdd") }}
          </button>
        </form>
        <!-- İki şeyi ÖNDEN söylüyoruz: alt alan adı ayrı bir site sayılır (kotadan düşer) ve
             DNS kaydı ancak üst alan adının bölgesi panelde yönetiliyorsa atılabilir. -->
        <small class="field-hint">{{ t("panel.sites.subdomainHint") }}</small>
      </section>

      <section class="detail-section binding-block">
        <div class="section-head">
          <h4>{{ t("panel.sites.bindings") }}</h4>
          <button class="secondary compact" type="button" @click="showBindingForm = !showBindingForm">
            {{ showBindingForm ? t("panel.common.cancel") : t("panel.sites.addBinding") }}
          </button>
        </div>

        <!-- Onizleme host'u (nip.io) LISTELENMEZ: kullanicinin sahip oldugu bir alan adi degildir,
             yalnizca alan adi yonlendirilmeden siteyi acmak icin uretilir. Listede gercek bir
             baglama gibi durup yer kapliyordu. -->
        <div v-if="gercekBaglamalar.length > 0" class="binding-list">
          <article v-for="binding in gercekBaglamalar" :key="binding.bindingInformation" class="binding-item">
            <div>
              <strong>{{ binding.protocol.toUpperCase() }}</strong>
              <p class="meta">{{ binding.bindingInformation }}</p>
            </div>

            <!--
              YIKICI EYLEM, EKLEMEDEN DAHA SÖNÜK.

              ÖLÇÜLDÜ: "Binding kaldır" ile "Binding ekle" aynı `secondary` görünümdeydi ama
              kaldırma her satırda TEKRARLANDIĞI için ekranda toplam ağırlığı daha büyüktü:
              yıkıcı eylem, ekleyen eylemden daha çok göze çarpıyordu. Kaldırma artık `ghost`
              (çerçevesiz, sessiz); onay diyaloğu ve `writeGate` AYNEN duruyor, yani güvenlik
              görünümden değil kapıdan geliyor. Etiket de kısaldı ("Kaldır"): hangi bağlamayı
              kaldırdığı zaten bulunduğu satırdan belli.
            -->
            <button
              class="ghost compact"
              v-bind="writeGate(busyBindingKey === toBindingKey(binding))"
              @click="emitRemoveBinding(binding)">
              {{ t("panel.sites.removeBinding") }}
            </button>
          </article>
        </div>

        <p v-else class="meta">
          {{ t("panel.sites.bindingNone") }}
        </p>

        <!-- Form BÖLÜMÜN İÇİNE alındı: eskiden `.binding-block`in kardeşiydi ve kartın en
             altında, listeden kopuk bir yerde açılıyordu. -->
        <form v-if="showBindingForm" class="binding-form" @submit.prevent="submitBinding">
          <label>
            <span>{{ t("panel.sites.protocol") }}</span>
            <select v-model="bindingForm.protocol">
              <option value="http">{{ t("panel.sites.http") }}</option>
            </select>
          </label>

          <label>
            <span>{{ t("panel.sites.hostName") }}</span>
            <input v-model="bindingForm.hostName" />
          </label>

          <label>
            <span>{{ t("panel.sites.ipAddress") }}</span>
            <select v-model="bindingForm.ipAddress">
              <option value="*">{{ t("panel.sites.allIps") }}</option>
              <option v-for="ip in serverIps" :key="ip.address" :value="ip.address">{{ ip.address }} ({{ ip.family }})</option>
            </select>
          </label>

          <label>
            <span>{{ t("panel.sites.port") }}</span>
            <input v-model.number="bindingForm.port" type="number" min="1" max="65535" required />
          </label>

          <!-- `compact` EKLENDI: tam boy bir birincil dugme, ustundeki alt alan adi formunun
               gonder dugmesinden daha agir goruniyordu; ayni rutbedeki iki eylem ayni boyda. -->
          <button type="submit" class="compact" v-bind="writeGate(busySiteName === site.name)">
            {{ t("panel.sites.addBinding") }}
          </button>
        </form>
      </section>
    </template>

    <p v-else class="meta">
      {{ t("panel.sites.detailEmpty") }}
    </p>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import type { SiteBinding, SiteBindingPayload, SiteDefaultsResult, SiteRecord, SitePreview } from "../api/sites";
import { getSitePreview } from "../api/sites";
import { getServerIpAddresses, type ServerIpAddress } from "../api/system";
import { useAuthStore } from "../stores/auth";
import { useSectionError } from "../composables/useNotice";
import { copyToClipboard } from "../utils/clipboard";
import { siteToolIcon } from "../utils/siteToolIcons";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";

// Site oluşturma içeren işlemler (subdomain) + server-level araçlar yalnız admin'e gösterilir.
const isAdmin = computed(() => useAuthStore().session?.user?.role === "Administrator");

const props = defineProps<{
  site: SiteRecord | null;
  busySiteName: string;
  busyBindingKey: string;
  defaultsReport: SiteDefaultsResult | null;
}>();

const emit = defineEmits<{
  (event: "start", siteName: string): void;
  (event: "stop", siteName: string): void;
  (event: "delete", siteName: string): void;
  (event: "prepare-defaults", siteName: string): void;
  (event: "open-tool", tool: string): void;
  (event: "add-subdomain", label: string): void;
  (event: "add-binding", payload: { siteName: string; binding: SiteBindingPayload }): void;
  (event: "remove-binding", payload: { siteName: string; binding: SiteBindingPayload }): void;
}>();
const { t } = useI18n();
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri kapiya girmez.
const { writeGate } = useSubscriptionGate();

// panel "Websites & Domains" domain araç kısayolları.
const siteTools = computed(() => [
  { id: "hosting", label: t("panel.sites.tools.hosting") },
  { id: "files", label: t("panel.sites.tools.files") },
  { id: "php", label: t("panel.sites.tools.php") },
  { id: "asp", label: t("panel.sites.tools.asp") },
  { id: "mail", label: t("panel.sites.tools.mail") },
  { id: "ssl", label: t("panel.sites.tools.ssl") },
  // Veritabanları (admin-only): site iş alanında DB ekle/sil + phpMyAdmin (SiteDatabasesView).
  ...(isAdmin.value ? [{ id: "webdb", label: t("panel.sites.tools.domaindb") }] : []),
  { id: "ftp", label: t("panel.sites.tools.ftp") },
  { id: "dns", label: t("panel.sites.tools.dns") },
  { id: "stats", label: t("panel.sites.tools.stats") },
  { id: "wp", label: "WordPress" },
  { id: "protected", label: t("panel.sites.tools.protected") },
  { id: "backup", label: t("panel.sites.tools.backup") },
  { id: "apps", label: t("panel.sites.tools.apps") },
  // yazılım mağazası server-level (admin-only).
  ...(isAdmin.value ? [{ id: "store", label: t("panel.sites.tools.store") }] : [])
]);

const bindingForm = reactive<SiteBindingPayload>({
  protocol: "http",
  hostName: "",
  ipAddress: "*",
  port: 80
});

const subdomainLabel = ref("");
const showBindingForm = ref(false);
const preview = ref<SitePreview | null>(null);
const previewLoading = ref(false);
// Önizleme hatası ÇEVRİLİR: burada `t()` yoktu, kullanıcı `sites.preview_failed` gibi ham
// anahtar görüyordu. `useSectionError` çeviriyi ve "okunamadı" durumunu tek yerde tutar.
const previewLoad = useSectionError();
async function loadPreview() {
  if (!props.site) {
    return;
  }
  previewLoading.value = true;
  preview.value = null;
  previewLoad.clear();
  try {
    preview.value = await getSitePreview(props.site.name);
  } catch (error) {
    previewLoad.capture(error);
  } finally {
    previewLoading.value = false;
  }
}

// Çoklu IP (panel dedicated IP): binding'de seçilebilecek sunucu IP'leri.
const serverIps = ref<ServerIpAddress[]>([]);
onMounted(async () => {
  serverIps.value = await getServerIpAddresses().catch(() => []);
});

function submitSubdomain() {
  const label = subdomainLabel.value.trim().toLowerCase();
  if (!label) {
    return;
  }
  emit("add-subdomain", label);
  subdomainLabel.value = "";
}

const isStarted = computed(() => props.site?.state.toLowerCase() === "started");
const isStopped = computed(() => props.site?.state.toLowerCase() === "stopped");
const hostsCopied = ref(false);

// hosts satiri uzun ve teknik: ekrani doldurmasi yerine tek tikla panoya alinir.
async function copyHostsLine() {
  if (!preview.value?.hostsLine) return;

  // Ortak yardımcı güvenli olmayan bağlamda da kopyalar; olmadıysa "kopyalandı" DENMEZ.
  if (!(await copyToClipboard(preview.value.hostsLine))) {
    hostsCopied.value = false;
    return;
  }

  hostsCopied.value = true;
  setTimeout(() => (hostsCopied.value = false), 2000);
}

const gercekBaglamalar = computed(() =>
  (props.site?.bindings ?? []).filter(
    (binding) => !binding.hostName || !binding.hostName.toLowerCase().endsWith(".nip.io")
  )
);

/**
 * Onizleme seridinin gorunup gorunmeyecegini belirler (site bir adrese baglanmis mi).
 * Adresi ACMA isi ust baglam cubugundaki baglantiya devredildi; buradaki `openSite`
 * sarmalayicisi o dugmeyle birlikte kalkti.
 */
const primaryUrl = computed(() => {
  // nip.io önizleme bağlamalarını ATLA → asıl domain gösterilsin/açılsın.
  const binding = props.site?.bindings.find((item) => item.hostName && !item.hostName.toLowerCase().endsWith(".nip.io"))
    ?? props.site?.bindings[0];
  if (!binding) {
    return "";
  }

  const host = binding.hostName || (binding.ipAddress === "*" ? "localhost" : binding.ipAddress);
  const defaultPort = (binding.protocol === "http" && binding.port === 80)
    || (binding.protocol === "https" && binding.port === 443);
  return `${binding.protocol}://${host}${defaultPort ? "" : `:${binding.port}`}`;
});

function submitBinding() {
  if (!props.site) {
    return;
  }

  emit("add-binding", {
    siteName: props.site.name,
    binding: { ...bindingForm }
  });

  bindingForm.hostName = "";
  bindingForm.ipAddress = "*";
  bindingForm.port = 80;
  showBindingForm.value = false;
}

function emitRemoveBinding(binding: SiteBinding) {
  if (!props.site) {
    return;
  }

  emit("remove-binding", {
    siteName: props.site.name,
    binding: {
      protocol: binding.protocol,
      hostName: binding.hostName,
      ipAddress: binding.ipAddress,
      port: binding.port
    }
  });
}

function toBindingKey(binding: SiteBinding): string {
  return `${binding.protocol}|${binding.ipAddress}|${binding.port}|${binding.hostName}`;
}

function translateState(state: string): string {
  const key = `panel.sites.runtime.${state.toLowerCase()}`;
  const translated = t(key);
  return translated === key ? state : translated;
}
</script>

<style scoped>
/*
 * BU DOSYADAN SILINEN KOPYALAR — hepsi base.css'te zaten vardi ve buradaki kapsamli surumler
 * [data-v] niteligi yuzunden DAHA YUKSEK ozgullukte oldugu icin ortak kurali EZIYORDU:
 *
 *  - `button:disabled { opacity: 0.65 }`  -> base.css bu degeri OLCEREK kaldirmisti (0.65 ->
 *    3.15:1; opaklik metni ve dolguyu ayni anda soldurdugu icin artirmak da islemiyor). Ortak
 *    kural duz gri yuzey + `--text-muted` kullanir: 6.00:1 (koyu) / 5.24:1 (acik). Bu kopya o
 *    duzeltmeyi YALNIZ BU EKRANDA geri aliyordu.
 *  - `input, select { border: 1px solid var(--panel-border) }` -> ortak kural `--input-border`
 *    kullanir. OLCULDU: `--panel-border` girdi zemininde 1.41:1, `--input-border` 3.02:1
 *    (WCAG 1.4.11 esigi 3:1). Bu ekranin form alanlarinin cercevesi pratikte gorunmuyordu.
 *  - `input, select { padding: 12px 14px }` -> ortak dolgu --sp-6/--sp-10. Fark olculdu:
 *    alanlar bu ekranda ~12px daha uzundu, yani ayni form panelin baska yerinde baska boyda.
 *  - `label:not(:where(...))`, `input/select { width: 100% }`, `button { border-radius; font }`,
 *    `button.compact { padding }` -> base.css'te birebir ayni tanimlar.
 *  - `.defaults-block / .defaults-grid / .default-chip*` -> OLU: bu sablonda hic kullanilmiyor.
 *    Varsayilan hazirlama raporu SitesView'e tasinmis, kurallari burada unutulmustu.
 */

/* `.binding-block` bu listeden CIKARILDI: artik `.detail-section` tasiyor ve o kural ayni iki
   ozelligi (grid + aralik) daha sonra, farkli bir degerle yaziyordu — yani iki kural cakisiyor,
   kazanani kaynak sirasi belirliyordu. Tek kaynak: `.detail-section`. */
.detail-card,
.detail-grid,
.binding-form,
.binding-list {
  display: grid;
  gap: var(--sp-12);
}

.detail-card {
  padding: var(--sp-16);
  border-radius: var(--radius-lg);
  border: 1px solid var(--panel-border);
  /* Iki durgu da AYNI renkti, yani gradyan hicbir sey cizmiyordu. */
  background: var(--surface-2);
}

/* BÖLÜM AYRACI. Beş konu tek kutuda alt alta duruyordu ve aralarında hiçbir sınır yoktu;
   göz künyenin nerede bitip araçların nerede başladığını ancak metni okuyarak anlıyordu.
   Ayrac YENİ BİR KART DEĞİL (kart içinde kart, gürültü olurdu): komşu bölümler arasına
   tek bir üst çizgi. İLK bölüm çizgi almaz -- `+` bileşicisi tam olarak bunu sağlar. */
.detail-section {
  display: grid;
  gap: var(--sp-10);
}

.detail-section + .detail-section {
  padding-top: var(--sp-12);
  border-top: 1px solid var(--panel-border);
}

/* Baslik + durum rozeti yan yana; rozet basligin ALTINA degil YANINA gelir. */
.detail-heading {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--sp-8);
  min-width: 0;
}

/* YIKICI BOLGE AYRACI: eylem seridinde silmeyi digerlerinden ayiran gorsel sinir. */
.action-divider {
  align-self: stretch;
  width: 1px;
  margin: 0 var(--sp-4);
  background: var(--panel-border);
}

/* Durum satiri kalktigi icin geriye UC alan kaldi: sabit iki sutunda biri tek basina bir
   satirda kaliyordu. `auto-fit` genis ekranda ucunu de yan yana dizer, daraldikca ikiye ve
   bire duser — ayri bir kirilim noktasi gerekmez. */
.detail-grid {
  grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
  margin: 0;
}

.detail-grid div {
  display: grid;
  gap: var(--sp-4);
  min-width: 0;
}

.detail-grid dt,
.meta {
  color: var(--text-muted);
}

.detail-grid dd,
.meta {
  margin: 0;
}

/* Fiziksel yol (C:\inetpub\wwwroot\...) bosluksuz tek parca: kirilacak yeri olmadigi icin
   hucreden TASIYOR ve karti yatayda buyutuyordu. */
.detail-grid dd {
  overflow-wrap: anywhere;
}

/* `.tools-block, .subdomain-block { display: grid; gap }` KALDIRILDI: ikisi de artik
   `.detail-section` ve o kural AYNI iki ozelligi zaten veriyor. Iki yerde duran ayni tanim,
   birini degistirip digerini unutmanin davetiyesidir. */

/* Onizleme SERIDI: ikincil bir eylem oldugu icin tek satirda, sessiz bir yuzeyde durur.
   Kendi basligi ve kart cercevesi yok -- sayfadaki asil bilgiyle gorsel olarak yarismamali. */
/* ÇERÇEVE VE ZEMİN KALDIRILDI. Şerit tek bir düğme için kendi kutusunu çiziyordu; içi
   neredeyse boş olduğu hâlde araç ızgarasıyla aynı görsel ağırlığı taşıyordu. Artık
   künyenin altında çerçevesiz bir satır: aynı içerik, kutu gürültüsü yok. */
.preview-strip {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--sp-8);
}

/* Ortak ustbaslik dilinin (`.tile-label`) ayarlari: kucuk, buyuk harfli, sessiz. `opacity`
   YERINE dogrudan `--text-muted`: saydamlik metni ZEMINE dogru soldurur ve olculebilir bir
   kontrast degeri birakmaz (0.65 opaklikta gri metin ~2.9:1'e dusuyordu). */
.preview-label {
  color: var(--text-muted);
  font-size: var(--fs-2xs);
  letter-spacing: 0.035em;
  text-transform: uppercase;
}

/* Adres uzun olabilir: seridi bozmasin diye buyuyup kirpilir, tamami baslikta (title) durur. */
.preview-strip .preview-url {
  flex: 1 1 220px;
  min-width: 0;
  font-size: var(--fs-xs);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

/* `--danger-1` DOLGU rengi; METIN icin `--danger-text` var (base.css'te olculdu). */
.preview-error {
  flex-basis: 100%;
  font-size: var(--fs-xs);
  color: var(--danger-text);
}

.subdomain-form {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--sp-10);
}

.subdomain-input {
  display: inline-flex;
  align-items: center;
  gap: var(--sp-6);
}

.subdomain-input input {
  width: 160px;
}

.subdomain-input span {
  color: var(--text-muted);
  font-size: var(--fs-sm);
}

.tools-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(116px, 1fr));
  gap: var(--sp-8);
}

.tool-card {
  display: grid;
  justify-items: center;
  align-content: center;
  gap: var(--sp-8);
  min-height: 78px;
  padding: var(--sp-12) var(--sp-8);
  border-radius: var(--radius-md);
  border: 1px solid var(--panel-border);
  background: var(--surface-1);
  color: var(--text-strong);
  cursor: pointer;
  transition: border-color 0.15s ease, transform 0.05s ease;
}

.tool-card:hover {
  border-color: color-mix(in srgb, var(--accent-1) 45%, transparent);
}

.tool-card:active {
  transform: translateY(1px);
}

/* IKON KARESI. Kutu OLCULERI degismedi (34px kare, ayni yuvarlaklik, ayni %14 vurgu
   dolgusu): kartin gorsel capasi ayni yerde duruyor, degisen yalnizca icindekinin ne
   OLDUGU -- 8.91px'lik iki-uc harflik bir damga yerine 20px'lik bir cizgi ikon.
   Dolgu `padding` ile veriliyor: `viewBox`li bir SVG icerik kutusuna sigar, yani
   34 - 2x7 = 20px etkin ikon boyu. Cizgi kalinligi (1.7) ve ucu panelin ortak ikon
   dilinden (`MainLayout` > `.nav-icon`). */
.tool-card-icon {
  box-sizing: border-box;
  width: 34px;
  height: 34px;
  padding: 7px;
  border-radius: var(--radius-md);
  background: color-mix(in srgb, var(--accent-1) 14%, transparent);
  fill: none;
  stroke: var(--accent-1);
  stroke-width: 1.7;
  stroke-linecap: round;
  stroke-linejoin: round;
}

.tool-card-label {
  font-size: var(--fs-2xs);
  text-align: center;
}

/* Baglama satiri artik KART: alt-cizgili bir serit degil, kendi cercevesi olan bir satir.
   Zemin `rgba(12, 16, 24, 0.4)` idi — koda GOMULU koyu bir renk; acik temada beyaz kartin
   uzerine neredeyse siyah bir bant ciziyordu (tema degiskenlerine bagli olmadigi icin). */
.binding-item {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: var(--sp-12);
  padding: var(--sp-8) var(--sp-10);
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--surface-1);
}

.binding-form {
  grid-template-columns: repeat(4, minmax(0, 1fr));
  align-items: end;
}

/* Bu formun etiketleri (protokol/host/IP/port) sinif tasimiyor; ortak kural `.form-grid label`
   ile kapili oldugu icin buradaki dort etiketin izgara duzeni yerelde verilir. Onay kutusu
   yok, dolayisiyla istisna listesine (ve ozgulluk tuzagina) gerek kalmiyor. */
.binding-form label {
  display: grid;
  gap: var(--sp-6);
}

/* Kirilim noktalari 960/760 -> ortak esikler (900 ve 720): bu ekran kendi noktalarinda
   yerlesim degistiriyor, panelin geri kalani baska noktalarda degistiriyordu. */
@media (max-width: 900px) {
  .binding-form {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 720px) {
  .detail-actions,
  .binding-item {
    flex-direction: column;
    align-items: flex-start;
  }

  /* Dikey yiginda 1px'lik dikey cizgi gorunmez olur; ayrac yatay bir kurala doner ve
     "Sil" yine kendi bolgesinde kalir. */
  .action-divider {
    align-self: stretch;
    width: auto;
    height: 1px;
    margin: var(--sp-4) 0;
  }
}
</style>
