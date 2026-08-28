/**
 * TARİH/SAAT BİÇİMLEYİCİ — TEK KAYNAK.
 *
 * NEDEN BURADA: `new Date(x).toLocaleString()` gövdesi 24 ekrana ELLE kopyalanmıştı
 * (`formatDate`, `formatUtc`, `formatTime`, `formatCreatedAt` — dört ayrı ad, aynı iş) ve
 * kopyalar ayrışmıştı:
 *
 *   A) 7 kopya bozuk tarihte GİRDİYİ olduğu gibi basıyordu (`Number.isNaN(...) ? iso : ...`),
 *   B) 11 kopya boş değeri yakalıyordu ama bozuk tarihi yakalamıyordu,
 *   C) 2 kopyada hiç koruma yoktu — bozuk veride kullanıcıya "Invalid Date" görünüyordu.
 *
 * Bu birleştirme (C)'deki "Invalid Date" hatasını kapatır: ayrıştırılamayan değer artık HAM
 * hâliyle basılır. Ham değer en azından sunucudan ne geldiğini gösterir; "Invalid Date" ise
 * kullanıcıya hiçbir şey söylemez ve hatanın veride mi panelde mi olduğunu gizler.
 *
 * `fallback` DIŞARIDAN verilir: "tarih yok" durumunda ekranlar bugün üç ayrı şey basıyor
 * ("-", "—" ve `panel.common.notAvailable`). Bunu tek bir dizgede birleştirmek yeni bir i18n
 * anahtarı gerektirir; o karar verilene kadar her çağıran KENDİ metnini geçirir; yani bu
 * birleştirme GÖRÜNEN çıktıyı değiştirmez.
 */
export function formatDateTime(value: string | null | undefined, fallback = ""): string {
  if (!value) {
    return fallback;
  }

  const parsed = new Date(value);
  // Ayrıştırılamayan değer YUTULMAZ: ham dizge, "Invalid Date"ten daha çok bilgi taşır.
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString();
}

/**
 * .NET `TimeSpan` dizgesini ("1.02:03:04" / "02:03:04") kısa süreye çevirir: "1g 2sa 3dk 4sn".
 *
 * NEDEN BURADA: gövde `DashboardOverviewView` ve `MonitoringView` içinde BİREBİR aynı iki kopya
 * hâlindeydi; iki ekran çalışma süresini yan yana gösteriyor, biçimleri ayrışırsa aynı sunucu
 * iki farklı süre okur gibi görünür.
 *
 * `t` DIŞARIDAN verilir (bkz. utils/bytes.ts'teki aynı gerekçe): birim etiketleri koda gömülürse
 * `panel.units.*Short` anahtarları ölür ve panel İngilizce'de de "sa/dk" basar.
 */
export function formatTimeSpan(value: string, t: (key: string) => string): string {
  const match = value.match(/^(?:(\d+)\.)?(\d{2}):(\d{2}):(\d{2})(?:\.\d+)?$/);
  // Beklenmeyen biçim UYDURULMAZ: ham değer basılır.
  if (!match) {
    return value;
  }

  const days = Number(match[1] ?? "0");
  const hours = Number(match[2]);
  const minutes = Number(match[3]);
  const seconds = Number(match[4]);
  const parts: string[] = [];

  if (days > 0) {
    parts.push(`${days}${t("panel.units.dayShort")}`);
  }

  parts.push(`${hours}${t("panel.units.hourShort")}`);
  parts.push(`${minutes}${t("panel.units.minuteShort")}`);
  parts.push(`${seconds}${t("panel.units.secondShort")}`);
  return parts.join(" ");
}
