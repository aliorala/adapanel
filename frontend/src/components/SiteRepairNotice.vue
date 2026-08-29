<template>
  <!-- Hiç bulgu yoksa HİÇBİR ŞEY gösterilmez. Sürekli duran bir "her şey yolunda" şeridi,
       gerçekten bir sorun çıktığında fark edilmeyi zorlaştırır. -->
  <!--
    KATLANIR KUTU (gizlenmez).

    ÖLÇÜLDÜ: site Pano'sunda içeriğe inmeden önce üst üste üç alarm kutusu duruyordu ve
    toplamı ~470px idi; site sekmeleri ancak bundan sonra başlıyordu. Bu kutu artık madde
    sayısını başlıkta yazar, listeyi `<details>` içinde tutar.

    AÇIK/KAPALI KARARI ÖNEME BAĞLI, kullanıcı tercihine değil: gerçekten bozuk bir şey
    varsa ("fail") liste AÇIK açılır — ciddi bir bulgu bir tıkın arkasına saklanamaz.
    Yalnız uyarı varsa ("warn") kapalı başlar; sayı ve onarma düğmesi yine görünür.
  -->
  <details v-if="findings.length" class="repair-notice" :class="worstSeverity" :open="worstSeverity === 'fail'">
    <summary class="repair-head">
      <strong>{{ t("panel.health.siteNeedsRepair") }}</strong>
      <span class="repair-count">{{ t("panel.health.siteFindingCount", { count: findings.length }) }}</span>
      <!-- Düğme `<summary>` içinde: tıklaması katlamayı TETİKLEMEMELİ, yoksa "Onar"a
           basan kullanıcı listeyi de kapatmış olur. -->
      <button class="secondary compact" v-bind="writeGate(busy)" @click.stop.prevent="runRepair">
        {{ busy ? t("panel.health.siteRepairing") : t("panel.health.siteRepair") }}
      </button>
    </summary>

    <ul class="repair-list">
      <li v-for="(finding, index) in findings" :key="finding.key + index" :class="finding.severity">
        <span class="repair-text">{{ t(finding.key) }}</span>
        <!-- Onarıldı / onay bekliyor ayrımı GÖRÜNÜR olmalı: kullanıcı "panel halletmiş"
             sanıp bir sorunu olduğu yerde bırakmasın. -->
        <span v-if="finding.repaired" class="repair-tag ok">{{ t("panel.health.findingRepaired") }}</span>
        <span v-else-if="finding.needsConfirmation" class="repair-tag warn">
          {{ t("panel.health.findingNeedsConfirmation") }}
        </span>
        <small v-if="finding.detail" class="repair-detail">{{ describeDetail(finding.detail) }}</small>
      </li>
    </ul>
  </details>
</template>

<script setup lang="ts">
/**
 * Site sayfasındaki onarma uyarısı.
 *
 * Neden gerekli: onarma yalnız Sunucu Sağlığı ekranında toplu olarak vardı. Kullanıcı orada
 * "2 alan adı eski" yazısını görüyor ama HANGİ site olduğunu öğrenemiyordu. Sorun, sorunun
 * bulunduğu sayfada görünmeli.
 */
import { onMounted, ref, watch, computed } from "vue";
import { useI18n } from "vue-i18n";
import { inspectSiteRepair, repairSite, type RepairFinding } from "../api/siteHealth";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";
import { looksLikeMessageKey, splitCompositeMessage } from "../utils/apiError";

const props = defineProps<{ siteName: string }>();
const emit = defineEmits<{ (event: "notify", message: string, isError: boolean): void }>();

const i18n = useI18n();
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts).
const { writeGate } = useSubscriptionGate();
// Yer tutuculu metinler ("{count} bulgu") de bu sarmalayicidan gectigi icin PARAMETRE alir;
// parametresiz cagri davranisi degismedi.
const t = (key: string, params?: Record<string, unknown>) => {
  const value = params ? i18n.t(key, params) : i18n.t(key);
  return typeof value === "string" ? value : key;
};

const findings = ref<RepairFinding[]>([]);
const busy = ref(false);

