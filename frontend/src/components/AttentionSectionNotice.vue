<template>
  <!-- Madde yoksa HİÇBİR ŞEY gösterilmez. Sürekli duran bir "her şey yolunda" kutusu,
       gerçek bir sorun çıktığında fark edilmeyi zorlaştırır. -->
  <section v-if="items.length" class="attention-notice" :class="worstSeverity">
    <div class="attention-head">
      <strong>{{ t("panel.attention.noticeTitle") }}</strong>
      <button class="ghost compact" type="button" @click="emit('open')">
        {{ t("panel.attention.openDrawer") }}
      </button>
    </div>

    <ul class="attention-list">
      <li v-for="item in items" :key="item.id" :class="item.severity">
        <span class="attention-name">{{ t(item.titleKey) }}</span>
        <!-- Açıklama "ne yapılabilir"i söyler; başlık tek başına yalnızca sorunun adıdır. -->
        <span v-if="item.descriptionKey" class="attention-desc">{{ t(item.descriptionKey) }}</span>
        <!-- Teknik ayrıntı (sürüm, servis adı) çeviriye tabi değildir; olduğu gibi gösterilir. -->
        <small v-if="item.detail" class="attention-detail">{{ item.detail }}</small>
      </li>
    </ul>
  </section>
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
import { computed } from "vue";
import { useI18n } from "vue-i18n";
import type { AttentionItem } from "../api/attention";

const props = defineProps<{ items: AttentionItem[] }>();
const emit = defineEmits<{ (event: "open"): void }>();

const i18n = useI18n();
const t = (key: string) => {
  const value = i18n.t(key);
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
  border: 1px solid var(--warn-border, #e0b400);
  border-radius: 8px;
  padding: 0.7rem 1rem;
  background: var(--warn-bg, rgba(224, 180, 0, 0.08));
}

.attention-notice.critical {
  border-color: var(--danger-1, #d64545);
  background: color-mix(in srgb, var(--danger-1, #d64545) 8%, transparent);
}

.attention-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 1rem;
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
