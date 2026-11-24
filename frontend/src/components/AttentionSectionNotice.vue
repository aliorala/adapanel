<template>
  <p v-if="repairResult" :class="['notice', { error: !repairResult.verified }]" role="status">{{ repairResult.message }}</p>
  <!-- Madde yoksa HİÇBİR ŞEY gösterilmez. Sürekli duran bir "her şey yolunda" kutusu,
       gerçek bir sorun çıktığında fark edilmeyi zorlaştırır. -->
  <!--
    KATLANIR KUTU (gizlenmez).

    ÖLÇÜLDÜ: site Pano'sunda bu kutu, dışarıdan görünüm şeridi ve onarma kutusuyla birlikte
    içeriğin üstünde ~470px yer kaplıyordu. Madde sayısı artık başlıkta duruyor, liste
    `<details>` içinde. Açık/kapalı kararı ÖNEME bağlı: kritik bir madde varsa liste AÇIK
    açılır, yalnız uyarı varsa kapalı başlar — sayı ve "Tümünü gör" her hâlükârda görünür.
  -->
  <details v-if="items.length" class="attention-notice" :class="worstSeverity" :open="worstSeverity === 'critical'">
    <summary class="attention-head">
      <strong>{{ t("panel.attention.noticeTitle") }}</strong>
      <span class="attention-count">{{ t("panel.attention.noticeCount", { count: items.length }) }}</span>
      <!-- Çekmeceyi açan düğme katlamayı TETİKLEMEZ: `<summary>` içindeki tıklama
           varsayılan olarak kutuyu açıp kapatır, kullanıcı ise başka bir ekrana gidiyor. -->
      <button class="ghost compact" type="button" @click.stop.prevent="emit('open')">
        {{ t("panel.attention.openDrawer") }}
      </button>
    </summary>

    <ul class="attention-list">
      <li v-for="item in items" :key="item.id" :class="item.severity">
        <span class="attention-name">{{ t(item.titleKey) }}</span>
        <!-- Açıklama "ne yapılabilir"i söyler; başlık tek başına yalnızca sorunun adıdır. -->
        <span v-if="item.descriptionKey" class="attention-desc">{{ t(item.descriptionKey) }}</span>
        <!-- Teknik ayrıntı (sürüm, servis adı) çeviriye tabi değildir; olduğu gibi gösterilir. -->
        <small v-if="item.detail" class="attention-detail">{{ item.detail }}</small>
        <SslBindingRepair v-if="item.sslRepair" :target="item.sslRepair" @result="repairResult = $event" @refresh="emit('refresh')" />
      </li>
    </ul>
  </details>
</template>

<script setup lang="ts">
/**
 * Bulunduğunuz sayfaya ait "dikkat" maddelerini sayfanın en üstünde madde madde gösterir.
 *
 * Neden gerekli: menüdeki rozet yalnızca bir SAYI gösteriyordu. Kullanıcı "3" görüyor, tıklıyor
 * ve sayfada neyin eksik olduğunu kendisi aramak zorunda kalıyordu. Sorun, sorunun bulunduğu
 * sayfada ve adıyla yazmalı.
 *
 * Tek bir yerden beslenir (MainLayout'un banner yuvası), bu yüzden hedef sayfaların hiçbirine
 * ayrı ayrı eklemek gerekmez.
 */
import { computed, ref } from "vue";
import { useI18n } from "vue-i18n";
import type { AttentionItem } from "../api/attention";
import SslBindingRepair from './SslBindingRepair.vue';

const props = defineProps<{ items: AttentionItem[] }>();
const repairResult = ref<{ message: string; verified: boolean } | null>(null);
const emit = defineEmits<{ (event: "open"): void; (event: "refresh"): void }>();

const i18n = useI18n();
// Yer tutuculu metinler ("{count} madde") de bu sarmalayicidan gectigi icin PARAMETRE alir;
// parametresiz cagri davranisi degismedi.
const t = (key: string, params?: Record<string, unknown>) => {
  const value = params ? i18n.t(key, params) : i18n.t(key);
  return typeof value === "string" ? value : key;
};

const items = computed(() => props.items);

// Kutunun rengi EN CİDDİ maddeye göre belirlenir: bir kritik madde, yanındaki üç bilgi
// maddesi yüzünden sarıya boyanmamalı.
const worstSeverity = computed(() =>
  items.value.some((item) => item.severity === "critical") ? "critical" : "warning"
);
</script>

<style scoped>
.attention-notice {
  border: 1px solid var(--warn-1);
  border-radius: 8px;
  padding: 0.7rem 1rem;
  background: var(--warn-bg, rgba(224, 180, 0, 0.08));
}

.attention-notice.critical {
  border-color: var(--danger-1);
  background: color-mix(in srgb, var(--danger-1) 8%, transparent);
}

/* `<summary>` varsayılan işareti `display: flex` ile kayboluyor; onun yerine ::before
   ile elle çizilir, böylece katlanabilir olduğu görünür kalır. */
.attention-head {
  display: flex;
  align-items: center;
  gap: 0.6rem;
  cursor: pointer;
  list-style: none;
}

.attention-head::-webkit-details-marker {
  display: none;
}

.attention-head::before {
  content: "▸";
  flex: 0 0 auto;
  color: var(--text-muted);
}

.attention-notice[open] > .attention-head::before {
  content: "▾";
}

/* Sayı, başlık ile "Tümünü gör" arasındaki boşluğu doldurur; düğme sağda kalır. */
.attention-count {
  margin-right: auto;
  color: var(--text-muted);
  font-size: 0.85em;
}

.attention-list {
  margin: 0.45rem 0 0;
  padding-left: 1.1rem;
}

.attention-list li {
  margin: 0.3rem 0;
}

.attention-name {
  font-weight: 600;
}

/* Açıklama adın hemen ardından, aynı satırda: göz önce adı, sonra ne yapılacağını görsün. */
.attention-desc::before {
  content: " — ";
  opacity: 0.6;
}

.attention-detail {
  display: block;
  opacity: 0.7;
}
</style>
