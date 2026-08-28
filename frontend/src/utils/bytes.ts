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
/**
 * HASSASİYET — çağıran ekranın İŞİNE göre.
 *
 * `compact` (varsayılan): 10'un üstünde ondalık gösterilmez. Liste ve tablo için doğru; orada
 * sayı bir BÜYÜKLÜK bilgisidir ("bu yedek kaç GiB"), 0.1 GiB'lik fark kararı değiştirmez ve
 * ondalıklar sütunu gürültüye boğar.
 *
 * `gauge`: bayt dışındaki her birimde ondalık KORUNUR. NEDEN: ölçer/kota gösteren yerde kullanıcı
 * büyüklüğü değil SINIRA YAKINLIĞI okur ve kotaların yığıldığı aralık tam da 10-100 GiB'dir —
 * yani `compact` kuralının ondalığı attığı ilk aralık. Orada "16 GiB / 20 GiB" ile
 * "15.5 GiB / 20 GiB" arasındaki fark 512 MiB'dir ve kotasına 500 MiB kalmış bir kullanıcı için
 * bu farkın tamamı bilgidir. Ondalık 10'un ALTINDA da bırakılır (ör. "9.0 GiB"): ölçer sayısı
 * canlı değişir, basamak sayısının zıplaması değeri okumayı zorlaştırır.
 */
export type ByteFormatPrecision = "compact" | "gauge";

export function formatBytes(
  value: number,
  t: (key: string) => string,
  precision: ByteFormatPrecision = "compact"
): string {
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

  // Bayt biriminde ondalık ANLAMSIZ (yarım bayt yok) — bu kural hassasiyetten BAĞIMSIZ, ölçer de
  // olsa "1536.0 B" yazmayız. Üstündeki birimlerde karar hassasiyete geçer (bkz. ByteFormatPrecision).
  const ondalik = unitIndex === 0 ? 0 : precision === "gauge" || size < 10 ? 1 : 0;
  return `${size.toFixed(ondalik)} ${units[unitIndex]}`;
}
