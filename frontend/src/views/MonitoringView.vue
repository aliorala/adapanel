<template>
  <div class="monitor-stack">
    <section class="panel">
      <div class="section-head">
        <div>
          <h2>{{ t("panel.monitor.title") }}</h2>
          <p class="meta">{{ t("panel.monitor.subtitle") }}</p>
        </div>
        <span class="meta">{{ loading ? t("panel.system.loading") : formatUtc(overview?.currentServerTimeUtc ?? null) }}</span>
      </div>
      <!-- Ortak kaynak göstergeleri ana sayfayla aynı durum eşiklerini kullanır. -->
      <div v-if="overview" class="resource-grid">
        <ResourceMetric kind="cpu" :label="t('panel.system.cpu')" :value="overview.cpuUsagePercent" :detail="overview.logicalProcessorCount + ' ' + t('panel.system.cores')">
          <svg v-if="cpuSpark" class="meter-spark cpu" viewBox="0 0 100 28" preserveAspectRatio="none" aria-hidden="true"><polyline :points="cpuSpark" /></svg>
        </ResourceMetric>
        <ResourceMetric kind="memory" :label="t('panel.system.memory')" :value="overview.memoryUsagePercent" :detail="formatBytes(overview.usedMemoryBytes) + ' / ' + formatBytes(overview.totalMemoryBytes)">
          <svg v-if="memSpark" class="meter-spark mem" viewBox="0 0 100 28" preserveAspectRatio="none" aria-hidden="true"><polyline :points="memSpark" /></svg>
        </ResourceMetric>
        <ResourceMetric kind="disk" :label="primaryDisk?.name || t('panel.system.disks')" :value="primaryDisk?.usagePercent ?? null" :detail="primaryDisk ? formatBytes(primaryDisk.usedBytes) + ' / ' + formatBytes(primaryDisk.totalBytes) : t('panel.common.notAvailable')" :hint="primaryDisk ? t('panel.system.free') + ': ' + formatBytes(primaryDisk.freeBytes) : ''" />
      </div>
    </section>

    <div class="monitor-columns">
      <section class="panel">
        <h3>{{ t("panel.system.serverInfo") }}</h3>

        <!-- `info-grid` + `info-card` SARMALAYICILARI KALDIRILDI. Panoda o iki ad ANLAMLI: orada
             yan yana İKİ kart var (sunucu bilgisi + diskler) ve ızgara onları diziyor. Burada
             ikisi zaten AYRI PANEL; geriye tek çocuklu bir ızgara ve panelin içinde ikinci bir
             çerçeve kalıyordu — aynı bilgi iki kenarlık, iki zemin, iki dolgu (16 + 18px) ile
             sarılıyordu. İç kutu silindi, listenin kendisi panelin çocuğu oldu. -->
        <dl v-if="overview" class="info-list">
          <div>
            <dt>{{ t("panel.system.machineName") }}</dt>
            <dd>{{ overview.machineName }}</dd>
          </div>
          <div>
            <dt>{{ t("panel.system.operatingSystem") }}</dt>
            <dd>{{ overview.operatingSystem }}</dd>
          </div>
          <div>
            <dt>{{ t("panel.system.architecture") }}</dt>
            <dd>{{ overview.osArchitecture }}</dd>
          </div>
          <div>
            <dt>{{ t("panel.system.framework") }}</dt>
            <dd>{{ overview.frameworkDescription }}</dd>
          </div>
          <div>
            <dt>{{ t("panel.system.bootTime") }}</dt>
            <dd>{{ formatUtc(overview.bootTimeUtc) }}</dd>
          </div>
          <div>
            <dt>{{ t("panel.system.uptime") }}</dt>
            <dd>{{ formatTimeSpan(overview.uptime) }}</dd>
          </div>
        </dl>

        <!-- ÖLÇÜLDÜ: `overview` gelmeden bu üç panel BAŞLIKTAN İBARETTİ — kenarlıklı, boş bir
             kutu. Boş kutu "burada bir şey yok" diye okunur; oysa doğrusu "henüz gelmedi".
             Aynı sayfanın üst şeridi zaten durumu söylüyordu, alt panelleri sessiz bırakmak
             tutarsızdı. -->
        <p v-else class="meta" role="status" aria-live="polite">{{ t("panel.common.loading") }}</p>
      </section>

      <section class="panel">
        <h3>{{ t("panel.system.disks") }}</h3>

        <div v-if="overview" class="mini-table">
          <div class="mini-row table-head disk-row">
            <span>{{ t("panel.system.diskName") }}</span>
            <span>{{ t("panel.system.usage") }}</span>
            <span>{{ t("panel.system.free") }}</span>
          </div>

          <!-- DİSK SATIRI ARTIK KONUŞUYOR — panonun disk tablosuyla BİREBİR aynı dil.
               Önceden `C:\ 99.9% 533 MiB` DÜZ METİNDİ: ne sıra, ne ton, ne sözcük. Dolmuş bir
               disk, ekranın en sakin bileşeninde duruyordu. Eşik UYDURULMADI: halkalarla aynı
               iki sabit kullanılıyor (bkz. `ringStyle`).
               RENK TEK KANAL DEĞİL, üç kanal: (1) yüzde zaten yazılı, (2) yanında SÖZCÜKLE
               rozet ("Kritik"/"Uyarı"), (3) satırın çok hafif tonu. Renk körü kullanıcı ve
               ekran okuyucu rozetin METNİNİ okur. -->
          <div
            v-for="disk in sortedDisks"
            :key="disk.name"
            :class="['mini-row', 'disk-row', { warn: diskTone(disk) === 'warn', danger: diskTone(disk) === 'danger' }]">
            <span>{{ disk.name }}</span>
            <span>
              {{ disk.usagePercent.toFixed(1) }}%
              <span v-if="diskTone(disk)" :class="['status-pill', diskTone(disk)]">{{ diskToneLabel(disk) }}</span>
            </span>
            <span>{{ formatBytes(disk.freeBytes) }}</span>
          </div>
        </div>
        <p v-else class="meta" role="status" aria-live="polite">{{ t("panel.common.loading") }}</p>
      </section>
    </div>

    <section class="panel">
      <h3>{{ t("panel.system.network") }}</h3>

      <div v-if="overview" class="mini-table">
        <div class="mini-row table-head network-row">
          <span>{{ t("panel.system.interface") }}</span>
          <span>{{ t("panel.system.download") }}</span>
          <span>{{ t("panel.system.upload") }}</span>
          <span>{{ t("panel.system.total") }}</span>
        </div>

        <!-- ÜÇÜNCÜ SÜTUN BİR HIZDI AMA BİRİKMİŞ TOPLAM GİBİ YAZILIYORDU.
             CANLI ÖLÇÜLDÜ (Ethernet 2): satır `120 KiB/sn · 563 KiB/sn · 683 KiB` diyordu.
             İlk iki hücre `/sn` taşıyor, üçüncüsü TAŞIMIYOR — ama üçü de aynı iki alandan,
             yani saniyelik hızdan geliyor. Sütunun adı da ("Toplam trafik") panelin HİÇ
             ölçmediği bir şeyi, birikmiş trafiği vaat ediyordu; `bytesReceivedPerSecond` bir
             sayaç değil, anlık orandır. İki düzeltme birlikte: biçim `formatSpeed`e alındı
             (aynı satırdaki komşularıyla tek dil) ve sütun adı "Toplam"a indi — birimi artık
             hücrenin kendisi söylüyor. Sütun KALDIRILMADI: indirme+yükleme toplamı, hangi
             arayüzün yükü taşıdığını tek bakışta veren şeydir. -->
        <div v-for="network in overview.networkInterfaces" :key="network.name" class="mini-row network-row">
          <span>{{ network.name }}</span>
          <span>{{ formatSpeed(network.bytesReceivedPerSecond) }}</span>
          <span>{{ formatSpeed(network.bytesSentPerSecond) }}</span>
          <span>{{ formatSpeed(network.bytesReceivedPerSecond + network.bytesSentPerSecond) }}</span>
        </div>

        <!-- Ağ arayüzü listesi BOŞ dönebilir (kapalı/filtrelenmiş arayüzler). Eskiden geriye
             yalnız başlık satırı kalıyordu: tablo "yüklenmedi" mi "hiç arayüz yok" mu
             söylemiyordu. -->
        <div v-if="overview.networkInterfaces.length === 0" class="mini-row">
          <span class="meta">{{ t("panel.system.networkEmpty") }}</span>
        </div>
      </div>
      <p v-else class="meta" role="status" aria-live="polite">{{ t("panel.common.loading") }}</p>

      <!-- GRAFİĞİN ÖLÇEĞİ YOKTU: kutunun içinde iki eğri vardı ama ne eksen, ne ızgara, ne de
           tek bir sayı. Kullanıcı bir tepenin 2 KB/sn mi 200 MB/sn mi olduğunu okuyamıyor,
           kutunun %95'i boş duruyordu. Eklenenlerin hepsi ÖLÇÜLEN veriden gelir, hiçbiri
           uydurma değil: y ekseninde tepe değer (`netScale`, zaten çizimin ölçeği), yarısı ve
           sıfır; x ekseninde örnek penceresinin iki ucu. Izgara çizgileri `--panel-border` ile
           en geride durur — grafiği okutur, onunla yarışmaz. -->
      <div v-if="overview && hasNetTrend" class="net-trend">
        <div class="net-trend-head">
          <span>{{ t("panel.monitor.networkTrend") }}</span>
          <span class="meta">{{ t("panel.monitor.trendPeak") }}: {{ formatSpeed(netScale) }}</span>
        </div>
        <div class="net-plot">
          <div class="net-axis-y" aria-hidden="true">
            <span>{{ formatSpeed(netScale) }}</span>
            <span>{{ formatSpeed(netScale / 2) }}</span>
            <span>0</span>
          </div>
          <svg class="net-spark" viewBox="0 0 100 28" preserveAspectRatio="none" role="img"
            :aria-label="t('panel.monitor.networkTrend') + ' — ' + t('panel.monitor.trendPeak') + ': ' + formatSpeed(netScale)">
            <line class="net-grid" x1="0" y1="1" x2="100" y2="1" vector-effect="non-scaling-stroke" />
            <line class="net-grid" x1="0" y1="14" x2="100" y2="14" vector-effect="non-scaling-stroke" />
            <line class="net-axis" x1="0" y1="27" x2="100" y2="27" vector-effect="non-scaling-stroke" />
            <polyline class="down" :points="netDownSpark" />
            <polyline class="up" :points="netUpSpark" />
          </svg>
        </div>
        <!-- X EKSENİNİN SOL UCU SABİT BİR CÜMLEYDİ ("Son ~24 saat") VE ÖLÇÜMLE İLGİSİ YOKTU.
             CANLI ÖLÇÜLDÜ: `polyline` 4 NOKTA taşırken eksen 24 saat yazıyordu. Sunucu dakikada
             bir örneklediği için o grafik ~4 dakikalık bir pencereydi; yani panel, elinde
             olmayan bir geçmişi varmış gibi gösteriyordu. Panel yeni kurulduğunda ya da servis
             yeniden başladığında grafik HEP böyle başlar, yani bu istisna değil ilk hâl.
             Veri zaten elimizdeydi: `SystemHistorySample.timestampUtc` (bkz. stores/system.ts)
             hiç okunmuyordu. Sol uç artık ilk örneğin GERÇEK zamanı; biçim panelin geri
             kalanıyla aynı (utils/datetime.ts). -->
        <div class="net-axis-x" aria-hidden="true">
          <span>{{ trendStartLabel }}</span>
          <span>{{ t("panel.monitor.trendNow") }}</span>
        </div>
        <div class="net-legend">
          <span><i class="legend-dot down"></i>{{ t("panel.system.download") }}</span>
          <span><i class="legend-dot up"></i>{{ t("panel.system.upload") }}</span>
        </div>
      </div>
    </section>

    <!-- Canlı süreç izleyici (htop benzeri). Fark: ham süreç adı yerine "hangi site / hangi
         bileşen" etiketi gösterilir — "sunucu yavaş" sorusunun asıl cevabı budur. -->
    <section class="panel">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.processes.title") }}</h3>
          <p class="meta">{{ t("panel.processes.subtitle") }}</p>
        </div>
        <div class="row-actions">
          <select v-model="processSort" class="compact-select">
            <option value="cpu">{{ t("panel.processes.sortCpu") }}</option>
            <option value="memory">{{ t("panel.processes.sortMemory") }}</option>
          </select>
          <button class="secondary compact" @click="processPaused = !processPaused">
            {{ processPaused ? t("panel.processes.resume") : t("panel.processes.pause") }}
          </button>
        </div>
      </div>

      <!--
        Ekrana SABİTLENİR (`page-notice`, bkz. styles/base.css).
        Bu ekranın YAZMA işlemi yok; sabitlenmesi gereken şey bir "işlem sonucu" değil, canlı
        akışın ÖLDÜĞÜ bilgisi. İzleme sayfası beş bölüm uzunluğunda ve süreç tablosu ortada
        kalıyor: yoklama başarısız olduğunda kullanıcı aşağıdaki grafiklere bakarken tablodaki
        DEĞERLERİN DONDUĞUNU fark etmiyor, gördüğü eski anlık görüntüyü canlı sanıyordu.
        Yoklama düzelince şerit kendiliğinden kaybolur.
      -->
      <!-- "YÜKLENİYOR…" 10+ SANİYE ASILI KALIYORDU ve o cümle bunu söyleyemiyordu.
           Süreç anlık görüntüsü sunucuda WMI/performans sayaçlarından toplanıyor; ilk tur yavaş
           sunucuda saniyeler sürebiliyor ve iki saniyede bir yeniden denendiği için ekranda
           HİÇBİR ŞEY değişmiyordu — kullanıcı ekranın donduğunu sanıyordu.
           Bilgi eklendi, çıkarılmadı: ilk 4 saniye panelin ortak "Yükleniyor…" cümlesi, sonrası
           ölçümün SÜRDÜĞÜNÜ açıkça söyleyen cümle. Uydurma bir zaman aşımı YOK; yoklama devam
           eder, hata olursa zaten kendi şeridine düşer. -->
      <p v-if="processError" class="notice error page-notice" role="alert">{{ processError }}</p>
      <p v-else-if="!snapshot" class="meta" role="status" aria-live="polite">
        {{ processWaitedLong ? t("panel.processes.stillLoading") : t("panel.common.loading") }}
      </p>
      <p v-else-if="!snapshot.cpuBaselineReady" class="field-hint">{{ t("panel.processes.baselineHint") }}</p>

      <div v-if="snapshot" class="proc-table">
        <div class="proc-row head">
          <span>{{ t("panel.processes.owner") }}</span>
          <span>{{ t("panel.processes.pid") }}</span>
          <span>{{ t("panel.system.cpu") }}</span>
          <span>{{ t("panel.system.memory") }}</span>
          <span>{{ t("panel.processes.uptime") }}</span>
        </div>
        <!-- Sabit yükseklikli kaydırma alanı: satır sayısı değiştikçe bölümün boyu değişmesin,
             yoksa ALTINDAKİ "kim kullanıyor" bölümü sürekli aşağı yukarı zıplar.
             Fare/klavye tablonun üzerindeyken sıralama DONDURULUR (bkz. orderedProcesses). -->
        <div
          class="proc-scroll"
          @mouseenter="orderFrozen = true"
          @mouseleave="orderFrozen = false"
          @focusin="orderFrozen = true"
          @focusout="orderFrozen = false">
          <TransitionGroup name="proc" tag="div" class="proc-body">
            <div v-for="item in orderedProcesses" :key="item.processId" class="proc-row">
              <div class="proc-owner">
                <strong>{{ item.label || item.name }}</strong>
                <small>{{ t("panel.processes.category." + item.category) }} · {{ item.name }}<template v-if="item.detail"> · {{ item.detail }}</template></small>
              </div>
              <span class="proc-num">{{ item.processId }}</span>
              <span class="proc-num">
                <span :class="['proc-bar', cpuSeverity(item.cpuPercent)]" :style="{ '--fill': Math.min(100, item.cpuPercent) + '%' }"></span>
                {{ item.cpuPercent.toFixed(1) }}%
              </span>
              <span class="proc-num">{{ formatBytes(item.workingSetBytes) }}</span>
              <span class="proc-num">{{ formatUptime(item.uptimeSeconds) }}</span>
            </div>
          </TransitionGroup>
          <p v-if="orderedProcesses.length === 0" class="meta">{{ t("panel.processes.empty") }}</p>
        </div>
        <p class="field-hint">
          {{ t("panel.processes.footer", { shown: orderedProcesses.length, total: snapshot.totalProcessCount }) }}
          <span v-if="orderFrozen"> · {{ t("panel.processes.orderFrozen") }}</span>
        </p>
      </div>
    </section>

    <!-- Süreç listesinin bir adım derini. MySQL/PostgreSQL TEK süreçtir ve tüm siteler onu
         paylaşır; "MariaDB %42" satırı o yükün KİMDEN geldiğini söyleyemez. Cevap sürecin
         içindeki aktif sorgularda: her satır bir siteye bağlanır. -->
    <section class="panel">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.consumers.title") }}</h3>
          <p class="meta">{{ t("panel.consumers.subtitle", { name: consumerSourceLabel }) }}</p>
        </div>
        <label class="stacked-field consumer-source">
          <span class="field-label">{{ t("panel.consumers.source") }}</span>
          <select v-model="consumerSource" class="compact-select">
            <option value="mariadb">MariaDB</option>
            <option value="mysql">MySQL</option>
            <option value="postgresql">PostgreSQL</option>
            <option value="iis">IIS (web)</option>
          </select>
        </label>
      </div>

      <!-- CANLI ile GEÇMİŞ ayrı sekmeler: canlı liste "şu an ne oluyor", geçmiş "ne olmuştu"
           sorusunu cevaplar. İkisini tek listede karıştırmak, canlı ekranın okunabilirliğini
           bozardı. -->
      <div class="segmented-tabs consumer-tabs" role="tablist">
        <button role="tab" :aria-selected="consumerTab === 'live'" :class="{ active: consumerTab === 'live' }" @click="consumerTab = 'live'">
          {{ t("panel.consumers.tabLive") }}
        </button>
        <button role="tab" :aria-selected="consumerTab === 'history'" :class="{ active: consumerTab === 'history' }" @click="consumerTab = 'history'">
          {{ t("panel.consumers.tabHistory") }}
        </button>
      </div>

      <ResourceConsumers v-if="consumerTab === 'live'" :source="consumerSource" />
      <QueryHistory v-else />
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from "vue";
import ResourceMetric from "../components/ResourceMetric.vue";
import { useI18n } from "vue-i18n";
import { useSystemStore } from "../stores/system";
import { getProcessSnapshot, type ProcessSnapshot } from "../api/processes";
import QueryHistory from "../components/QueryHistory.vue";
import ResourceConsumers from "../components/ResourceConsumers.vue";
import type { ConsumerSource } from "../api/consumers";
import { formatBytes as sharedFormatBytes } from "../utils/bytes";
import { formatDateTime, formatTimeSpan as sharedFormatTimeSpan } from "../utils/datetime";

