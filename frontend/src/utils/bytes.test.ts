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
