<template>
  <section class="panel module-shell cluster-workspace">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.cluster.title") }}</h2>
        <p class="meta">{{ t("panel.cluster.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading" @click="refresh">
        {{ loading ? t("panel.cluster.refreshing") : t("panel.cluster.refresh") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">
      {{ notice }}
    </p>

    <!-- Toplam sayilar. Ulasilamayan dugum varsa toplamin EKSIK oldugu ACIKCA soylenir:
         bilinmeyeni sifir saymak, panelin yalan soylemesi olurdu.

         KAPSAMLI KOPYA SILINDI: bu blok `.form-surface` icine gomulu kendi `.summary-grid` /
         `.summary-cell` izgarasiydi -- base.css'te BIREBIR ayni isi yapan `.summary-strip` /
         `.summary-item` varken. Iki gorunur sonucu vardi: (1) kutu icinde kutu (kenarli
         `form-surface` + kenarsiz hucreler), ProxyView'in ayni rutbedeki ozet seridi ise
         kenarli kutucuklardan olusuyordu -- ayni bilgi iki ekranda iki ayri kutu deseniyle;
         (2) `.summary-grid` `auto-fit minmax(140px)` kullandigi icin genis ekranda 4 sutun,
         ProxyView'de ise sabit 4 sutun vardi ve dar ekran kirilimlari (900/720px) yalniz
         paylasilan surumde tanimliydi. Artik ikisi de ayni serit. -->
    <!-- OLCUM GELMEDEN SERIT "0" DIYORDU — ve dort kutucugun DORDU birden.
         Sayilar `overview?.… ?? 0` yaziliyordu; `overview` istek bitene kadar null oldugu icin
         ekran acilisinda "Kayitli sunucu 0 / Ulasilabilir 0" goruluyordu. Bu, hemen asagidaki
         listenin YAPMADIGI hatanin aynisiydi: liste `loaded` bayragi olmadan "kayit yok"
         demiyor, cunku yonetici dugumleri silinmis sanip yeniden eklemesin (gerekcesi kodda
         yazili). Serit ayni korumayi almiyordu, yani ayni ekranda ayni soruya iki farkli
         durusla cevap veriliyordu. Daha kotusu: ILK istek BASARISIZ olursa `loaded` hic true
         olmadigi icin o dort sifir EKRANDA KALICI kaliyordu.
         `??` YETMEZ, `loaded` GEREKIR: sunucu gercekten 0 dugum donduginde de deger 0'dir;
         "olculmedi" ile "olculdu ve sifir cikti" ancak bu bayrakla ayrilir.
         `—` secildi (panelin "deger yok" isareti); tire bir sayi gibi okunamaz. -->
    <div class="summary-strip">
      <article class="summary-item">
        <span class="tile-label">{{ t("panel.cluster.summaryNodes") }}</span>
        <strong>{{ loaded ? overview?.nodes.length ?? 0 : "—" }}</strong>
      </article>
      <article class="summary-item">
        <span class="tile-label">{{ t("panel.cluster.summaryReachable") }}</span>
        <strong>{{ loaded ? overview?.reachableCount ?? 0 : "—" }}</strong>
      </article>
      <article class="summary-item">
        <span class="tile-label">{{ t("panel.cluster.summaryUnreachable") }}</span>
        <strong :class="{ 'danger-text': loaded && (overview?.unreachableCount ?? 0) > 0 }">
          {{ loaded ? overview?.unreachableCount ?? 0 : "—" }}
        </strong>
      </article>
      <article class="summary-item">
        <span class="tile-label">{{ t("panel.cluster.summarySites") }}</span>
        <strong>{{ loaded ? overview?.totalSites ?? 0 : "—" }}</strong>
        <small v-if="loaded && (overview?.unreachableCount ?? 0) > 0" class="warn-text">
          {{ t("panel.cluster.summarySitesPartial") }}
        </small>
      </article>
    </div>

    <!-- Yeni dugum / duzenleme formu -->
    <section class="form-surface">
      <div class="section-head">
        <div>
          <h3>{{ editingId ? t("panel.cluster.formEditTitle") : t("panel.cluster.formTitle") }}</h3>
          <p class="meta">{{ t("panel.cluster.formHint") }}</p>
        </div>
        <button class="secondary compact" @click="toggleForm">
          {{ showForm ? t("panel.common.cancel") : t("panel.cluster.addNode") }}
        </button>
      </div>

      <template v-if="showForm">
        <div class="form-grid">
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.cluster.fieldLabel") }}</span>
            <input v-model="form.label" maxlength="64" :placeholder="t('panel.cluster.fieldLabelPlaceholder')" />
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.cluster.fieldAddress") }}</span>
            <input v-model="form.address" maxlength="255" :placeholder="t('panel.cluster.fieldAddressPlaceholder')" />
            <span class="field-hint">{{ t("panel.cluster.fieldAddressHint") }}</span>
          </label>
        </div>

        <label class="stacked-field">
          <span class="field-label">{{ t("panel.cluster.fieldToken") }}</span>
          <PasswordField v-model="form.apiToken" autocomplete="off" :placeholder="editingId ? t('panel.cluster.fieldTokenKeep') : 'adap_…'" />
          <span class="field-hint">{{ t("panel.cluster.fieldTokenHint") }}</span>
        </label>

        <label class="check-field">
          <input v-model="form.enabled" type="checkbox" />
          <span>{{ t("panel.cluster.fieldEnabled") }}</span>
        </label>
        <p class="field-hint">{{ t("panel.cluster.fieldEnabledHint") }}</p>

        <!-- TLS doğrulaması VARSAYILAN AÇIK. Kapatmak, merkezin yönetici belirtecini araya
             girene teslim etmeyi göze almaktır; bu yüzden kutu İŞARETLİ başlar ve işaret
             kaldırılınca kırmızı uyarı çıkar. -->
        <label class="check-field">
          <input v-model="form.verifyTlsCertificate" type="checkbox" />
          <span>{{ t("panel.cluster.fieldTls") }}</span>
        </label>
        <p v-if="form.verifyTlsCertificate" class="field-hint">{{ t("panel.cluster.fieldTlsHint") }}</p>
        <p v-else class="notice error">{{ t("panel.cluster.fieldTlsWarning") }}</p>

        <div class="row-actions">
          <button class="compact" :disabled="saving" @click="save">
            {{ saving ? t("panel.cluster.saving") : t("panel.cluster.save") }}
          </button>
        </div>
      </template>
    </section>

    <!-- Dugum listesi -->
    <section class="module-shell">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.cluster.nodesTitle") }}</h3>
          <p class="meta">{{ t("panel.cluster.nodesHint") }}</p>
        </div>
      </div>

      <div class="node-cards">
        <article v-for="row in overview?.nodes ?? []" :key="row.node.id" class="node-card">
          <header class="node-head">
            <div class="cell-main">
              <strong>{{ row.node.label }}</strong>
              <small>{{ row.node.address }}</small>
            </div>
            <span :class="['status-pill', statusTone(row)]">{{ statusLabel(row) }}</span>
          </header>

          <!-- Ulasilabilen dugum: gercek olcumler -->
          <!-- DOLULUK RENGI HER OLCUME BAGLI, YALNIZ DISKE DEGIL.
               OLCULDU: eskiden CPU ve BELLEK hicbir kosula bagli degildi -- %99 CPU ile %3 CPU
               ayni duz metin rengiyle yaziliyordu; yalnizca diskin kendine ait, tek basina
               secilmis bir esigi (85) vardi. Yani en buyuk iki sayi sifir bilgi tasiyordu.
               Ayni esik uc olcume de uygulaniyor (`dolulukSinifi`). Renk TEK KANAL DEGIL:
               sayinin kendisi ("%99") her zaman gorunur, renk yalnizca vurgudur. -->
          <div v-if="row.reachable && row.info" class="node-metrics">
            <div class="metric">
              <span class="tile-label">{{ t("panel.cluster.metricVersion") }}</span>
              <strong>{{ row.info.panelVersion || "—" }}</strong>
            </div>
            <div class="metric">
              <span class="tile-label">{{ t("panel.cluster.metricSites") }}</span>
              <strong class="metric-number">{{ row.info.siteCount }}</strong>
            </div>
            <div class="metric">
              <span class="tile-label">{{ t("panel.cluster.metricCpu") }}</span>
              <strong :class="['metric-number', dolulukSinifi(row.info.overview.cpuUsagePercent)]">
                {{ yuzde(row.info.overview.cpuUsagePercent) }}
              </strong>
            </div>
            <div class="metric">
              <span class="tile-label">{{ t("panel.cluster.metricMemory") }}</span>
              <strong :class="['metric-number', dolulukSinifi(row.info.overview.memoryUsagePercent)]">
                {{ yuzde(row.info.overview.memoryUsagePercent) }}
              </strong>
            </div>
            <!-- DISK OLCULEMEDIYSE "%0" DEGIL "—". `enDoluDisk` disk listesi bos gelince 0
                 donuyordu; 0 ise esigin altinda oldugu icin saglikli/notr renkte "%0"
                 yaziliyordu. Yani "diskini okuyamadim" ile "diskin bombos" ekranda BIREBIR
                 ayni goruntuydu -- ve ikisinden biri, dolmak uzere olan bir sunucuyu sakin
                 gosterebilirdi. Ayni karar halkalarda zaten verilmisti (MonitoringView
                 `ringStyle(null)` notr kalir); burada eksik olan sadece uygulanmasiydi. -->
            <div class="metric">
              <span class="tile-label">{{ t("panel.cluster.metricDisk") }}</span>
              <strong :class="['metric-number', dolulukSinifi(enDoluDisk(row))]">
                {{ yuzde(enDoluDisk(row)) }}
              </strong>
            </div>
            <div class="metric">
              <span class="tile-label">{{ t("panel.cluster.metricAlerts") }}</span>
              <strong :class="['metric-number', { 'danger-text': uyariSayisi(row) > 0 }]">
                {{ uyariSayisi(row) }}
              </strong>
            </div>
          </div>

          <!-- Ulasilamayan dugum: satir DUSMEZ, sebebini SOYLER -->
          <p v-else class="notice error node-down">
            {{ statusMessage(row) }}
            <small v-if="row.detail"> ({{ row.detail }})</small>
          </p>

          <!-- YIKICI EYLEM AYRI VE SABIT KONUMDA (ProxyView'deki `.action-row .danger` ile ayni
               karar). Eskiden "Sil", "Duzenle"nin 8px sagindaydi: ayni boy, ayni sira, ayni
               hizada -- refleksle yanlisina basilacak dizilim. Uc guvenli eylem kendi obeginde
               kaldi, silme obekten kopartildi ve HER ZAMAN satirin sonunda duruyor. -->
          <footer class="node-actions">
            <span v-if="!row.node.verifyTlsCertificate" class="status-pill danger">
              {{ t("panel.cluster.tlsOff") }}
            </span>
            <span v-if="!row.node.hasToken" class="status-pill warn">{{ t("panel.cluster.noToken") }}</span>
            <span class="cell-muted">{{ sonGorulme(row) }}</span>
            <div class="safe-actions">
              <button class="secondary compact row-button" :disabled="busyId === row.node.id" @click="probe(row)">
                {{ t("panel.cluster.test") }}
              </button>
              <button
                class="secondary compact row-button"
                :disabled="busyId === row.node.id || !row.reachable"
                :title="row.reachable ? t('panel.cluster.openHint') : t('panel.cluster.openDisabled')"
                @click="open(row)">
                {{ t("panel.cluster.open") }}
              </button>
              <button class="secondary compact row-button" @click="edit(row)">{{ t("panel.common.edit") }}</button>
            </div>
            <button class="danger compact row-button" :disabled="busyId === row.node.id" @click="remove(row)">
              {{ t("panel.cluster.remove") }}
            </button>
          </footer>
        </article>

        <p v-if="!loaded" class="empty-state" role="status" aria-live="polite">{{ t("panel.common.loading") }}</p>
        <p v-else-if="(overview?.nodes.length ?? 0) === 0" class="empty-state">{{ t("panel.cluster.empty") }}</p>
      </div>
    </section>

    <!-- Yeni site: hangi sunucuya gidecegi SECILIR (Plesk'te abonelik tek dugume kurulur) -->
    <section v-if="(overview?.reachableCount ?? 0) > 0" class="form-surface">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.cluster.siteTitle") }}</h3>
          <p class="meta">{{ t("panel.cluster.siteHint") }}</p>
        </div>
      </div>

      <div class="form-grid">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.cluster.siteName") }}</span>
          <input v-model="siteForm.name" maxlength="128" placeholder="example.test" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.cluster.siteNode") }}</span>
          <select v-model="siteForm.nodeId">
            <option value="">{{ otomatikEtiketi }}</option>
            <option v-for="row in ulasilabilirler" :key="row.node.id" :value="row.node.id">
              {{ row.node.label }} ({{ row.info?.siteCount ?? 0 }})
            </option>
          </select>
          <span class="field-hint">{{ t("panel.cluster.siteNodeHint") }}</span>
        </label>
      </div>

      <div class="row-actions">
        <button class="compact" :disabled="creatingSite" @click="createSite">
          {{ creatingSite ? t("panel.cluster.siteCreating") : t("panel.cluster.siteCreate") }}
        </button>
      </div>
    </section>
  </section>
</template>

<script setup lang="ts">
import { getFormattingLocale } from '../utils/locale';
/*
 * ÇOKLU SUNUCU YÖNETİMİ (Plesk Multi Server karşılığı).
 *
 * Model kopyalandı, icat edilmedi: bir "yönetim düğümü" (bu panel) ve ona kayıtlı "hizmet
 * düğümleri" vardır. Merkez düğümleri izler, tek tıkla düğüme geçilir ve yeni site tek bir
 * düğüme kurulur (Plesk'te de bir abonelik düğümler arasına BÖLÜNMEZ).
 *
 * Bu ekran YALNIZ YÖNETİCİ içindir; uç da sınıf düzeyinde Administrator ister (asıl dayatma orada).
 */
import { computed, onMounted, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import PasswordField from "../components/PasswordField.vue";
import { useConfirm } from "../components/ConfirmDialog.vue";
import {
  createClusterSite,
  getClusterNodeLink,
  getClusterOverview,
  probeClusterNode,
  registerClusterNode,
  removeClusterNode,
  updateClusterNode,
  type ClusterNodeStatus,
  type ClusterOverview
} from "../api/cluster";
import { extractApiDetails, extractApiMessage } from "../stores/auth";
import { formatDateTime } from "../utils/datetime";
import { openAsyncReachable } from "../utils/externalTab";

const { t } = useI18n();
const { confirm } = useConfirm();

const overview = ref<ClusterOverview | null>(null);
// Veri gelmeden "kayıt yok" gösterilmez: yönetici düğümü yok sanıp yeniden eklemesin.
const loaded = ref(false);
const loading = ref(false);
const saving = ref(false);
const creatingSite = ref(false);
const busyId = ref("");
const showForm = ref(false);
const editingId = ref("");
const notice = ref("");
const noticeError = ref(false);

const form = reactive({
  label: "",
  address: "",
  apiToken: "",
  enabled: true,
  // VARSAYILAN AÇIK — sunucu tarafındaki varsayılanla aynı.
  verifyTlsCertificate: true
});

const siteForm = reactive({ name: "", nodeId: "" });

const ulasilabilirler = computed(() => (overview.value?.nodes ?? []).filter((row) => row.reachable));

const otomatikEtiketi = computed(() => t("panel.cluster.siteNodeAuto"));

onMounted(refresh);

async function refresh() {
  loading.value = true;
  try {
    overview.value = await getClusterOverview();
    loaded.value = true;
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    loading.value = false;
  }
}

function toggleForm() {
  showForm.value = !showForm.value;
  if (!showForm.value) {
    resetForm();
  }
}

function resetForm() {
  editingId.value = "";
  form.label = "";
  form.address = "";
  form.apiToken = "";
  form.enabled = true;
  form.verifyTlsCertificate = true;
}

function edit(row: ClusterNodeStatus) {
  editingId.value = row.node.id;
  form.label = row.node.label;
  form.address = row.node.address;
  // Belirteç OKUNAMAZ: sunucu yalnız korunmuş hâlini saklar ve geri döndürmez.
  // Boş bırakılırsa mevcut anahtar korunur.
  form.apiToken = "";
  form.enabled = row.node.enabled;
  form.verifyTlsCertificate = row.node.verifyTlsCertificate;
  showForm.value = true;
}

async function save() {
  if (form.label.trim().length < 2 || form.address.trim().length === 0) {
    showNotice(t("panel.cluster.formIncomplete"), true);
    return;
  }

  if (!editingId.value && form.apiToken.trim().length === 0) {
    showNotice(t("cluster.token_required"), true);
    return;
  }

  saving.value = true;
  try {
    const payload = {
      label: form.label.trim(),
      address: form.address.trim(),
      apiToken: form.apiToken.trim(),
      enabled: form.enabled,
      verifyTlsCertificate: form.verifyTlsCertificate
    };

    if (editingId.value) {
      await updateClusterNode(editingId.value, payload);
    } else {
      await registerClusterNode(payload);
    }

    showForm.value = false;
    resetForm();
    await refresh();
    showNotice(t("panel.cluster.saved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    saving.value = false;
  }
}

async function probe(row: ClusterNodeStatus) {
  busyId.value = row.node.id;
  try {
    const durum = await probeClusterNode(row.node.id);
    await refresh();
    showNotice(
      durum.reachable
        ? t("panel.cluster.testOk", { label: row.node.label, ms: durum.durationMs })
        : `${t(durum.statusKey)}${durum.detail ? ` (${durum.detail})` : ""}`,
      !durum.reachable
    );
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busyId.value = "";
  }
}

/**
 * "Düğüme git": düğümün GERÇEK panel adresini (gizli giriş yolu dahil) çözer ve yeni sekmede açar.
 *
 * Bu bir OTURUM DEVRİ DEĞİLDİR ve olmaması bilinçlidir — yönetici düğümde kendi kimliğiyle
 * giriş yapar. Gerekçe sunucu tarafında (ClusterNodeLinkDto) yazılı: bir API anahtarının oturum
 * çerezine dönüşebilmesi, panelin kurduğu yetenek modelini anlamsızlaştırırdı.
 */
async function open(row: ClusterNodeStatus) {
  if (busyId.value) return;
  busyId.value = row.node.id;
  try {
    await openAsyncReachable(async () => (await getClusterNodeLink(row.node.id)).url);
    showNotice(t("panel.cluster.openedHint", { label: row.node.label }), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busyId.value = "";
  }
}

async function remove(row: ClusterNodeStatus) {
  const confirmed = await confirm({
    title: t("panel.cluster.removeTitle"),
    message: t("panel.cluster.removeMessage", { label: row.node.label }),
    impact: [t("panel.cluster.removeImpactLocal"), t("panel.cluster.removeImpactRemote")],
    confirmLabel: t("panel.cluster.remove"),
    busyLabel: t("panel.cluster.removing")
  });

  if (!confirmed) {
    return;
  }

  busyId.value = row.node.id;
  try {
    await removeClusterNode(row.node.id);
    await refresh();
    showNotice(t("panel.cluster.removed"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busyId.value = "";
  }
}

async function createSite() {
  if (siteForm.name.trim().length < 2) {
    showNotice(t("panel.cluster.siteNameRequired"), true);
    return;
  }

  creatingSite.value = true;
  try {
    const sonuc = await createClusterSite({
      nodeId: siteForm.nodeId || null,
      name: siteForm.name.trim(),
      hostName: siteForm.name.trim()
    });

    siteForm.name = "";
    await refresh();
    showNotice(t("panel.cluster.siteCreated", { site: sonuc.siteName, label: sonuc.nodeLabel }), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    creatingSite.value = false;
  }
}

function statusTone(row: ClusterNodeStatus) {
  if (row.reachable) {
    return uyariSayisi(row) > 0 ? "warn" : "ok";
  }

  return row.node.enabled ? "danger" : "warn";
}

function statusLabel(row: ClusterNodeStatus) {
  if (row.reachable) {
    return t("panel.cluster.statusOnline");
  }

  return row.node.enabled ? t("panel.cluster.statusOffline") : t("panel.cluster.statusDisabled");
}

/** Sunucunun döndürdüğü anahtarı çevirir; çeviri yoksa anahtarın kendisi görünmesin diye yedek metin. */
function statusMessage(row: ClusterNodeStatus) {
  if (!row.statusKey) {
    return t("cluster.node_unreachable");
  }

  const translated = t(row.statusKey);
  return translated === row.statusKey ? t("cluster.node_unreachable") : translated;
}

/**
 * Doluluk yüzdesinin durum sınıfı — CPU, bellek ve disk için AYNI eşik.
 *
 * Eşikler tek yerde: eskiden yalnız diskin kendi başına yazılmış bir 85'i vardı, CPU ve bellek
 * ise hiçbir eşiğe bağlı değildi. Aynı ekranda iki ölçüm renkleniyor, ikisi renklenmiyordu.
 *
 * 90 / 75 SEÇİLDİ: 90 ve üstü, panelin "durdurur" saydığı bölge (dolmuş disk yazma hatası verir,
 * doymuş CPU istek kuyruğu büyütür); 75 ise "bak ama acele etme" bölgesi. Eski 85 tek bir
 * ölçümün yerel kararıydı; iki eşiğe bölmek uyarı ile arızayı da ayırır (renk tek kanal olmasın
 * diye sayının kendisi zaten hep yazılı).
 */
function dolulukSinifi(value: number | null) {
  // Ölçülemeyen değer bir SAĞLIK İDDİASI taşımaz: ne yeşil ne kırmızı, sınıfsız kalır.
  if (value === null) {
    return "";
  }

  if (value >= 90) {
    return "danger-text";
  }

  return value >= 75 ? "warn-text" : "";
}

/** `null` = ÖLÇÜLEMEDİ (düğüm disk listesi vermedi); 0 ile karıştırılmamalı, bkz. şablon. */
function enDoluDisk(row: ClusterNodeStatus): number | null {
  const disks = row.info?.overview.disks ?? [];
  // Ortalama DEĞİL en kötü disk: sunucuyu durduran şey disklerin ortalaması değil, dolan ilk diski.
  return disks.length === 0 ? null : Math.max(...disks.map((disk) => disk.usagePercent));
}

function uyariSayisi(row: ClusterNodeStatus) {
  // "NotInstalled" bir uyarı değildir (o bileşen o sunucuda kurulu değil); çalışmayan servis uyarıdır.
  return (row.info?.services ?? []).filter((service) => service.status === "Stopped").length;
}

function sonGorulme(row: ClusterNodeStatus) {
  if (!row.node.lastSeenAtUtc) {
    return t("panel.cluster.neverSeen");
  }

  // ELLE `new Date(...).toLocaleString(getFormattingLocale())` — `utils/datetime.ts`in tek kaynaga topladigi 24
  // kopyadan biri BURADA HAYATTA KALMIS. Bugun ciktisi ayni, ama korumasi degil: bozuk bir
  // damga "Invalid Date" olarak basilirdi (tek kaynak ham degeri basar, gerekcesi orada
  // yazili). Ayrica bicim kararlarindan biri degistiginde bu ekran geride kalirdi.
  return t("panel.cluster.lastSeen", { time: formatDateTime(row.node.lastSeenAtUtc) });
}

function yuzde(value: number | null) {
  return value === null ? "—" : `${Math.round(value)}%`;
}

function resolveApiMessage(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  const message = translated === key ? key : translated;
  const details = extractApiDetails(error);
  return details ? `${message}\n\n${details}` : message;
}

function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}
</script>

<style scoped>
.cluster-workspace {
  align-content: start;
}

/* Ozet serit degeri: etiketten (`--fs-2xs`, 0.78rem) belirgin sekilde buyuk ama olcegin ICINDE.
   1.6rem SILINDI -- yazi olceginde boyle bir adim yok (en ust adim `--fs-2xl` = 1.42rem) ve
   bu sayi bir sayfa basligi degil, bir kutucuk degeri; `--fs-xl` (1.18rem) etiketle arasinda
   1.5 kat fark birakir, bu hiyerarsi icin yeterli.
   `tabular-nums`: dort kutucuk ve alti olcum yan yana duruyor; degisken genislikli basamakla
   9 -> 10 gecisinde sayi kaydigi icin goz sutunu takip edemiyordu. */
.summary-item strong,
.metric-number {
  font-size: var(--fs-xl);
  line-height: 1.15;
  font-variant-numeric: tabular-nums;
}

.node-cards {
  display: grid;
  gap: var(--sp-12);
}

/* IC KART RECETESI — alti ekranda ayni (bkz. ServerHealthView'deki olcum notu):
   1px kenar + `--radius-md` + `--surface-1` + `--sp-12` dolgu. Burada 10px yaricap ve
   14px dolgu vardi; ikisi de olcek disiydi. */
.node-card {
  display: grid;
  gap: var(--sp-12);
  padding: var(--sp-12);
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--surface-1);
}

.node-head {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: var(--sp-10);
  flex-wrap: wrap;
}

.node-metrics {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(110px, 1fr));
  gap: var(--sp-10);
}

.metric {
  display: grid;
  gap: var(--sp-2);
}

.node-down {
  margin: 0;
}

.node-actions {
  display: flex;
  align-items: center;
  gap: var(--sp-8);
  flex-wrap: wrap;
}

.node-actions .cell-muted {
  margin-right: auto;
}

/* Guvenli eylemler tek obek. Silme dugmesi obegin DISINDA kaldigi icin aradaki bosluk
   `--node-actions` araligindan (8px) belirgin sekilde buyuk (16px): goz iki grubu ayirir,
   parmak yanlisina kaymaz. */
.safe-actions {
  display: flex;
  align-items: center;
  gap: var(--sp-8);
  flex-wrap: wrap;
}

.node-actions > .danger {
  margin-left: var(--sp-16);
}

@media (max-width: 760px) {
  .form-grid {
    grid-template-columns: 1fr;
  }

  .node-actions .cell-muted {
    margin-right: 0;
    width: 100%;
  }

  /* Dar ekranda obekler alt alta duser; ayirici bosluk yatayda anlamsizlasir, dikeye
     tasinir ki silme dugmesi yine kendi basina dursun. */
  .node-actions > .danger {
    margin-left: 0;
    margin-top: var(--sp-4);
  }
}
</style>
