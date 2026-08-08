<template>
  <!--
    "OKUNAMADI" durumu — BOŞ durumdan ayırt edilebilir olmalı.

    NEDEN: catch bloklarında state `null`/`[]` yapılıp kullanıcıya hiçbir şey söylenmiyordu.
    Bölüm boş görünüyor, kullanıcı "kayıt yok" mu "okunamadı" mı ayırt edemiyordu — üstelik
    "kayıt yok" sanıp var olan ayarı yeniden kurmaya kalkabiliyordu.

    role="alert" ile ekran okuyucu da duyar; "Tekrar dene" ile çıkış yolu vardır.
  -->
  <p class="notice error section-load-error" role="alert">
    <span class="section-load-error-text">
      <strong>{{ t("panel.common.loadFailed") }}</strong>
      <span v-if="message">{{ message }}</span>
    </span>
    <button type="button" class="secondary compact" :disabled="busy" @click="emit('retry')">
      {{ t("panel.common.retry") }}
    </button>
  </p>
</template>

<script setup lang="ts">
import { useI18n } from "vue-i18n";

withDefaults(defineProps<{ message?: string; busy?: boolean }>(), { message: "", busy: false });
const emit = defineEmits<{ (event: "retry"): void }>();

const { t } = useI18n();
</script>

<style scoped>
.section-load-error {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
}

.section-load-error-text {
  display: grid;
  gap: 3px;
  min-width: 0;
  word-break: break-word;
}

.section-load-error button {
  width: auto;
  flex: 0 0 auto;
}
</style>