type DiskUsage = {
  name: string;
  usagePercent: number;
  freeBytes: number;
  usedBytes: number;
  totalBytes: number;
};

type NetworkInterface = {
  name: string;
  bytesReceivedPerSecond: number;
  bytesSentPerSecond: number;
};

type SystemOverview = {
  machineName: string;
  operatingSystem: string;
  osArchitecture: string;
  frameworkDescription: string;
  currentServerTimeUtc: string | null;
  bootTimeUtc: string;
  uptime: string;
  cpuUsagePercent: number;
  logicalProcessorCount: number;
  totalMemoryBytes: number;
  usedMemoryBytes: number;
  memoryUsagePercent: number;
  disks: DiskUsage[];
  networkInterfaces: NetworkInterface[];
};

const props = defineProps<{
  overview: SystemOverview | null;
  loading: boolean;
}>();

const { t } = useI18n();

const primaryDisk = computed(() => props.overview?.disks[0] ?? null);

/*
 * EN DOLU DİSK EN ÜSTTE — panodaki `sortedDisks` ile aynı gerekçe ve aynı kod.
 * Sunucudan gelen sıra sürücü harfine göredir (C, D, E...), yani dikkat isteyen satırın nerede
 * duracağı TESADÜFE kalıyordu: dördüncü sürücü dolduğunda uyarı listenin dibindeydi.
 * KOPYA ÜZERİNDE sıralanır: `sort` yerinde çalışır ve prop dizisini doğrudan sıralamak üst
 * bileşenin verisini (ve kaynak göstergesinin okuduğu `disks[0]`ı) değiştirirdi.
 */
