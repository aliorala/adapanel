<template>
  <!--
    ÖLÇÜM BAŞARISIZSA ŞERİT KAYBOLMAZ.

    Eskiden `health` null kalıyor ve bütün şerit sessizce yok oluyordu: kullanıcı "dışarıdan
    görünüm" diye bir şeyin var olduğunu bile bilmiyordu. Oysa bu bileşenin varlık sebebi tam
    olarak "ölçemedik"i "sorun yok"tan ayırmak. Üç durum artık ayrı: ölçülüyor / ölçüm sonucu /
    ölçüm yapılamadı (+ tekrar dene).
  -->
  <section v-if="loading || health || loadError.message.value" class="site-health-strip" :aria-busy="loading ? 'true' : 'false'">
    <!--
      ÜÇ SATIR İKİYE İNDİ.

      ÖLÇÜLDÜ: site Pano'sunda bu şerit, "dikkat" kutusu ve onarma kutusuyla birlikte içeriğin
      üstünde ~470px kaplıyordu. Şeridin kendisi üç satırdı: başlık, rozetler, altta ölçüm
      zamanı + "Yenile". Ölçüm zamanı ve yenileme başlık satırının SAĞ ucuna alındı — ikisi de
      şeridin ÜST BİLGİSİ, rozetlerin devamı değil. Hiçbir bilgi çıkarılmadı.
    -->
    <div class="health-head">
      <span class="health-label">
        {{ label("title", "Dışarıdan görünüm") }}
        <FieldHint
          :label="label('title', 'Dışarıdan görünüm')"
          :text="label('hint', 'Bu rozetler sunucunun kendi servis durumunu değil, alan adının dışarıdan görünen davranışını gösterir.')" />
      </span>
      <span v-if="health?.domain" class="meta health-domain">{{ health.domain }}</span>

      <span class="health-actions">
        <span v-if="health" class="meta health-checked">
          {{ label("checkedAt", "Ölçüm: {time}", { time: checkedAtText }) }}
          <template v-if="health.durationMs > 0">· {{ health.durationMs }} ms</template>
        </span>
        <!-- OTOMATİK POLLING YOK: ölçüm dış ağa çıkar, yalnız istendiğinde yenilenir. -->
        <button type="button" class="secondary compact health-refresh" :disabled="loading" @click="load">
          {{ loading ? label("measuring", "Dışarıdan ölçülüyor…") : label("refresh", "Yenile") }}
        </button>
      </span>
    </div>

    <!-- Ölçüm birkaç saniye sürer: ilk yüklemede iskelet. -->
    <div v-if="loading && !health" class="health-badges" role="status">
      <span v-for="index in 4" :key="index" class="health-badge skeleton" aria-hidden="true"></span>
      <span class="meta measuring">{{ label("measuring", "Dışarıdan ölçülüyor…") }}</span>
    </div>

    <div v-else-if="health" class="health-badges">
      <span
        v-for="badge in badges"
        :key="badge.id"
        :class="['status-pill', 'health-badge', badge.status]"
        :title="badge.detail || badge.value">
        <span class="badge-label">{{ badge.label }}</span>
        <span v-if="badge.value" class="badge-value">{{ badge.value }}</span>
        <span class="badge-mark" :aria-label="statusLabel(badge.status)">{{ mark(badge.status) }}</span>
        <FieldHint
          v-if="badge.detail"
          align="end"
          :label="`${badge.label} — ${statusLabel(badge.status)}`"
          :text="badge.detail" />
      </span>

      <!-- Posta kayıtları: MX / SPF / DKIM / DMARC tek grup halinde. -->
      <span v-if="mailBadges.length > 0" class="mail-group">
        <span class="meta mail-caption">{{ label("mail", "Posta") }}:</span>
        <span
          v-for="badge in mailBadges"
          :key="badge.id"
          :class="['status-pill', 'health-badge', badge.status]"
          :title="badge.detail || badge.value">
          <span class="badge-label">{{ badge.label }}</span>
          <span class="badge-mark" :aria-label="statusLabel(badge.status)">{{ mark(badge.status) }}</span>
          <FieldHint
            v-if="badge.detail"
            align="end"
            :label="`${badge.label} — ${statusLabel(badge.status)}`"
            :text="badge.detail" />
        </span>
      </span>
    </div>

    <SectionLoadError
      v-else-if="loadError.message.value"
      :message="loadError.message.value"
      :busy="loading"
      @retry="load" />
  </section>
</template>

