<!--
  YEDEK ZAMANLAMA ALANLARI — TEK KAYNAK.

  Aynı program kavramı iki yerde düzenleniyor: sunucu geneli otomatik yedek (Araçlar > Otomasyon
  ve Yedekleme ekranındaki kısayol kartı) ve abonelik başına yedek programı (Yedekleme ekranının
  altındaki abonelik programları). Alanları iki ekrana ayrı ayrı yazmak, birinde düzeltilip
  diğerinde unutulan bir seçenek demekti; bu yüzden alanlar burada bir kez tanımlanır.

  SEÇİCİ NEDEN TEK (Plesk'in yöntemi): Plesk'te de "Run this backup task" tek bir sıklık
  seçicidir (hourly | daily | weekly | monthly) ve seçime göre alt alanlar açılır. İki ayrı
  kontrol (biri "aralık", biri "saat") koymak "hangisi geçerli?" sorusunu doğururdu; tek seçici
  bu belirsizliği kaynağında yok eder. Aralık seçeneği KALDIRILMADI: "6 saatte bir" isteyen
  kullanıcı onu seçer ve saat alanı hiç görünmez.
-->
<template>
  <label class="stacked-field">
    <span class="field-label">{{ t("panel.schedule.mode") }}</span>
    <select v-model="plan.mode" :disabled="disabled">
      <option value="interval">{{ t("panel.schedule.modeInterval") }}</option>
      <option value="daily">{{ t("panel.schedule.modeDaily") }}</option>
      <option value="weekly">{{ t("panel.schedule.modeWeekly") }}</option>
      <option value="monthly">{{ t("panel.schedule.modeMonthly") }}</option>
    </select>
  </label>

  <label v-if="plan.mode === 'interval'" class="stacked-field">
    <span class="field-label">{{ t("panel.schedule.intervalHours") }}</span>
    <input v-model.number="plan.intervalHours" type="number" min="1" max="720" :disabled="disabled" />
    <small class="field-hint">{{ t("panel.schedule.intervalHint") }}</small>
  </label>

  <!-- SAAT: takvim modlarının hepsinde sorulur. Değer SUNUCUNUN yerel saatidir; ipucu bunu
       açıkça söyler, çünkü panele başka bir saat diliminden bağlanan yönetici tarayıcısının
       saatini kastettiğini sanırdı. Dilim bilgisi sunucudan gelmediyse ipucu HİÇ gösterilmez:
       yanlış dilim yazmaktansa susmak doğrudur. -->
  <label v-else class="stacked-field">
    <span class="field-label">{{ t("panel.schedule.time") }}</span>
    <input v-model="timeText" type="time" :disabled="disabled" />
  </label>

  <label v-if="plan.mode === 'daily'" class="stacked-field">
    <span class="field-label check-field">
      <input v-model="secondEnabled" type="checkbox" :disabled="disabled" />
      {{ t("panel.schedule.secondEnabled") }}
    </span>
    <small class="field-hint">{{ t("panel.schedule.secondHint") }}</small>
  </label>

  <label v-if="plan.mode === 'daily' && secondEnabled" class="stacked-field">
    <span class="field-label">{{ t("panel.schedule.secondTime") }}</span>
    <input v-model="secondTimeText" type="time" :disabled="disabled" />
  </label>

  <label v-if="plan.mode === 'weekly'" class="stacked-field">
    <span class="field-label">{{ t("panel.schedule.weekday") }}</span>
    <select v-model.number="plan.dayOfWeek" :disabled="disabled">
      <option v-for="day in 7" :key="day - 1" :value="day - 1">{{ t(`panel.schedule.day.${day - 1}`) }}</option>
    </select>
  </label>

  <label v-if="plan.mode === 'monthly'" class="stacked-field">
    <span class="field-label">{{ t("panel.schedule.dayOfMonth") }}</span>
    <input v-model.number="plan.dayOfMonth" type="number" min="1" max="31" :disabled="disabled" />
    <small class="field-hint">{{ t("panel.schedule.dayOfMonthHint") }}</small>
  </label>
</template>

<script setup lang="ts">
import { computed } from "vue";
import { useI18n } from "vue-i18n";
import type { BackupSchedulePlan } from "../api/schedule";

/**
 * `plan` DOĞRUDAN düzenlenir: üst ekran reaktif bir nesne verir ve alanlar onun üzerinde
 * değişir. Alan alan `emit` yazmak, altı alanın her biri için ayrı bir olay demekti ve iki
 * ekranda iki ayrı bağlama koduna yol açardı — tam da bu bileşenin önlemek için var olduğu şey.
 */
const props = defineProps<{
  plan: BackupSchedulePlan;
  disabled?: boolean;
}>();

const { t } = useI18n();

const plan = computed(() => props.plan);

const secondEnabled = computed({
  get: () => props.plan.secondHour != null,
  set(enabled: boolean) {
    props.plan.secondHour = enabled ? (props.plan.hour + 12) % 24 : null;
    props.plan.secondMinute = props.plan.minute;
  }
});

const secondTimeText = computed({
  get: () => `${String(props.plan.secondHour ?? 0).padStart(2, "0")}:${String(props.plan.secondMinute ?? 0).padStart(2, "0")}`,
  set(value: string) {
    if (!/^\d{2}:\d{2}$/.test(value)) return;
    const [hour, minute] = value.split(":").map(Number);
    if (hour >= 0 && hour <= 23 && minute >= 0 && minute <= 59) {
      props.plan.secondHour = hour;
      props.plan.secondMinute = minute;
    }
  }
});

/**
 * `<input type="time">` tek bir "HH:MM" metniyle çalışır; kayıt ise saat ve dakikayı AYRI tutar
 * (sunucu ikisini ayrı doğruluyor). Dönüşüm burada, tek yerde yapılır.
 */
const timeText = computed({
  get: () => `${String(props.plan.hour).padStart(2, "0")}:${String(props.plan.minute).padStart(2, "0")}`,
  set(value: string) {
    const [saat, dakika] = (value || "").split(":");
    const h = Number(saat);
    const m = Number(dakika);
    // Tarayıcı alanı boşaltıldığında "" gelir. Mevcut değer KORUNUR: sıfıra düşürmek yedeği
    // sessizce gece yarısına taşırdı ve kullanıcı bunu ancak ertesi gün fark ederdi.
    if (Number.isFinite(h) && Number.isFinite(m)) {
      props.plan.hour = h;
      props.plan.minute = m;
    }
  }
});
</script>