const sortedDisks = computed(() => [...(props.overview?.disks ?? [])]
  .sort((a, b) => b.usagePercent - a.usagePercent));

// Plesk-benzeri "derin izleme": sunucu dakikada bir CPU/RAM/ağ örnekler (kalıcı geçmiş halkası); izleme ekranı
// bu geçmişi çekip küçük SVG sparkline'larla trend gösterir → sayfa yenilense de DOLU gelir (~24 saatlik pencere).
const systemStore = useSystemStore();
let historyTimer: number | null = null;

const cpuSeries = computed(() => systemStore.history.map((sample) => sample.cpuPercent));
const memSeries = computed(() => systemStore.history.map((sample) => sample.memoryPercent));
const netDownSeries = computed(() => systemStore.history.map((sample) => sample.networkDownBytesPerSec));
const netUpSeries = computed(() => systemStore.history.map((sample) => sample.networkUpBytesPerSec));

const cpuSpark = computed(() => spark(cpuSeries.value, 100));
const memSpark = computed(() => spark(memSeries.value, 100));
const netScale = computed(() => Math.max(1, ...netDownSeries.value, ...netUpSeries.value));
const netDownSpark = computed(() => spark(netDownSeries.value, netScale.value));
const netUpSpark = computed(() => spark(netUpSeries.value, netScale.value));
const hasNetTrend = computed(() => systemStore.history.length > 1);

