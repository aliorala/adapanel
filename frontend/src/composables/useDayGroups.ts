import { useI18n } from "vue-i18n";

/**
 * UTC damgalı kayıtları KULLANICININ YEREL gününe göre gruplama — TEK KAYNAK.
 *
 * NEDEN VAR: 0.5.5'ten beri yedek DOSYALARI diskte gün gün klasörleniyor
 * (`backups/2026-08-17/sites/...`) ama panel düz bir liste gösteriyordu. Gün başlığı üreten
 * kod iki ekranda (Yedekler + site içi yedek) gerekiyor; kopyalanırsa "yerel gün" hesabı
 * bir kopyada yanlış yapılır ve hata sınıfı geri gelir.
 *
 * NEDEN YEREL GÜN: sunucu `createdAtUtc` döndürür. UTC gününe göre gruplamak TR (UTC+3) gibi
 * dilimlerde 00:00–02:59 arasında alınan yedeği BİR ÖNCEKİ günün altına düşürürdü. Backend'in
 * ürettiği gün klasörü de YEREL tarihe göre adlandırılıyor (bilerek böyle) — ekranın yerel günü
 * kullanması, panelde görülen günün diskteki klasörle AYNI gün olmasını sağlar.
 *
 * NEDEN `toISOString().slice(0, 10)` DEĞİL: o değer UTC'dir; tam kaçınmak istediğimiz hatanın
 * kendisidir. Gün anahtarı bu yüzden `getFullYear/getMonth/getDate` ile YEREL alanlardan kurulur.
 */

export type DayGroup<T> = {
  /**
   * Yerel gün anahtarı, `YYYY-MM-DD` (YEREL alanlardan kurulur, UTC'den değil). Açılıp
   * kapanma durumu bu anahtarla saklanır — tarih metni diline göre değişse bile anahtar sabittir.
   */
  key: string;
  /** Okunabilir gün başlığı, ör. "Bugün · 17 Ağustos 2026". */
  label: string;
  /** O güne düşen kayıtlar; kaynak listenin sırası KORUNUR. */
  items: T[];
};

/** Geçersiz/boş tarih damgası taşıyan kayıtların düştüğü grup anahtarı. */
export const UNKNOWN_DAY_KEY = "";

/** Verilen anın YEREL gününü `YYYY-MM-DD` olarak döndürür; tarih çözülemezse boş dizge. */
export function localDayKey(value: string | Date | null | undefined): string {
  if (value === null || value === undefined || value === "") {
    return UNKNOWN_DAY_KEY;
  }

  const date = value instanceof Date ? value : new Date(value);
  if (Number.isNaN(date.getTime())) {
    return UNKNOWN_DAY_KEY;
  }

  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${date.getFullYear()}-${month}-${day}`;
}

/**
 * "En yeni önce" karşılaştırıcı — TEK KAYNAK.
 *
 * NEDEN DİZGE KARŞILAŞTIRMASI DEĞİL: ISO damgaları aynı görünse de .NET saniyenin kesrini
 * SONDAKİ SIFIRLARI ATARAK yazar; `...:00Z` ile `...:00.5Z` dizge olarak karşılaştırıldığında
 * `'.'` < `'Z'` olduğu için yarım saniye SONRA alınmış yedek ÖNCE gelmiş sayılırdı. Zaman
 * değerine çevirip karşılaştırmak bu tuzağı tamamen kapatır.
 *
 * Tarihi çözülemeyen kayıtlar listenin SONUNA düşer: onları 1970 sayıp listenin ortasına
 * sokmak, gerçek bir günün altına yanlış kayıt koymak olurdu.
 */
export function compareNewestFirst(
  left: string | Date | null | undefined,
  right: string | Date | null | undefined
): number {
  const leftTime = timeValue(left);
  const rightTime = timeValue(right);
  return leftTime === rightTime ? 0 : rightTime - leftTime;
}

function timeValue(value: string | Date | null | undefined): number {
  if (value === null || value === undefined || value === "") {
    return Number.NEGATIVE_INFINITY;
  }

  const time = (value instanceof Date ? value : new Date(value)).getTime();
  return Number.isNaN(time) ? Number.NEGATIVE_INFINITY : time;
}

export function useDayGroups() {
  const { t, locale } = useI18n();

  /**
   * Gün başlığı: "bugün/dün" kısayolu VE tarihin kendisi birlikte yazılır. Yalnız "Bugün"
   * yazmak, ekran açık kalıp gün dönerse yanlış bir başlık bırakırdı; yalnız tarih yazmak da
   * kullanıcının en çok baktığı günü tanımasını zorlaştırıyordu.
   */
  function dayLabel(key: string): string {
    if (key === UNKNOWN_DAY_KEY) {
      return t("panel.day.unknown");
    }

    const [year, month, day] = key.split("-").map(Number);
    const date = new Date(year, (month ?? 1) - 1, day ?? 1);
    if (Number.isNaN(date.getTime())) {
      return key;
    }

    // `locale.value` ("tr" / "en") geçerli bir BCP47 dil etiketidir; ay adı kullanıcının
    // diliyle, sıralama biçimi de o dilin kuralıyla gelir.
    const text = date.toLocaleDateString(locale.value, {
      weekday: "long",
      day: "numeric",
      month: "long",
      year: "numeric"
    });

    const now = new Date();
    const todayKey = localDayKey(now);
    if (key === todayKey) {
      return `${t("panel.day.today")} · ${text}`;
    }

    const yesterday = new Date(now.getFullYear(), now.getMonth(), now.getDate() - 1);
    if (key === localDayKey(yesterday)) {
      return `${t("panel.day.yesterday")} · ${text}`;
    }

    return text;
  }

  /**
   * Listeyi yerel güne göre gruplar. Kaynak listenin SIRASI korunur: çağıran taraf listeyi
   * zamana göre sıralı verirse gün grupları da zamana göre sıralı çıkar (ve bir gün listede
   * yalnızca TEK bir grup olur). Sırasız bir liste verilirse aynı gün birden fazla kez
   * görünmez — anahtar başına tek grup tutulur — ama grupların sırası kaynağın sırasıdır.
   */
  function groupByDay<T>(items: readonly T[], stampOf: (item: T) => string | Date | null | undefined): DayGroup<T>[] {
    const groups: DayGroup<T>[] = [];
    const index = new Map<string, DayGroup<T>>();

    for (const item of items) {
      const key = localDayKey(stampOf(item));
      let group = index.get(key);
      if (!group) {
        group = { key, label: dayLabel(key), items: [] };
        index.set(key, group);
        groups.push(group);
      }

      group.items.push(item);
    }

    return groups;
  }

  return { localDayKey, dayLabel, groupByDay };
}
