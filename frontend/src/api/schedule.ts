/**
 * Yedek programının ekran tarafındaki ORTAK biçimi.
 *
 * Sunucu geneli otomasyon ayarları ve abonelik başına program, alan adları farklı olsa da AYNI
 * kavramı taşır (mod + saat + gün). Ortak biçim sayesinde alan bileşeni
 * (`BackupScheduleFields.vue`) ve özet metni tek yerde durur; iki ekranın birbirinden sapması
 * mümkün olmaz.
 */
export type BackupSchedulePlan = {
  /** interval | daily | weekly | monthly */
  mode: string;
  /** Yalnızca `interval` modunda kullanılır. */
  intervalHours: number;
  /** SUNUCUNUN yerel saati (0-23). */
  hour: number;
  minute: number;
  /** 0 = Pazar … 6 = Cumartesi. */
  dayOfWeek: number;
  /** 1-31; ay o günü çekmiyorsa sunucu ayın son gününü kullanır. */
  dayOfMonth: number;
};

/** Sunucu tarafındaki varsayılanlarla aynı: eski kurulumlar aralık modunda kalır. */
export function bosProgram(): BackupSchedulePlan {
  return { mode: "interval", intervalHours: 24, hour: 3, minute: 0, dayOfWeek: 1, dayOfMonth: 1 };
}

function saatMetni(plan: BackupSchedulePlan) {
  return `${String(plan.hour).padStart(2, "0")}:${String(plan.minute).padStart(2, "0")}`;
}

/**
 * "Her gün 03:00" gibi tek satırlık özet. Listelerde ve kartlarda kullanılır; çeviri
 * fonksiyonu dışarıdan verilir ki bu dosya i18n kurulumuna bağımlı olmasın.
 */
export function programOzeti(plan: BackupSchedulePlan, t: (key: string, named?: Record<string, unknown>) => string) {
  switch (plan.mode) {
    case "daily":
      return t("panel.schedule.summaryDaily", { time: saatMetni(plan) });
    case "weekly":
      return t("panel.schedule.summaryWeekly", {
        day: t(`panel.schedule.day.${plan.dayOfWeek}`),
        time: saatMetni(plan)
      });
    case "monthly":
      return t("panel.schedule.summaryMonthly", { day: plan.dayOfMonth, time: saatMetni(plan) });
    default:
      return t("panel.schedule.summaryInterval", { hours: plan.intervalHours });
  }
}