/** Grafiğin sol ucu: pencereyi UYDURMAK yerine ilk örneğin kendi zaman damgasını yazar. */
const trendStartLabel = computed(() =>
  formatDateTime(systemStore.history[0]?.timestampUtc ?? null, t("panel.common.notAvailable")));

// --- "Şu an kim kullanıyor?" kaynak seçimi ---
const consumerSource = ref<ConsumerSource>("mariadb");
const consumerTab = ref<"live" | "history">("live");
const consumerSourceLabel = computed(() => ({
  mariadb: "MariaDB",
  mysql: "MySQL",
  postgresql: "PostgreSQL",
  iis: "IIS"
}[consumerSource.value]));

// --- Canlı süreç izleyici ---
const snapshot = ref<ProcessSnapshot | null>(null);

// Sıralama dondurma: iki saniyede bir gelen veri satırları yeniden sıralarsa, okumaya çalıştığın
// satır elinin altından kaçar. Fare/klavye tablonun üzerindeyken ESKİ SIRA korunur; değerler
// yerinde güncellenmeye devam eder. İmleç ayrılınca sıra tekrar canlıya döner.
const orderFrozen = ref(false);
const frozenOrder = ref<number[]>([]);

const orderedProcesses = computed(() => {
  const rows = snapshot.value?.processes ?? [];
  if (!orderFrozen.value) {
    return rows;
  }

  const byId = new Map(rows.map((row) => [row.processId, row]));
  // Önce donmuş sıradaki (hâlâ var olan) satırlar, sonra bu arada beliren yeni süreçler sona.
  // Yeni süreç araya girip mevcut satırları kaydırmaz.
  const kept = frozenOrder.value.map((pid) => byId.get(pid)).filter((row) => row !== undefined);
  const keptIds = new Set(kept.map((row) => row!.processId));
  return [...kept, ...rows.filter((row) => !keptIds.has(row.processId))] as typeof rows;
});

