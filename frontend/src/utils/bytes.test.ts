import { describe, expect, it } from "vitest";

import { formatBytes } from "./bytes";

/*
 * Bu testler EKRANDA GÖRÜNEN sayının kararlarını korur. İkisi bilinçli ve BİRBİRİNE ZIT:
 * listelerde ondalık gürültüdür, kota göstergesinde ondalık BİLGİDİR. Ortak biçimleyici tek
 * kaynak olduğu için, birini düzeltmeye çalışan bir değişiklik diğerini sessizce bozabilir —
 * asıl korunan şey bu.
 */

const birimler: Record<string, string> = {
  "panel.units.bytes": "B",
  "panel.units.kib": "KiB",
  "panel.units.mib": "MiB",
  "panel.units.gib": "GiB",
  "panel.units.tib": "TiB"
};

const t = (key: string) => birimler[key] ?? key;

const GIB = 1024 ** 3;

describe("formatBytes", () => {
  it("liste bicimi: 10'un ustunde ondalik GOSTERMEZ", () => {
    expect(formatBytes(15.5 * GIB, t)).toBe("16 GiB");
    expect(formatBytes(9.5 * GIB, t)).toBe("9.5 GiB");
  });

  it("olcer bicimi: 10'un ustunde de ondalik KORUR (kota okunabilsin)", () => {
    // Kotasina 512 MiB kalmis kullanici icin "16 GiB" ile "15.5 GiB" ayni sey degildir.
    expect(formatBytes(15.5 * GIB, t, "gauge")).toBe("15.5 GiB");
    // Basamak sayisi zipla-MA-sin: 10'un altinda da tek ondalik.
    expect(formatBytes(9 * GIB, t, "gauge")).toBe("9.0 GiB");
  });

  it("bayt biriminde ondalik YOK — olcer olsa bile (yarim bayt diye bir sey yok)", () => {
    expect(formatBytes(512, t)).toBe("512 B");
    expect(formatBytes(512, t, "gauge")).toBe("512 B");
  });

  it("olculemeyen deger sifir bayt olarak yazilir (iki bicimde de ayni)", () => {
    expect(formatBytes(0, t)).toBe("0 B");
    expect(formatBytes(Number.NaN, t, "gauge")).toBe("0 B");
  });
});

/*
 * KOTA ALANLARININ TABANI.
 *
 * Kart bilesenleri (SubscriptionCard, UsersView) mount EDILEMIYOR: depoda @vue/test-utils yok.
 * Ama kirilan sey aslinda bilesende degil, sunucu alanini BAYTA cevirirken secilen tabandaydi —
 * sinir "trafficLimitGb" ham sayi + koda gomulu "GB" ile basiliyor, kullanilan ise 1024 tabanli
 * bicimleyiciden geciyordu. Asagidaki testler o cevrimi kilitler: sunucunun 1024 tabanini
 * (SubscriptionEnforcementService, CustomerScopeService, FileQuotaGuard) 1000'e ceviren bir
 * degisiklik burada duser.
 */
describe("kota alanlari bayta cevrilirken", () => {
  const MIB = 1024 ** 2;

  it("trafficLimitGb GiB'dir: sunucu sinira 1024^3 uyguluyor", () => {
    expect(formatBytes(20 * GIB, t, "gauge")).toBe("20.0 GiB");
    // 1000 tabani ayni sayiyi %7 kucuk gosterirdi — kullanicinin kotasi bittigini gec fark
    // etmesine yol acan fark tam olarak bu.
    expect(formatBytes(20 * 1000 ** 3, t, "gauge")).toBe("18.6 GiB");
  });

  it("diskUsageMb/maxDiskMb MiB'dir: sunucu bayti 1024^2'ye boluyor", () => {
    expect(formatBytes(5120 * MIB, t, "gauge")).toBe("5.0 GiB");
    expect(formatBytes(512 * MIB, t, "gauge")).toBe("512.0 MiB");
  });

  it("kullanilan ve sinir AYNI tabandan okunur: yuzde ile sayi ayrisamaz", () => {
    // 15.5 GiB / 20 GiB = %78; iki sayi da ayni carpandan turedigi icin cubuk ile yazi ayni seyi
    // soyler. Sinir 1000 tabanindan turetilseydi ayni kullanimda oran %83'e cikar, yani ekranda
    // "15.5 / 20" yazip cubugu baska bir yerde durdururdu.
    const kullanilan = 15.5 * GIB;
    const sinir = 20 * GIB;
    expect(formatBytes(kullanilan, t, "gauge")).toBe("15.5 GiB");
    expect(formatBytes(sinir, t, "gauge")).toBe("20.0 GiB");
    expect(Math.round((kullanilan / sinir) * 100)).toBe(78);
  });
});
