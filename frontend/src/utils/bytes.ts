/**
 * BAYT BİÇİMLEYİCİ — TEK KAYNAK.
 *
 * NEDEN BURADA: aynı gövde `DashboardOverviewView`, `MonitoringView`, `BackupView`, `LogsView`,
 * `DatabasesView` gibi ekranlara ELLE kopyalanmıştı ve kopyalar zaten ayrışmıştı (bazıları
 * `panel.units.*` anahtarlarını kullanıp yerelleşiyor, bazıları "KiB" dizesini koda gömüyor).
 * Yeni ekranlar için ikinci bir biçim daha üretmemek adına, YERELLEŞEN sürüm buraya alındı;
 * yeni çağrı yerleri bunu kullanır.
 *
 * `t` DIŞARIDAN verilir: bu dosya bir Vue bileşeni/composable değil, `useI18n()` yalnız kurulum
 * (setup) bağlamında çağrılabilir. Birim etiketleri koda gömülseydi panel İngilizce'de de
 * Türkçe'de de aynı sabiti basardı ve sözlükteki `panel.units.*` ölü anahtara dönerdi.
 */
export function formatBytes(value: number, t: (key: string) => string): string {
  if (!Number.isFinite(value) || value <= 0) {
    return `0 ${t("panel.units.bytes")}`;
  }

  const units = [
    t("panel.units.bytes"),
    t("panel.units.kib"),
    t("panel.units.mib"),
    t("panel.units.gib"),
    t("panel.units.tib")
  ];

  let size = value;
  let unitIndex = 0;

  while (size >= 1024 && unitIndex < units.length - 1) {
    size /= 1024;
    unitIndex += 1;
  }

  // Bayt biriminde ondalık ANLAMSIZ (yarım bayt yok); 10'un üstünde de gürültü.
  return `${size.toFixed(size >= 10 || unitIndex === 0 ? 0 : 1)} ${units[unitIndex]}`;
}