// Donmadığı sürece güncel sırayı hatırla ki donduğu an referans hazır olsun.
watch(snapshot, (value) => {
  if (!orderFrozen.value && value) {
    frozenOrder.value = value.processes.map((row) => row.processId);
  }
});
const processSort = ref<"cpu" | "memory">("cpu");
const processPaused = ref(false);
const processError = ref("");
// İlk anlık görüntü hâlâ gelmediyse cümlenin değişeceği an. 4 sn ölçüyle seçildi: yoklama
// aralığı 2 sn, yani iki turu kaçırmış olmak "yavaş" demek için yeterli kanıt.
const processWaitedLong = ref(false);
let processSlowTimer: number | null = null;
let processTimer: number | null = null;
let processInFlight = false;

async function pollProcesses() {
  // Sekme arka plandaysa veya kullanıcı duraklattıysa sunucuyu boşuna yorma.
  // Önceki istek hâlâ sürüyorsa yenisini kuyruğa alma (yavaş sunucuda istek yığılmasın).
  if (processPaused.value || document.hidden || processInFlight) {
    return;
  }

  processInFlight = true;
  try {
    snapshot.value = await getProcessSnapshot(processSort.value);
    processError.value = "";
  } catch {
    processError.value = t("panel.processes.failed");
  } finally {
    processInFlight = false;
  }
}

// Sıralama değişince beklemeden yenile.
watch(processSort, () => void pollProcesses());

onMounted(() => {
  void systemStore.fetchHistory();
  historyTimer = window.setInterval(() => void systemStore.fetchHistory(), 60000);

  void pollProcesses();
  processTimer = window.setInterval(() => void pollProcesses(), 2000);
  processSlowTimer = window.setTimeout(() => {
    processWaitedLong.value = true;
  }, 4000);
});

onUnmounted(() => {
  if (historyTimer !== null) {
    window.clearInterval(historyTimer);
    historyTimer = null;
  }

  if (processTimer !== null) {
    window.clearInterval(processTimer);
    processTimer = null;
  }

  if (processSlowTimer !== null) {
    window.clearTimeout(processSlowTimer);
    processSlowTimer = null;
  }
});

function cpuSeverity(percent: number) {
  if (percent >= 50) return "high";
  return percent >= 20 ? "medium" : "low";
}

/**
 * Süreç çalışma süresi.
 *
 * BİRİM ETİKETLERİ KODA GÖMÜLÜYDÜ ("g" / "sa" / "dk") ve bu, panelin İngilizcesinde de Türkçe
 * kısaltma basıyordu — üstelik aynı ekranın hemen üstündeki "Çalışma süresi" satırı
 * `utils/datetime.ts` üzerinden `panel.units.*Short` anahtarlarını kullanıyor. Yani AYNI EKRANDA
 * iki süre biçimi vardı ve biri dil değişimini görmüyordu. Anahtarlar zaten mevcut; kopya silindi.
 */
