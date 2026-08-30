import { afterEach, describe, expect, it, vi } from "vitest";

import { maskSecretsInUri, toReachableUrl } from "./url";

/*
 * Bu testler "TARAYICIDA AÇ" akışlarının çıkış kapısını koruyor.
 *
 * NEDEN önemli: `toReachableUrl`in dönüşü `window.open`, `location.href` ve `:href`e gider.
 * Çağıranların ikisi (webmail, wp-login) sekmeyi `window.open("", "_blank")` ile açar;
 * `about:blank` sekmesi AÇANIN kökenini miras alır, yani oraya yazılan bir `javascript:`
 * adresi PANELİN kökeninde — yöneticinin oturumunda — çalışır.
 *
 * Eleme eklenmeden önce ÖLÇÜLDÜ: `new URL("javascript:alert(1)", origin)` hata atmıyor,
 * `hostname` boş kalıyor ve dizge olduğu gibi geri dönüyordu. Üstelik `URL` normalleştirmesi
 * baştaki boşluğu atıp büyük harfi küçülttüğü için "  JaVaScRiPt:..." girdisi ÇALIŞIR hâle
 * geliyordu. Aşağıdaki durumlar o ölçümün ta kendisidir.
 */

// url.ts `window.location`a bakar; vitest ortamı `node`, yani `window` yok. Gerçek panelin
// kurulumunu taklit ediyoruz: HTTPS, standart dışı port (8890) — canlı panelde de böyle.
function panelKurulumu(origin = "https://panel.ornek:8890", hostname = "panel.ornek") {
  vi.stubGlobal("window", { location: { origin, hostname } });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("toReachableUrl — şema elemesi", () => {
  it("javascript: adresini GEÇİRMİYOR", () => {
    panelKurulumu();
    // Bu satır kırmızı yanarsa: yöneticinin sekmesinde panel kökeninde kod çalışabiliyor.
    expect(toReachableUrl("javascript:alert(document.domain)")).toBe("");
  });

  it("BÜYÜK/küçük harf ve baştaki boşlukla gizlenmiş javascript: de geçmiyor", () => {
    panelKurulumu();
    // `URL` bunları normalleştirip ÇALIŞIR hâle getiriyordu — eleme normalleştirmeden
    // SONRA (url.protocol üzerinden) yapılmalı, ham dizgeye bakarak değil.
    expect(toReachableUrl("JaVaScRiPt:alert(1)")).toBe("");
    expect(toReachableUrl("  javascript:alert(1)")).toBe("");
    expect(toReachableUrl("java\tscript:alert(1)")).toBe("");
  });

  it("data: ve vbscript: geçmiyor", () => {
    panelKurulumu();
    expect(toReachableUrl("data:text/html,<script>alert(1)</script>")).toBe("");
    expect(toReachableUrl("vbscript:msgbox(1)")).toBe("");
  });

  it("ayrıştırılamayan dizge HAM DÖNMÜYOR", () => {
    panelKurulumu();
    // Eskiden `catch` girdiyi olduğu gibi geri veriyordu: elemediğimiz şeye "temiz" demek.
    expect(toReachableUrl("http://[kapanmamis")).toBe("");
  });
});

describe("toReachableUrl — meşru kullanım KIRILMADI", () => {
  it("uzak http(s) adresini aynen geçiriyor", () => {
    panelKurulumu();
    expect(toReachableUrl("https://site.ornek/yol?a=1")).toBe("https://site.ornek/yol?a=1");
    expect(toReachableUrl("http://site.ornek/")).toBe("http://site.ornek/");
  });

  it("sunucu-yerel host'u panelin host'uyla değiştirmeye DEVAM ediyor", () => {
    panelKurulumu();
    // Fonksiyonun asıl işi bu; eleme eklerken kaybolmadığını burada sabitliyoruz.
    expect(toReachableUrl("http://127.0.0.1:8080/pma/")).toBe("http://panel.ornek:8080/pma/");
    expect(toReachableUrl("http://localhost:8080/pma/")).toBe("http://panel.ornek:8080/pma/");
  });

  it("IIS bağlamasından kurulan adres (http/https) geçiyor", () => {
    panelKurulumu();
    // WebsiteWorkspaceView `${binding.protocol}://${host}:${port}` kuruyor; protokol
    // orada yalnız "http"/"https" olabiliyor (aynı dosyada port 80/443 ile karşılaştırılıyor).
    expect(toReachableUrl("https://kiraci.ornek:8443")).toBe("https://kiraci.ornek:8443/");
  });

  it("boş girdi boş kalıyor", () => {
    panelKurulumu();
    expect(toReachableUrl("")).toBe("");
  });
});

describe("maskSecretsInUri", () => {
  it("S3 imzasını ekrana basmıyor", () => {
    // Yedek listesi ekran görüntüsü paylaşılan bir yerdir; imza o nesneye yazma yetkisidir.
    const sonuc = maskSecretsInUri("https://kova.s3.amazonaws.com/y.zip?X-Amz-Signature=abc");
    expect(sonuc.text).toBe("https://kova.s3.amazonaws.com/y.zip");
    expect(sonuc.masked).toBe(true);
  });

  it("kullanıcı:parola çiftini çıkarıyor", () => {
    const sonuc = maskSecretsInUri("ftp://ali:gizli@sunucu.ornek/yedek");
    expect(sonuc.text).not.toContain("gizli");
    expect(sonuc.masked).toBe(true);
  });

  it("temiz adresi masked=false ile geçiriyor", () => {
    const sonuc = maskSecretsInUri("https://sunucu.ornek/yedek");
    expect(sonuc.masked).toBe(false);
  });
});
