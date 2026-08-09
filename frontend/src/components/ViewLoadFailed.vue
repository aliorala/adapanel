<template>
  <!--
    Bölüm paketi (chunk) İNDİRİLEMEDİ. Bu "kayıt yok" değildir ve sessiz kalmamalıdır:
    panel güncellendikten sonra tarayıcıda duran eski index.html artık var olmayan hash'li
    dosyaları ister ve bölüm boş açılır. Tek gerçek çözüm sayfayı yeniden yüklemektir —
    "Tekrar dene" bu yüzden sayfayı yeniler, aynı ölü isteği tekrarlamaz.
  -->
  <div class="panel module-shell view-load-failed">
    <SectionLoadError :message="t('panel.common.sectionChunkFailed')" @retry="reload" />
  </div>
</template>

<script setup lang="ts">
import { useI18n } from "vue-i18n";
import SectionLoadError from "./SectionLoadError.vue";

const { t } = useI18n();

function reload() {
  window.location.reload();
}
</script>

<style scoped>
.view-load-failed {
  display: grid;
  gap: 10px;
}
</style>