function formatUptime(seconds: number) {
  if (seconds <= 0) return "—";
  const days = Math.floor(seconds / 86400);
  const hours = Math.floor((seconds % 86400) / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  const gun = t("panel.units.dayShort");
  const saat = t("panel.units.hourShort");
  const dakika = t("panel.units.minuteShort");
  if (days > 0) return `${days}${gun} ${hours}${saat}`;
  if (hours > 0) return `${hours}${saat} ${minutes}${dakika}`;
  return `${minutes}${dakika}`;
}

// Değer dizisini 100×28 viewBox içinde bir polyline "points" dizgesine çevirir (üst=yüksek değer).
function spark(values: number[], maxValue: number): string {
  if (values.length < 2) {
    return "";
  }

  const max = maxValue > 0 ? maxValue : 1;
  const count = values.length;
  return values
    .map((value, index) => {
      const x = (index / (count - 1)) * 100;
      const ratio = Math.max(0, Math.min(1, value / max));
      const y = 27 - ratio * 26;
      return `${x.toFixed(1)},${y.toFixed(1)}`;
    })
    .join(" ");
}

/*
 * HALKA/SATIR EŞİKLERİ — panodaki (DashboardOverviewView) iki sabitle BİREBİR aynı.
 * İkinci bir eşik icat etmek, aynı diski panoda "kritik", izleme ekranında "iyi" gösterirdi.
 * Değerler de uydurma değil: Plesk ve cPanel'in kaynak göstergeleri de %80'de sarıya, %90'da
 * kırmızıya döner; sunucu yöneticisinin başka panellerden gelen refleksi bozulmasın.
 */
const METER_WARN_PERCENT = 80;
const METER_DANGER_PERCENT = 90;

/**
 * Disk satırının tonu — kaynak göstergesiyle aynı iki eşikten okunur.
 * Boş dize "eşiğin altında" demektir; o satır ne rozet alır ne ton, çünkü sağlıklı bir disk
 * hakkında söylenecek bir şey yoktur (her satırı yeşile boyamak gürültüdür).
 */
function diskTone(disk: DiskUsage): "" | "warn" | "danger" {
  if (disk.usagePercent >= METER_DANGER_PERCENT) {
    return "danger";
  }

  return disk.usagePercent >= METER_WARN_PERCENT ? "warn" : "";
}

/** Rozetin SÖZCÜĞÜ: rengin yanında ikinci (ve ekran okuyucunun okuduğu tek) kanal. */
function diskToneLabel(disk: DiskUsage): string {
  return diskTone(disk) === "danger"
    ? t("panel.common.severityCritical")
    : t("panel.common.severityWarning");
}

/** Tarih bicimi panelin geri kalaniyla AYNI (bkz. utils/datetime.ts). */
function formatUtc(value: string | null): string {
  return formatDateTime(value, t("panel.common.notAvailable"));
}

/** Bayt bicimi panelin geri kalaniyla AYNI (bkz. utils/bytes.ts); ekran kendi bicimini uydurmaz. */
function formatBytes(value: number) {
  return sharedFormatBytes(value, t);
}

function formatSpeed(value: number): string {
  return `${formatBytes(value)}/${t("panel.units.second")}`;
}

/** Sure bicimi panelin geri kalaniyla AYNI (bkz. utils/datetime.ts). */
function formatTimeSpan(value: string): string {
  return sharedFormatTimeSpan(value, t);
}
</script>

<style scoped>
/* 18px ÖLÇEK DIŞIYDI (bkz. styles/base.css `--sp-*`) ve panoda aynı liste 16'ya taşınmıştı;
   iki ekran yan yana açıldığında dikey ritimleri tutmuyordu. 16 SEÇİLDİ: `.panel`in kendi iç
   dolgusu da 16, yani panelden panele boşluk ile panelin içindeki boşluk artık AYNI sayı. */
.monitor-stack,
.monitor-columns,
.mini-table {
  display: grid;
  gap: var(--sp-16);
}

.monitor-columns {
  grid-template-columns: repeat(2, minmax(0, 1fr));

  /* PANELLER KENDİ YÜKSEKLİĞİNDE DURUR — panoda ölçülen hatanın AYNISI buradaydı.
     Izgaranın varsayılanı `stretch`: "Sunucu bilgisi" (6 satır) ile "Diskler" (çoğu sunucuda
     1-2 satır) yan yana ve kısa olan uzun olana GERİLİYORDU. Panoda bu, başlıkla içerik
     arasında ~180px boş alan olarak ölçülmüştü; burada da diskler paneli, tablosunun bittiği
     yerden sayfanın altına kadar boş kalıyordu. */
  align-items: start;
}

/* `.info-card` ve `.mini-row` bu listeden ÇIKARILDI: ilki şablondan tümüyle kalktı (panelin
   içinde ikinci bir çerçeveydi), ikincisi ise artık kendi kenarlığı ve kendi zemini olan bir
   KART değil, gerçek bir tablo satırı (gerekçesi `.mini-table` kuralında). */

.meter-spark {
  width: 100%;
  height: 30px;
  margin-top: var(--sp-12);
}

.meter-spark polyline {
  fill: none;
  stroke: var(--accent-1);
  stroke-width: 1.6;
  vector-effect: non-scaling-stroke;
  stroke-linejoin: round;
  stroke-linecap: round;
}

.meter-spark.mem polyline {
  stroke: var(--resource-memory);
}

/* Üst boşluk KALDIRILDI: kapsayıcı `.panel` zaten 12px ızgara aralığı veriyor ve margin ona
   EKLENİYORDU (12 + 16 = 28px, ölçek dışı bir sıçrama). */
.net-trend {
  display: grid;
  gap: var(--sp-8);
}

.net-trend-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

/* Y ekseni etiketleri grafiğin SOLUNDA ayrı bir sütun: SVG `preserveAspectRatio="none"` ile
   yatayda geriliyor, metni onun içine koymak harfleri de gerdirirdi. */
.net-plot {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr);
  gap: var(--sp-8);
  align-items: stretch;
}

.net-axis-y {
  display: flex;
  flex-direction: column;
  justify-content: space-between;
  padding-block: 2px;
  color: var(--text-muted);
  font-size: var(--fs-2xs);
  font-variant-numeric: tabular-nums;
  text-align: right;
  white-space: nowrap;
}

.net-axis-x {
  display: flex;
  justify-content: space-between;
  color: var(--text-muted);
  font-size: var(--fs-2xs);
}

.net-spark {
  width: 100%;
  height: 60px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  background: var(--surface-2);
}

/* Izgara ve taban çizgisi: veriyle YARIŞMAZ, veriyi konumlandırır. Kesikli çizgi ızgarayı
   eğrilerden ayırır; taban (sıfır) çizgisi kesiksiz, çünkü o bir ölçüm sınırıdır. */
.net-spark .net-grid,
.net-spark .net-axis {
  stroke: var(--panel-border);
  stroke-width: 1;
}

.net-spark .net-grid {
  stroke-dasharray: 3 3;
}

.net-spark polyline {
  fill: none;
  stroke-width: 1.6;
  vector-effect: non-scaling-stroke;
  stroke-linejoin: round;
  stroke-linecap: round;
}

.net-spark polyline.down {
  stroke: var(--accent-1);
}

.net-spark polyline.up {
  stroke: var(--warn-1);
}

.net-legend {
  display: flex;
  gap: var(--sp-16);
  color: var(--text-muted);
  font-size: var(--fs-2xs);
}

.net-legend .legend-dot {
  display: inline-block;
  width: 10px;
  height: 10px;
  border-radius: 50%;
  margin-right: var(--sp-6);
  vertical-align: middle;
}

.legend-dot.down {
  background: var(--accent-1);
}

.legend-dot.up {
  background: var(--warn-1);
}

.info-list {
  display: grid;
  gap: var(--sp-12);
  margin: 0;
}

.info-list div {
  display: grid;
  gap: var(--sp-4);
}

/* 0.9rem ölçek dışıydı; `--fs-md` (0.92rem) panonun aynı listesindeki değerdir. */
.info-list dt {
  color: var(--text-muted);
  font-size: var(--fs-md);
}

