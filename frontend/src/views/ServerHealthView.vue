<template>
  <section class="panel module-shell server-health">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.health.title") }}</h3>
        <p class="meta">{{ t("panel.health.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading" @click="load">{{ t("panel.health.refresh") }}</button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <SectionLoadError v-if="dnsLoad.message.value" :message="dnsLoad.message.value" @retry="load" />

    <!-- DNS erişilebilirliği: servisin "Running" olması YETMEZ, 53 portunun genel IP'de
         dinlemesi gerekir. Bu kart o farkı gösterir ve tek tıkla onarır. -->
    <article v-if="dns && dns.installed" :class="['dns-card', dns.needsRepair ? 'bad' : 'good']">
      <div class="dns-head">
        <span :class="['state-dot', dns.needsRepair ? 'dot-danger' : 'dot-ok']"></span>
        <div class="health-name">
          <strong>{{ t("panel.health.dnsTitle") }}</strong>
          <span class="svc-id">{{ dns.needsRepair ? t("panel.health.dnsBad") : t("panel.health.dnsGood") }}</span>
        </div>
        <span :class="['status-pill', dns.needsRepair ? 'danger' : 'ok']">
          {{ dns.publiclyBound ? t("panel.health.dnsBound") : t("panel.health.dnsNotBound") }}
        </span>
        <!-- Onarım düğmesi YALNIZCA onarılacak bir şey varken görünür. Her zaman durduğunda
             kullanıcı "demek ki sorun var" diye tekrar tekrar basıyor, hiçbir şey değişmiyor
             (çünkü zaten sorun yok) ve panel bozukmuş gibi hissettiriyordu. -->
        <button v-if="dns.needsRepair" class="secondary compact" :disabled="dnsBusy" @click="repairDns">
          {{ dnsBusy ? t("panel.health.dnsRepairing") : t("panel.health.dnsRepair") }}
        </button>
      </div>
      <dl class="dns-facts">
        <div>
          <dt>{{ t("panel.health.dnsListening") }}</dt>
          <dd>{{ dns.listeningAddresses.length ? dns.listeningAddresses.join(", ") : "—" }}</dd>
        </div>
        <div>
          <dt>{{ t("panel.health.dnsExpected") }}</dt>
          <dd>{{ dns.expectedAddresses.length ? dns.expectedAddresses.join(", ") : "—" }}</dd>
        </div>
        <div v-if="dns.missingAddresses.length">
          <dt>{{ t("panel.health.dnsMissing") }}</dt>
          <dd class="warn">{{ dns.missingAddresses.join(", ") }}</dd>
        </div>
        <div>
          <dt>{{ t("panel.health.dnsZones") }}</dt>
          <dd>{{ dns.zoneCount }}</dd>
        </div>
        <div>
          <dt>{{ t("panel.health.dnsStartMode") }}</dt>
          <dd>
            {{ dns.startMode }}
            <span v-if="dns.delayedAutoStart">({{ t("panel.health.dnsDelayedOn") }})</span>
            <span v-else class="warn">({{ t("panel.health.dnsDelayedOff") }})</span>
          </dd>
        </div>
        <div v-if="dns.lastAutoRepairUtc">
          <dt>{{ t("panel.health.dnsLastAutoRepair") }}</dt>
          <dd>{{ formatDate(dns.lastAutoRepairUtc) }} — {{ dns.lastAutoRepairStatus }}</dd>
        </div>
      </dl>
      <p class="dns-details">{{ dns.details }}</p>
    </article>

    <!-- Alan adı tazeleme: panel güncellendiğinde YENİ varsayılanlar (DMARC, DKIM, MX…) yalnız yeni
         alan adlarına uygulanır. Eskiden eklenmiş alan adları eksik kalır ve bu eksiklik kendini
         aylar sonra "postalarım spam'e düşüyor" olarak belli eder. Panel eksiği kendisi ölçer;
         uygulama kararı kullanıcıya bırakılır. -->
    <!-- Yuklenirken kart NE IYI NE KOTU gorunur. Onceden veri gelmeden 'good' + yesil nokta
         basiliyordu: kullanici "her sey yolunda" diye bakip gecerken kart birden kirmiziya
         donuyordu. Olculmemis bir seyi "saglikli" gostermek, panelin en cok tekrarlanan hatasi. -->
    <article :class="['dns-card', domainsLoading ? 'idle' : (onarilacakVar ? 'bad' : 'good')]">
      <div class="dns-head">
        <span :class="['state-dot', domainsLoading ? 'dot-idle' : (onarilacakVar ? 'dot-danger' : 'dot-ok')]"></span>
        <div class="health-name">
          <strong>{{ t("panel.health.domainsTitle") }}</strong>
          <span class="svc-id">{{ t("panel.health.domainsHint") }}</span>
        </div>
        <span v-if="domainsLoading" class="status-pill">{{ t("panel.health.domainsLoading") }}</span>
        <span v-else-if="domains" :class="['status-pill', domains.updatedSites > 0 ? 'danger' : 'ok']">
          {{
            domains.updatedSites > 0
              ? t("panel.health.domainsStale", { count: domains.updatedSites, total: domains.totalSites })
              : t("panel.health.domainsCurrent", { total: domains.totalSites })
          }}
        </span>
        <!-- "Eksikleri tamamla" YALNIZCA gerçekten eksik varken görünür. Aksi hâlde kullanıcı
             hiç eksik yokken de düğmeyi görüp basıyor, hiçbir şey değişmiyor ve panel çalışmıyor
             sanıyordu. (İşlem zaten idempotent; sorun düğmenin varlığının yanlış sinyal vermesiydi.) -->
        <button
          v-if="!domainsLoading && onarilacakVar"
          class="secondary compact"
          :disabled="domainsBusy"
          @click="refreshAllDomains">
          {{ domainsBusy ? t("panel.health.domainsRefreshing") : t("panel.health.domainsRefresh") }}
        </button>

        <!-- KOSULSUZ CALISTIRMA.
             Dugme yalnizca panel bir eksik GORDUGUNDE cikiyor; panelin goremedigi bir eksik
             varsa kullanicinin elinde hicbir yol kalmiyordu. Islem zaten idempotent ve var olan
             kayitlara dokunmuyor, dolayisiyla calistirmanin zarari yok -- ama sessizce oturmak
             yerine denemek mumkun olmali. Sessiz bir baglanti olarak duruyor ki asil dugmeyle
             karistirilmasin. -->
        <button
          v-if="!domainsLoading && !onarilacakVar"
          class="ghost compact"
          :disabled="domainsBusy"
          @click="refreshAllDomains">
          {{ domainsBusy ? t("panel.health.domainsRefreshing") : t("panel.health.domainsForce") }}
        </button>
      </div>

      <!-- SUNUCU GENELI bulgular: kapali port, posta sunucusunun adi gibi tek bir alan adina
           ait olmayan sorunlar. Bunlar hicbir yerde yazmiyordu; kart yesil "N/N guncel" derken
           yaninda onarma dugmesi cikiyor, kullanici neyin eksik oldugunu goremiyordu. -->
      <!-- PANELIN NE OLCTUGU. Kara kutu olmamali: "eksik yok" derken neye baktigini
           gostermezse, yanlis olcum sessizce dogru sonuc gibi gorunur. -->
      <SectionLoadError v-if="domainsLoad.message.value" :message="domainsLoad.message.value" @retry="load" />

      <p v-if="!domainsLoading && domains" class="measure-line">
        {{ t("panel.health.domainsMeasured", {
          total: domains.totalSites,
          stale: domains.updatedSites,
          findings: (domains.serverFindings?.length ?? 0)
        }) }}
      </p>

      <ul v-if="domains?.serverFindings?.length" class="server-findings">
        <li v-for="bulgu in domains.serverFindings" :key="bulgu.key + bulgu.detail" :class="bulgu.severity">
          {{ t(bulgu.key) }}
          <small v-if="bulgu.detail">{{ bulgu.detail }}</small>
        </li>
      </ul>

      <!-- Ne yapıldığını kayıt kayıt göster: "onarıldı" demek tek başına güven vermez. -->
      <dl v-if="domainReport.length" class="dns-facts">
        <div v-for="row in domainReport" :key="row.siteName">
          <dt>{{ row.siteName }}</dt>
          <dd :class="{ warn: row.error }">
            <template v-if="row.error">{{ row.error }}</template>
            <template v-else-if="row.alreadyCurrent">{{ t("panel.health.domainsRowCurrent") }}</template>
            <template v-else>
              {{ row.addedDnsRecords.join(", ") || "—" }}
              <!-- Bulgular: eklenen kayit sayisiyla anlatilamayan sorunlar. Gosterilmezse
                   uretilseler bile kullaniciya HIC ULASMIYORLAR. -->
              <span v-for="bulgu in row.findings ?? []" :key="bulgu.key" class="row-finding">
                · {{ t(bulgu.key) }}
              </span>
              <span v-if="row.appliedMailRecords">
                · {{ t("panel.health.domainsRowMail", { count: row.appliedMailRecords }) }}
              </span>
            </template>
          </dd>
        </div>
      </dl>
    </article>

    <p v-if="loading && !services.length" class="meta">{{ t("panel.common.loading") }}</p>

    <div v-else-if="services.length" class="health-list">
      <article v-for="svc in services" :key="svc.name" class="health-row">
        <div class="health-id">
          <span :class="['state-dot', dotClass(svc.status)]"></span>
          <div class="health-name">
            <strong>{{ svc.label }}</strong>
            <span class="svc-id">{{ svc.name }}</span>
          </div>
        </div>
        <span :class="['status-pill', pillClass(svc.status)]">{{ statusText(svc.status) }}</span>
        <button
          v-if="svc.status !== 'NotInstalled'"
          class="secondary compact"
          :disabled="busyName === svc.name"
          @click="restart(svc.name)">
          {{ busyName === svc.name ? t("panel.health.restarting") : t("panel.health.restart") }}
        </button>
        <span v-else class="na">—</span>
      </article>
    </div>
    <p v-else-if="loaded" class="meta">{{ t("panel.health.empty") }}</p>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import {
  getServerHealth,
  inspectDomains,
  refreshDomains,
  restartService,
  type DomainRefreshResult,
  type DomainRefreshSummary,
  type ServiceHealth,
} from "../api/health";
import { getDnsServerHealth, repairDnsServer, type DnsServerHealth } from "../api/dns";
import SectionLoadError from "../components/SectionLoadError.vue";
import { useNotice, useSectionError } from "../composables/useNotice";

const { t } = useI18n();
const services = ref<ServiceHealth[]>([]);
const dns = ref<DnsServerHealth | null>(null);
const dnsLoad = useSectionError();
const dnsBusy = ref(false);
const loading = ref(false);
const loaded = ref(false);
const busyName = ref("");
const { notice, noticeError, showNotice, resolveApiMessage } = useNotice();
const domains = ref<DomainRefreshSummary | null>(null);
const domainsLoad = useSectionError();
const domainReport = ref<DomainRefreshResult[]>([]);
const domainsBusy = ref(false);
// Ilk denetim bitene kadar true: kart bu sure boyunca notr gorunur.
const domainsLoading = ref(true);

/**
 * Onarmanın yapacağı bir iş var mı?
 *
 * Yalnız updatedSites'a bakmak yetmiyordu: sunucu geneli bulgular (posta sunucusunun adı,
 * kapalı güvenlik duvarı portu) hiçbir alan adına ait değil, dolayısıyla o sayıya girmiyor.
 * Girmeyince düğme çıkmıyor, düğme çıkmayınca onarma hiç çalışmıyordu — panelin düzeltebildiği
 * bir sorun kullanıcıya hiç ulaşmıyordu.
 */
const onarilacakVar = computed(
  () => (domains.value?.updatedSites ?? 0) > 0 || (domains.value?.serverFindings?.length ?? 0) > 0
);

onMounted(load);

async function load() {
  loading.value = true;
  notice.value = "";
  try {
    services.value = await getServerHealth();
    loaded.value = true;
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    loading.value = false;
  }

  // DNS kartı bağımsız yüklenir: ölçüm başarısız olsa bile servis listesi görünmeye devam eder.
  try {
    dns.value = await getDnsServerHealth();
    dnsLoad.clear();
  } catch (error) {
    // Kartın kaybolması "DNS kurulu değil" gibi okunuyordu; ölçülemediğini AÇIKÇA söylüyoruz.
    dns.value = null;
    dnsLoad.capture(error);
  }

  // Alan adı denetimi de bağımsız ve SALT OKUNUR: hiçbir şey değiştirmez, yalnız eksiği sayar.
  try {
    domains.value = await inspectDomains();
    domainReport.value = domains.value.results.filter((row) => row.error || !row.alreadyCurrent);
    domainsLoad.clear();
  } catch (error) {
    // Ölçüm YAPILAMADI ile "eksik yok" ayrı şeyler; ikincisini göstermek yeşil yalan olurdu.
    domains.value = null;
    domainReport.value = [];
    domainsLoad.capture(error);
  } finally {
    domainsLoading.value = false;
  }
}

async function refreshAllDomains() {
  domainsBusy.value = true;
  notice.value = "";
  try {
    const summary = await refreshDomains();
    domains.value = summary;
    // Tazelemeden SONRA tüm satırlar gösterilir: hangi alan adına ne eklendiği (ya da hiçbir şey
    // eklenmediği) görünmeden işlem "yapıldı mı" belirsiz kalır.
    domainReport.value = summary.results;
    showNotice(
      t("panel.health.domainsDone", { updated: summary.updatedSites, total: summary.totalSites }),
      summary.failedSites > 0,
    );
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    domainsBusy.value = false;
  }
}

async function repairDns() {
  dnsBusy.value = true;
  notice.value = "";
  try {
    dns.value = await repairDnsServer(true);
    showNotice(
      dns.value.publiclyBound ? t("panel.health.dnsRepaired") : t("panel.health.dnsStillBad"),
      !dns.value.publiclyBound
    );
    services.value = await getServerHealth();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    dnsBusy.value = false;
  }
}

function formatDate(value: string) {
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString();
}

async function restart(name: string) {
  busyName.value = name;
  notice.value = "";
  try {
    const updated = await restartService(name);
    const index = services.value.findIndex((svc) => svc.name === updated.name);
    if (index >= 0) {
      services.value[index] = updated;
    }
    showNotice(t("panel.health.restartedNotice", { name: updated.label }), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busyName.value = "";
  }
}

function statusText(status: string) {
  if (status === "Running") return t("panel.health.statusRunning");
  if (status === "Stopped") return t("panel.health.statusStopped");
  if (status === "NotInstalled") return t("panel.health.statusNotInstalled");
  return t("panel.health.statusUnknown");
}

function pillClass(status: string) {
  if (status === "Running") return "ok";
  if (status === "Stopped") return "danger";
  return "";
}

function dotClass(status: string) {
  if (status === "Running") return "dot-ok";
  if (status === "Stopped") return "dot-danger";
  return "dot-muted";
}

</script>

<style scoped>
.server-health {
  display: grid;
  gap: 12px;
}

.health-list {
  display: grid;
  gap: 8px;
}

.health-row {
  display: flex;
  align-items: center;
  gap: 14px;
  padding: 11px 14px;
  border: 1px solid var(--panel-border);
  border-radius: 11px;
  background: var(--surface-1);
}

.health-id {
  display: flex;
  align-items: center;
  gap: 12px;
  flex: 1 1 auto;
  min-width: 0;
}

.health-name {
  display: grid;
  gap: 2px;
  min-width: 0;
}

.svc-id {
  color: var(--text-muted);
  font-size: 0.74rem;
  font-family: var(--font-mono, ui-monospace, monospace);
}

.health-row .status-pill {
  flex-shrink: 0;
}

.health-row button {
  width: auto;
  flex-shrink: 0;
}

.na {
  width: 84px;
  text-align: center;
  color: var(--text-muted);
  flex-shrink: 0;
}

.state-dot {
  width: 9px;
  height: 9px;
  border-radius: 999px;
  background: var(--text-muted);
  flex-shrink: 0;
}

.state-dot.dot-ok {
  background: var(--ok-1);
  box-shadow: 0 0 0 3px color-mix(in srgb, var(--ok-1) 22%, transparent);
}

/* Yukleniyor: notr gri + hafif nabiz. Yesil/kirmizi BILEREK kullanilmiyor -- henuz olculmemis
   bir durumu renkle yorumlamak, kullaniciyi yaniltmanin en kolay yolu. */
.measure-line {
  margin: 0.4rem 0 0;
  font-size: 0.8rem;
  opacity: 0.65;
}

.row-finding {
  opacity: 0.85;
}

.server-findings {
  margin: 0.5rem 0 0;
  padding-left: 1.1rem;
  font-size: 0.88rem;
}

.server-findings li.fail {
  color: var(--danger-1, #d64545);
}

.server-findings small {
  display: block;
  opacity: 0.7;
}

.state-dot.dot-idle {
  background: var(--text-muted);
  animation: dot-pulse 1.2s ease-in-out infinite;
}

@keyframes dot-pulse {
  0%, 100% { opacity: 0.35; }
  50% { opacity: 1; }
}

.state-dot.dot-danger {
  background: var(--danger-1);
  box-shadow: 0 0 0 3px color-mix(in srgb, var(--danger-1) 22%, transparent);
}

.state-dot.dot-muted {
  background: var(--text-muted);
}

.dns-card {
  display: grid;
  gap: 10px;
  padding: 13px 14px;
  border: 1px solid var(--panel-border);
  border-radius: 11px;
  background: var(--surface-1);
}

.dns-card.bad {
  border-color: color-mix(in srgb, var(--danger-1) 55%, var(--panel-border));
  background: color-mix(in srgb, var(--danger-1) 7%, var(--surface-1));
}

.dns-head {
  display: flex;
  align-items: center;
  gap: 12px;
}

.dns-head .health-name {
  flex: 1 1 auto;
}

.dns-head button {
  width: auto;
  flex-shrink: 0;
}

.dns-facts {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(190px, 1fr));
  gap: 8px 16px;
  margin: 0;
}

.dns-facts dt {
  color: var(--text-muted);
  font-size: 0.74rem;
}

.dns-facts dd {
  margin: 0;
  font-size: 0.86rem;
  word-break: break-word;
}

.dns-facts .warn {
  color: var(--danger-1);
}

.dns-details {
  margin: 0;
  color: var(--text-muted);
  font-size: 0.72rem;
  font-family: var(--font-mono, ui-monospace, monospace);
  word-break: break-word;
}

/*
  Dar ekran: bu ekran olay akışının kalbi ("neyin bozuk olduğunu gör" + "yeniden başlat").
  Servis adı tek satıra, DURUM ROZETİ ile EYLEM DÜĞMESİ ise alt satırda yan yana durur —
  ikisi de asla gizlenmez, düğme parmak hedefine büyütülür.
*/
@media (max-width: 620px) {
  .health-row,
  .dns-head {
    flex-wrap: wrap;
    gap: 10px;
  }

  .health-row {
    padding: 11px 12px;
  }

  .health-id {
    flex: 1 1 100%;
  }

  /* DNS başlığında nokta + ad ilk satırı doldursun (nokta 9px + 10px boşluk). */
  .dns-head .health-name {
    flex: 1 1 calc(100% - 22px);
  }

  .health-row .status-pill,
  .dns-head .status-pill {
    order: 1;
  }

  .health-row button,
  .health-row .na,
  .dns-head button {
    order: 2;
    margin-left: auto;
    min-height: 42px;
  }

  .health-row button,
  .dns-head button {
    min-width: 148px;
  }

  .na {
    width: auto;
    min-width: 0;
  }

  .dns-head {
    align-items: center;
  }

  /* DNS ayrıntıları: tek sütun, etiket üstte — dinlenen/beklenen adresler uzun. */
  .dns-facts {
    grid-template-columns: minmax(0, 1fr);
    gap: 8px;
  }

  .dns-facts dd {
    overflow-wrap: anywhere;
  }
}
</style>