/**
 * Bulgunun AYRINTI satırını okunur hâle getirir.
 *
 * ÖLÇÜLEN HATA: kutuda kullanıcıya ham anahtar gösteriliyordu — `mail.not_configured`.
 * Kaynağı arka uçtaki `AddMailFindingsAsync`: MX kaydı varken posta sunucusuna sorulamazsa
 * yakalanan istisnanın `Message`'ı ayrıntı olarak gönderiliyor, o mesaj da AppException'ın
 * KODU oluyor. Yani ayrıntı bazen serbest metin, bazen bir çeviri anahtarı.
 *
 * Anahtarın Türkçe karşılığı sözlükte ZATEN VARDI ("E-posta sunucusu bağlantısı henüz hazır
 * değil…"); eksik olan tek şey `t()` çağrısıydı. Ayırt etme işi panelin başka 57 ekranında
 * kullanılan aynı kurala bırakıldı (utils/apiError): küçük harfle başlayan, noktalı, boşluksuz
 * dizge anahtar sayılır — "Failed to open zone file" gibi gerçek cümleler dokunulmadan geçer.
 * Çeviri bulunamazsa dizge OLDUĞU GİBİ kalır: teşhis bilgisi hiçbir durumda kaybolmaz.
 */
function describeDetail(detail: string): string {
  const raw = (detail ?? "").trim();
  if (!raw) {
    return raw;
  }

  // Arka uç bazen "anahtar — serbest metin" biçiminde bileşik gönderiyor; ikisi ayrılır.
  const { key, detail: rest } = splitCompositeMessage(raw);
  if (!looksLikeMessageKey(key)) {
    return raw;
  }

  const translated = t(key);
  const head = translated === key ? key : translated;
  return rest ? `${head} — ${rest}` : head;
}

const worstSeverity = computed(() => (findings.value.some((item) => item.severity === "fail") ? "fail" : "warn"));

async function load() {
  try {
    const result = await inspectSiteRepair(props.siteName);
    findings.value = result.findings ?? [];
  } catch {
    // Denetim başarısızsa SESSİZ kal: uyarı şeridi ana işi engellememeli. Gerçek hata
    // kullanıcı "Onar" dediğinde gösterilir.
    findings.value = [];
  }
}

async function runRepair() {
  busy.value = true;
  try {
    const result = await repairSite(props.siteName);
    findings.value = result.findings ?? [];
    emit("notify", t("panel.health.siteRepaired"), false);
  } catch {
    emit("notify", t("panel.health.siteRepair"), true);
  } finally {
    busy.value = false;
  }
}

onMounted(load);
watch(() => props.siteName, load);
</script>

<style scoped>
.repair-notice {
  border: 1px solid var(--warn-1);
  border-radius: 8px;
  padding: 0.75rem 1rem;
  margin: 0.75rem 0;
  background: var(--warn-bg, rgba(224, 180, 0, 0.08));
}

.repair-notice.fail {
  border-color: var(--danger-1);
  background: var(--danger-bg);
}

/* `<summary>` varsayılan üçgen işaretini KENDİ hizasında tutar; `display: flex` onu
   tarayıcıya göre kaybettirdiği için işaret ::marker yerine elle çizilir (aşağıda). */
.repair-head {
  display: flex;
  align-items: center;
  gap: 0.6rem;
  cursor: pointer;
  list-style: none;
}

.repair-head::-webkit-details-marker {
  display: none;
}

/* Katlama işareti: açıkken aşağı, kapalıyken sağa bakar. */
.repair-head::before {
  content: "▸";
  flex: 0 0 auto;
  color: var(--text-muted);
}

.repair-notice[open] > .repair-head::before {
  content: "▾";
}

/* Başlık ile düğme arasındaki boşluğu sayı doldurur; düğme sağa yaslı kalır. */
.repair-head strong {
  min-width: 0;
}

.repair-count {
  margin-right: auto;
  color: var(--text-muted);
  font-size: 0.85em;
}

.repair-list {
  margin: 0.5rem 0 0;
  padding-left: 1.1rem;
}

.repair-list li {
  margin: 0.25rem 0;
}

.repair-tag {
  margin-left: 0.5rem;
  font-size: 0.8em;
  padding: 0.05rem 0.4rem;
  border-radius: 4px;
}

.repair-tag.ok {
  background: rgba(60, 160, 90, 0.18);
}

.repair-tag.warn {
  background: rgba(224, 180, 0, 0.2);
}

.repair-detail {
  display: block;
  opacity: 0.7;
}
</style>