.info-list dd {
  margin: 0;
  font-weight: 600;
}

/* MİNİ TABLOLAR ARTIK GERÇEKTEN TABLO — panoda ölçülen hatanın aynısı buradaydı.
   `.mini-table` en üstteki ortak ızgara kuralından 18px `gap` alıyordu ve HER SATIRIN kendi
   kenarlığı + kendi zemini vardı. Üç diskin listesi böylece 18px aralıklı üç YÜZEN KARTA
   dönüşüyordu; üstelik başlık satırının zemini (`--surface-2`) veri satırlarınınkiyle BİREBİR
   aynı olduğu için başlık da onlardan ayrılmıyordu. Sütunlar hizalıydı ama göz bunu tek bir
   tablo olarak okuyamıyor, üç ayrı kutu sayıyordu.
   Panelin `.data-table`/`.data-row` kalıbına oturtuldu: dışta tek çerçeve, satırlar arasında
   ince ayraç, başlıkta ayrı zemin. Kazanılan dikey yer ölçülebilir: üç satırlık bir disk
   tablosu 2 x 18px gap + 4 x 1px kenar = 40px kısalır. */
.mini-table {
  gap: 0;
  overflow: hidden;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
}

.mini-row {
  display: grid;
  gap: var(--sp-12);
  align-items: center;
  padding: var(--sp-10) var(--sp-12);
  border-bottom: 1px solid var(--panel-border);
  background: var(--surface-0);
}

.mini-row:last-child {
  border-bottom: 0;
}

/* Kullanım sütunu artık iki parça taşıyor (yüzde + rozet), o yüzden daha geniş: 0.8fr -> 1.1fr.
   Kaynak ad sütunundan alınıyor (1.4 -> 1.1); disk adı "C:\" gibi 3-4 karakterlik bir dizedir,
   bu tablodaki en dar içeriğe sahip sütun odur. Panodaki oranlarla birebir aynı. */
.disk-row {
  grid-template-columns: 1.1fr 1.1fr 1fr;
}

/* SATIR TONU — üçüncü kanal. Renk çok hafif tutuldu (%6/%8): satır zemini yanındaki rozetin
   kendi dolgusunu bastırmamalı, işi yalnızca gözü doğru satıra çekmek.
   `--*-1` ile karıştırılıyor, `--*-text` ile DEĞİL: burada metin değil YÜZEY boyanıyor;
   satırın yazısı `--text-strong` kalır ve iki temada da okunur. */
.mini-row.warn {
  background: color-mix(in srgb, var(--warn-1) 6%, var(--surface-0));
}

.mini-row.danger {
  background: color-mix(in srgb, var(--danger-1) 8%, var(--surface-0));
}

.network-row {
  grid-template-columns: 1.2fr 0.9fr 0.9fr 0.9fr;
}

/* SAYI SÜTUNLARI SAĞA HİZALI, RAKAMLAR TEK GENİŞLİKTE.
   Her iki tabloda da ilk sütun bir AD (disk harfi, arayüz adı), kalanların hepsi SAYI:
   "%12.4 / %7.0 / %100.0" ve "1,2 GB/sn". Sola hizalıyken ondalık ayraçlar birbirini tutmuyor
   ve hangi satırın büyük olduğu ancak OKUNARAK anlaşılıyordu; sağa hizalı ve `tabular-nums`
   ile aynı basamak alt alta gelir, karşılaştırma bakışla olur. Başlıklar da hizaya dahil.
   Kullanım hücresindeki rozet `inline-flex` olduğu için `text-align` onu da sağa iter; ayrı
   bir flex kapsayıcı sınıfına GEREK YOK (panoda `.disk-usage` bu yüzden vardı).
   `:nth-child` KULLANILIYOR, `:not()` değil — bu depoda özgüllüğü yükselten çıplak `:not()`
   için nöbetçi var (CssSpecificityGuardTests) ve niyet zaten "ikinciden itibaren". */
.disk-row > span:nth-child(n + 2),
.network-row > span:nth-child(n + 2) {
  text-align: right;
  font-variant-numeric: tabular-nums;
}

/* Başlık satırı base.css'teki `.data-row.table-head` ile AYNI dili konuşur (aynı renk, aynı
   boyut, aynı kalınlık): panelde iki farklı "tablo başlığı" görünümü olmasın. Eski hâli
   700 kalınlıkta ve gövde boyundaydı, yani veri satırından yalnız kalınlıkla ayrılıyordu. */