<script setup lang="ts">
import { getFormattingLocale } from '../utils/locale';
import { computed, onMounted, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { getSiteHealth, type SiteHealth, type SiteHealthCheck, type SiteHealthStatus } from "../api/siteHealth";
import { useSectionError } from "../composables/useNotice";
import FieldHint from "./FieldHint.vue";
import SectionLoadError from "./SectionLoadError.vue";

/**
 * Bir sitenin DIŞARIDAN görünen sağlığını tek satırda gösteren şerit.
 * Sebep (2026-07-23): DNS servisi yalnız loopback'e bind kalmıştı; panel her şeyi yeşil
 * gösterdiği için sorun ALTI GÜN fark edilmedi. Buradaki rozetler panelin iç bilgisini değil,
 * dış gözlemi gösterir — "unknown" ölçemedik demektir, "sorun yok" demek değildir.
 */
const props = defineProps<{ siteName: string }>();
const { t } = useI18n();

const health = ref<SiteHealth | null>(null);
const loading = ref(false);
// Ölçüm başarısız → "okunamadı" durumu; BOŞ durumdan ayrı tutulur ve tekrar denenebilir.
const loadError = useSectionError();
let requestId = 0;

// i18n anahtarları başka bir ajanda; anahtar henüz yoksa ham anahtar yerine TR metni göster
// (SiteDetailView.translateState ile aynı kalıp).
function label(suffix: string, fallback: string, params?: Record<string, unknown>): string {
  const key = `panel.siteHealth.${suffix}`;
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

const statusOrder: SiteHealthStatus[] = ["ok", "warn", "fail", "unknown"];

function normalize(status: string | undefined): SiteHealthStatus {
  const value = (status ?? "").toLowerCase() as SiteHealthStatus;
  return statusOrder.includes(value) ? value : "unknown";
}

function mark(status: SiteHealthStatus): string {
  if (status === "ok") return "✓";
  if (status === "fail") return "✗";
  if (status === "warn") return "!";
  return "?";
}

function statusLabel(status: SiteHealthStatus): string {
  const fallbacks: Record<SiteHealthStatus, string> = {
    ok: "sorunsuz",
    warn: "uyarı",
    fail: "hata",
    unknown: "ölçülemedi"
  };
  return label(`status.${status}`, fallbacks[status]);
}

type Badge = {
  id: string;
  label: string;
  value: string;
  status: SiteHealthStatus;
  detail: string;
};

function detailOf(check: SiteHealthCheck | undefined, status: SiteHealthStatus): string {
  const parts = [check?.value ?? "", check?.detail ?? ""].map((part) => part.trim()).filter(Boolean);
  if (parts.length === 0) {
    return status === "unknown" ? label("notMeasured", "Bu kontrol ölçülemedi.") : "";
  }
  return parts.join(" — ");
}

function toBadge(id: string, text: string, check: SiteHealthCheck | undefined, value?: string): Badge {
  const status = normalize(check?.status);
  return {
    id,
    label: text,
    value: value ?? check?.value ?? "",
    status,
    detail: detailOf(check, status)
  };
}

// SSL rozetinde ham değer yerine kalan gün: "87 gün" / "süresi doldu".
const certificateText = computed(() => {
  const data = health.value;
  if (!data) {
    return "";
  }
  const days = data.certificateDaysLeft;
  if (days === null || days === undefined) {
    return data.https?.value ?? "";
  }
  return days < 0
    ? label("certExpired", "süresi doldu")
    : label("certDays", "{days} gün", { days });
});

const badges = computed<Badge[]>(() => {
  const data = health.value;
  if (!data) {
    return [];
  }
  return [
    toBadge("dns", label("dns", "DNS"), data.dnsResolves),
    // Asıl olay buydu: alan adı çözülüyor ama BAŞKA sunucuyu gösteriyor olabilir.
    toBadge("dnsMatch", label("dnsMatch", "Sunucu eşleşmesi"), data.dnsMatchesServer),
    toBadge("http", label("http", "HTTP"), data.http),
    toBadge("ssl", label("ssl", "SSL"), data.https, certificateText.value)
  ];
});

// Posta rozetleri kısa kalsın (MX/SPF/DKIM/DMARC); ham kayıt ipucu metnine taşınır.
const mailBadges = computed<Badge[]>(() => {
  const data = health.value;
  if (!data) {
    return [];
  }
  const records = data.mailRecords ?? [];
  if (records.length === 0) {
    const summary = toBadge("mail", label("mailSummary", "Kayıtlar"), data.mail);
    return normalize(data.mail?.status) === "unknown" && !data.mail?.value ? [] : [summary];
  }
  return records.map((record) => {
    const suffix = (record.key ?? "").split(".").pop() ?? "";
    return { ...toBadge(`mail-${suffix || record.key}`, suffix.toUpperCase(), record), value: "" };
  });
});

const checkedAtText = computed(() => {
  const raw = health.value?.checkedAtUtc ?? "";
  if (!raw) {
    return "";
  }
  // Sunucu UTC gönderir; sonek yoksa UTC olarak yorumla, sonra yerel saate çevir.
  const normalized = /[zZ]|[+-]\d{2}:\d{2}$/.test(raw) ? raw : `${raw}Z`;
  const date = new Date(normalized);
  return Number.isNaN(date.getTime()) ? "" : date.toLocaleTimeString(getFormattingLocale());
});

async function load() {
  const siteName = props.siteName?.trim();
  if (!siteName) {
    health.value = null;
    loadError.clear();
    return;
  }

  const current = ++requestId;
  loading.value = true;
  try {
    const result = await getSiteHealth(siteName);
    if (current !== requestId) {
      return;
    }
    health.value = result;
    loadError.clear();
  } catch (error) {
    // Hata YUTULMAZ: ölçüm yapılamadığını söylemek, ölçüm yokmuş gibi davranmaktan iyidir.
    if (current === requestId) {
      health.value = null;
      loadError.capture(error);
    }
  } finally {
    if (current === requestId) {
      loading.value = false;
    }
  }
}

onMounted(load);

// Site değişince tek seferlik yeniden ölçüm (polling değil).
watch(() => props.siteName, () => {
  health.value = null;
  void load();
});
</script>

<style scoped>
/*
 * YAZI OLCEGI TABANI: bes ham `rem` degeri (0.74 / 0.76 / 0.78rem = 9.99-10.53px) `--fs-2xs`e
 * baglandi. Ucu tabanin altindaydi; aralarindaki 0.54px fark zaten gorulebilir degildi.
 */
.site-health-strip {
  display: grid;
  gap: 8px;
  padding: 9px 12px;
  border: 1px solid var(--panel-border);
  border-radius: 11px;
  background: var(--surface-1);
}

.health-head {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.health-label {
  display: inline-flex;
  align-items: center;
  color: var(--text-muted);
  font-size: var(--fs-2xs);
  font-weight: 700;
  letter-spacing: 0.02em;
  text-transform: uppercase;
}

.health-domain {
  font-size: 0.8rem;
  word-break: break-all;
}

/* Dar ekranda rozetler alt alta sarar. */
.health-badges {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px 8px;
  min-width: 0;
}

.health-badge {
  gap: 5px;
  max-width: 100%;
  font-size: var(--fs-2xs);
}

.health-badge .badge-label {
  font-weight: 700;
}

.health-badge .badge-value {
  max-width: 22ch;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-weight: 500;
}

.health-badge .badge-mark {
  font-weight: 800;
}

/* fail → mevcut "danger" rengi; ok/warn zaten status-pill sınıflarından gelir. */
.health-badge.fail {
  background: var(--danger-bg);
  color: var(--danger-text);
  border-color: color-mix(in srgb, var(--danger-1) 30%, transparent);
}

/* unknown ASLA yeşil gösterilmez: "ölçemedik" nötr/soluk kalır. */
.health-badge.unknown {
  background: var(--surface-1);
  color: var(--text-muted);
  border-color: var(--panel-border);
  border-style: dashed;
}

.health-badge.ok {
  border-color: color-mix(in srgb, var(--ok-1) 30%, transparent);
}

.mail-group {
  display: inline-flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
  min-width: 0;
}

.mail-caption {
  font-size: var(--fs-2xs);
  font-weight: 700;
}

.skeleton {
  width: 84px;
  height: 22px;
  border-radius: 999px;
  border: 1px solid var(--panel-border);
  background: linear-gradient(90deg, var(--surface-1), color-mix(in srgb, var(--text-muted) 16%, var(--surface-1)), var(--surface-1));
  background-size: 200% 100%;
  animation: health-shimmer 1.2s ease-in-out infinite;
}

.measuring {
  font-size: var(--fs-2xs);
}

@keyframes health-shimmer {
  from { background-position: 200% 0; }
  to { background-position: -100% 0; }
}

@media (prefers-reduced-motion: reduce) {
  .skeleton {
    animation: none;
  }
}

/* Ölçüm zamanı + yenileme TEK KUTUDA ve başlık satırının sağ ucunda: tek bir
   `margin-left: auto` ikisini birlikte sağa yaslar. Ayrı ayrı `auto` verilseydi boş alanı
   PAYLAŞIRLAR ve aralarında geniş bir boşluk açılırdı. Ölçüm zamanı hiç yokken (ilk yükleme
   ya da okunamadı) düğme yine sağda kalır. Dar ekranda başlık satırı zaten sarıyor. */
.health-actions {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  margin-left: auto;
}

.health-checked {
  font-size: var(--fs-2xs);
}

.health-refresh {
  width: auto;
  flex: 0 0 auto;
}
</style>
