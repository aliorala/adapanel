<template>
  <!-- Hiç bulgu yoksa HİÇBİR ŞEY gösterilmez. Sürekli duran bir "her şey yolunda" şeridi,
       gerçekten bir sorun çıktığında fark edilmeyi zorlaştırır. -->
  <div v-if="findings.length" class="repair-notice" :class="worstSeverity">
    <div class="repair-head">
      <strong>{{ t("panel.health.siteNeedsRepair") }}</strong>
      <button class="secondary compact" :disabled="busy" @click="runRepair">
        {{ busy ? t("panel.health.siteRepairing") : t("panel.health.siteRepair") }}
      </button>
    </div>

    <ul class="repair-list">
      <li v-for="(finding, index) in findings" :key="finding.key + index" :class="finding.severity">
        <span class="repair-text">{{ t(finding.key) }}</span>
        <!-- Onarıldı / onay bekliyor ayrımı GÖRÜNÜR olmalı: kullanıcı "panel halletmiş"
             sanıp bir sorunu olduğu yerde bırakmasın. -->
        <span v-if="finding.repaired" class="repair-tag ok">{{ t("panel.health.findingRepaired") }}</span>
        <span v-else-if="finding.needsConfirmation" class="repair-tag warn">
          {{ t("panel.health.findingNeedsConfirmation") }}
        </span>
        <small v-if="finding.detail" class="repair-detail">{{ finding.detail }}</small>
      </li>
    </ul>
  </div>
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

const props = defineProps<{ siteName: string }>();
const emit = defineEmits<{ (event: "notify", message: string, isError: boolean): void }>();

const i18n = useI18n();
const t = (key: string) => {
  const value = i18n.t(key);
  return typeof value === "string" ? value : key;
};

const findings = ref<RepairFinding[]>([]);
const busy = ref(false);

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
  border: 1px solid var(--warn-border, #e0b400);
  border-radius: 8px;
  padding: 0.75rem 1rem;
  margin: 0.75rem 0;
  background: var(--warn-bg, rgba(224, 180, 0, 0.08));
}

.repair-notice.fail {
  border-color: var(--fail-border, #d64545);
  background: var(--fail-bg, rgba(214, 69, 69, 0.08));
}

.repair-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 1rem;
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