.table-head {
  color: var(--text-soft);
  font-size: var(--fs-xs);
  font-weight: 600;
  background: var(--surface-2);
}
@media (max-width: 1180px) {
  .monitor-columns {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 820px) {
  .section-head {
    flex-direction: column;
    align-items: flex-start;
  }

  /*
    Disk/ağ satırları tek sütuna inmiyor: başlık satırı da tek sütuna inince değerlerden
    kopuyor ve "hangi sayı neydi" okunamıyordu. Sütunlar korunup yazı/boşluk daraltılıyor.
  */
  .mini-row {
    gap: var(--sp-8);
    padding: var(--sp-8) var(--sp-10);
    font-size: var(--fs-xs);
  }

  .mini-row span {
    min-width: 0;
    overflow-wrap: anywhere;
  }

  /* Kullanım sütunu dar ekranda da rozeti taşımalı. Alan disk adı sütunundan alınıyor: "C:\"
     üç karakter, buradaki en dar içerik odur. Rozet yine sığmazsa satır iki satıra çıkar —
     KIRPMA YOK. */
  .disk-row {
    grid-template-columns: minmax(0, 0.9fr) 1fr 0.9fr;
  }

  /* "Toplam" sütunu gizlenir: indir+yükle toplamıdır, iki sütunu da ekranda tutmak
     türetilebilir bir sayıyı göstermekten önemli. */
  .network-row {
    grid-template-columns: minmax(0, 1.1fr) 0.9fr 0.9fr;
  }

  .network-row > :nth-child(4) {
    display: none;
  }
}
@media (max-width: 700px) {

  /* Aynı bilgi hemen aşağıdaki Diskler tablosunda var. */

  .meter-spark {
    height: 22px;
    margin-top: var(--sp-8);
  }

  .net-legend {
    gap: var(--sp-12);
    font-size: var(--fs-2xs);
  }
}

/* Üst boşluk KALDIRILDI: kapsayıcı `.panel` zaten 12px ızgara aralığı veriyor ve margin ona
   EKLENİYORDU (12 + 10 = 22px, ölçek dışı). */
.proc-table {
  display: grid;
  gap: var(--sp-4);
}

/* Sabit görüntü alanı: satır sayısı 8'e de düşse 30'a da çıksa bölümün yüksekliği DEĞİŞMEZ,
   dolayısıyla altındaki "kim kullanıyor" bölümü yerinde kalır. Liste uzunsa içeride kaydırılır. */
.proc-scroll {
  height: 420px;
  overflow-y: auto;
  overscroll-behavior: contain;
  padding-right: var(--sp-4);
}

.proc-body {
  position: relative;
  display: grid;
  gap: var(--sp-4);
}

/* Satırlar ışınlanmak yerine kayarak yer değiştirsin; göz takip edebilsin. */
.proc-move {
  transition: transform 320ms ease;
}

.proc-enter-active,
.proc-leave-active {
  transition: opacity 200ms ease;
}

.proc-enter-from,
.proc-leave-to {
  opacity: 0;
}

/* Ayrılan satır akıştan çıkarılır, yoksa kalanları aşağı iterek yeni bir zıplama yaratır. */
.proc-leave-active {
  position: absolute;
  width: 100%;
}

@media (prefers-reduced-motion: reduce) {
  .proc-move,
  .proc-enter-active,
  .proc-leave-active {
    transition: none;
  }
}

/* radius 9px ölçek dışıydı; `--radius-md` TAM OLARAK 9px, yani görünüm birebir aynı kalır ve
   değer artık tema/ölçek kaynağından gelir. */
.proc-row {
  display: grid;
  grid-template-columns: minmax(0, 2.4fr) 0.6fr 1fr 0.8fr 0.8fr;
  gap: var(--sp-10);
  align-items: center;
  padding: var(--sp-8) var(--sp-10);
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--surface-1);
}

/* Başlık satırı panelin ORTAK tablo başlığı diliyle konuşur (base.css `.data-row.table-head`):
   aynı renk (`--text-soft`), aynı boy (`--fs-xs`), aynı kalınlık. Eskiden `--text-muted` +
   0.74rem ile ipucu metninden bile silikti — beş sütunun ADI, sütunların İÇERİĞİNDEN daha zor
   okunuyordu. Zemini BİLEREK saydam kalıyor: bu tablo çerçevesiz kartlardan oluşuyor, başlığa
   zemin vermek onu altıncı bir karta çevirirdi. */
.proc-row.head {
  border: 0;
  background: transparent;
  padding-bottom: 0;
  font-size: var(--fs-xs);
  font-weight: 600;
  color: var(--text-soft);
}

/* SAYI SÜTUNLARI SAĞA HİZALI — başlıklar dahil.
   PID (4-5 hane), CPU yüzdesi, bellek ("1,2 GB") ve süre; dördü de sayı. Sola hizalıyken
   "1284" ile "7" aynı noktadan başlıyor ve 30 satırlık canlı listede hangisinin büyük olduğu
   ancak okunarak anlaşılıyordu. `tabular-nums` zaten vardı (`.proc-num`), eksik olan hizaydı;
   üstelik iki saniyede bir güncellenen bir listede sola hizalı değişken genişlikli sayı,
   sütunun sağ kenarını sürekli oynatıyordu. */
.proc-row > :nth-child(n + 2) {
  text-align: right;
}

.proc-owner {
  display: grid;
  gap: 1px;
  min-width: 0;
}

.proc-owner strong {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.proc-owner small {
  color: var(--text-muted);
  font-size: var(--fs-2xs);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.proc-num {
  position: relative;
  font-variant-numeric: tabular-nums;
  font-size: var(--fs-sm);
}

/* CPU çubuğu satırın arkasında ince bir dolgu: sıralama değişse de göz oranı yakalar. */
.proc-bar {
  position: absolute;
  left: 0;
  right: 0;
  bottom: calc(-1 * var(--sp-4));
  height: 3px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--text-muted) 20%, transparent);
}

.proc-bar::after {
  content: "";
  position: absolute;
  left: 0;
  top: 0;
  bottom: 0;
  width: var(--fill, 0%);
  border-radius: 999px;
  background: var(--ok-1);
}

.proc-bar.medium::after {
  background: var(--warn-1);
}

.proc-bar.high::after {
  background: var(--danger-1);
}

/*
  Canlı süreç tablosu telefonda: PID ve çalışma süresi gizlenir (PID telefondan zaten
  kullanılamaz, süre teşhis için ikincil), SAHİP + CPU + BELLEK kalır — "sunucuyu ne yiyor"
  sorusunun cevabı bu üçü. CPU çubuğu da korunur.
*/
@media (max-width: 720px) {
  .proc-row {
    grid-template-columns: minmax(0, 1.7fr) 0.8fr 0.9fr;
    gap: var(--sp-8);
    padding: var(--sp-8) var(--sp-10) var(--sp-10);
  }

  .proc-row > :nth-child(2),
  .proc-row > :nth-child(5) {
    display: none;
  }

  .proc-num {
    font-size: var(--fs-xs);
  }

  .proc-owner strong {
    font-size: var(--fs-sm);
  }

  .proc-owner small {
    font-size: var(--fs-2xs);
  }

  .row-actions {
    width: 100%;
  }
}
</style>
