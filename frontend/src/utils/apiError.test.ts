import { describe, expect, it } from "vitest";

import {
  classifyTransport,
  describeTransport,
  looksLikeMessageKey,
  resolveFailureDetail,
  resolveFailureKey,
  splitCompositeMessage
} from "./apiError";

/*
 * Bu testler EKRANDA GÖRÜNEN metnin kararlarını korur. Üçü de gerçek arıza kayıtlarından
 * geliyor: kullanıcı kırmızı şeritte ham anahtar (`wpcli.not_wordpress`), İngilizce ağ hatası
 * (`Network Error`) ve çevrilemeyen bileşik dizge (`ftp.user_exists — CreateUser failed…`)
 * görüyordu. Testler o üç durumun geri gelmesini engeller.
 */

describe("looksLikeMessageKey", () => {
  it("gercek anahtarlari taniyor", () => {
    expect(looksLikeMessageKey("files.path_outside_root")).toBe(true);
    expect(looksLikeMessageKey("sites.create.success")).toBe(true);
    expect(looksLikeMessageKey("auth.unauthorized")).toBe(true);
  });

  it("kullaniciya yazilmis metni anahtar SANMIYOR", () => {
    // Bunlari anahtar saymak, cumlenin basini kesip atmak demekti.
    expect(looksLikeMessageKey("Yedek alındı")).toBe(false);
    expect(looksLikeMessageKey("HTTP 502")).toBe(false);
    expect(looksLikeMessageKey("Network Error")).toBe(false);
    // Noktasiz tek kelime anahtar degildir.
    expect(looksLikeMessageKey("basarisiz")).toBe(false);
    // Bosluk iceren hicbir sey anahtar degildir.
    expect(looksLikeMessageKey("files.path outside")).toBe(false);
  });
});

describe("splitCompositeMessage", () => {
  it("em tire ile gomulmus ayrintiyi ayiriyor (SitesController kalibi)", () => {
    const result = splitCompositeMessage("ftp.user_exists — CreateUser failed: account already present");
    expect(result.key).toBe("ftp.user_exists");
    expect(result.detail).toBe("CreateUser failed: account already present");
  });

  it("iki nokta kalibini ayiriyor (DomainRefreshService / BackupService)", () => {
    const result = splitCompositeMessage("backup.copy_failed: access denied to D:\\yedek");
    expect(result.key).toBe("backup.copy_failed");
    expect(result.detail).toBe("access denied to D:\\yedek");
  });

  it("satir sonu kalibini ayiriyor (ComponentBootstrapper / PhpService)", () => {
    const result = splitCompositeMessage("php.install_failed\r\nexit code 1");
    expect(result.key).toBe("php.install_failed");
    expect(result.detail).toBe("exit code 1");
  });

  it("duz anahtari OLDUGU GIBI birakiyor", () => {
    expect(splitCompositeMessage("wpcli.not_wordpress")).toEqual({
      key: "wpcli.not_wordpress",
      detail: ""
    });
  });

  it("kullaniciya yazilmis cumleyi BOLMUYOR", () => {
    // Bastaki parca anahtar gibi gorunmuyorsa metin butun olarak kalir.
    const turkish = "Yedek alındı: 3 dosya kopyalandı";
    expect(splitCompositeMessage(turkish)).toEqual({ key: turkish, detail: "" });

    const english = "HTTP 502: Bad Gateway";
    expect(splitCompositeMessage(english)).toEqual({ key: english, detail: "" });
  });

  it("anahtarin ORTASINDAKI tireyi ayirici sanmiyor", () => {
    // Bosluksuz tire anahtarin parcasidir; boyle bir anahtar ikiye bolunmemeli.
    expect(splitCompositeMessage("ssl.fallback.acme-failed")).toEqual({
      key: "ssl.fallback.acme-failed",
      detail: ""
    });
  });

  it("bos girdiyi guvenle karsiliyor", () => {
    expect(splitCompositeMessage("")).toEqual({ key: "", detail: "" });
    expect(splitCompositeMessage("   ")).toEqual({ key: "", detail: "" });
  });
});

describe("classifyTransport", () => {
  it("yanit yoksa AG hatasi", () => {
    // Eskiden bu yol ham "Network Error" donuyordu; Turkce panelde Ingilizce metin.
    expect(classifyTransport({ code: "ERR_NETWORK", message: "Network Error" })).toBe("common.network_error");
  });

  it("zaman asimini ag kopuklugundan AYIRIYOR", () => {
    expect(classifyTransport({ code: "ECONNABORTED", message: "timeout of 5000ms exceeded" }))
      .toBe("common.timeout");
    expect(classifyTransport({ code: "ETIMEDOUT" })).toBe("common.timeout");
  });

  it("axios'tan GELMEYEN hatayi ag sorunu SANMIYOR", () => {
    // `try` blogunda patlayan siradan bir JS hatasi da yanitsizdir; ona "sunucuya ulasilamadi"
    // demek kullaniciyi bosuna agini kurcalamaya gonderirdi.
    expect(classifyTransport({ isAxios: false, message: "Cannot read properties of undefined" }))
      .toBe("common.unexpected_error");
  });

  it("durum kodlarini anlamli anahtarlara esliyor", () => {
    expect(classifyTransport({ status: 401, statusText: "Unauthorized" })).toBe("auth.unauthorized");
    expect(classifyTransport({ status: 403 })).toBe("common.forbidden");
    expect(classifyTransport({ status: 404 })).toBe("common.not_found");
    expect(classifyTransport({ status: 502, statusText: "Bad Gateway" })).toBe("common.server_error");
    expect(classifyTransport({ status: 500 })).toBe("common.server_error");
    expect(classifyTransport({ status: 400 })).toBe("common.request_failed");
  });
});

describe("describeTransport", () => {
  it("sunucu bir mesaj verdiyse HTTP durumunu EKLEMIYOR (gurultu olurdu)", () => {
    expect(describeTransport({ status: 400, statusText: "Bad Request", envelope: { message: "files.path_outside_root" } }))
      .toBe("");
  });

  it("zarf yoksa durumu teshis icin yaziyor", () => {
    expect(describeTransport({ status: 502, statusText: "Bad Gateway" })).toBe("HTTP 502 Bad Gateway");
    expect(describeTransport({ status: 502 })).toBe("HTTP 502");
  });

  it("hic yanit yoksa bos donuyor", () => {
    expect(describeTransport({ code: "ERR_NETWORK" })).toBe("");
  });
});

describe("resolveFailureKey / resolveFailureDetail — birlikte", () => {
  it("bilesik zarf mesajini anahtar + ayrinti olarak veriyor", () => {
    const failure = {
      status: 500,
      statusText: "Internal Server Error",
      envelope: { message: "ftp.user_exists — CreateUser failed: account already present" }
    };
    // Anahtar artik cevrilebilir; ayrinti kaybolmuyor, ayri satirda gosteriliyor.
    expect(resolveFailureKey(failure)).toBe("ftp.user_exists");
    expect(resolveFailureDetail(failure)).toBe("CreateUser failed: account already present");
  });

  it("zarfin kendi details alani gomulu ayrintiya TERCIH ediliyor", () => {
    const failure = {
      status: 500,
      envelope: { message: "backup.failed — kisa ozet", details: "tam yigin izi" }
    };
    expect(resolveFailureKey(failure)).toBe("backup.failed");
    expect(resolveFailureDetail(failure)).toBe("tam yigin izi");
  });

  it("ag hatasinda cevrilebilir anahtar + bos ayrinti", () => {
    const failure = { code: "ERR_NETWORK", message: "Network Error" };
    expect(resolveFailureKey(failure)).toBe("common.network_error");
    expect(resolveFailureDetail(failure)).toBe("");
  });

  it("zarfsiz 502'de anahtar cevrilir, durum kodu ayrintida kalir", () => {
    const failure = { status: 502, statusText: "Bad Gateway" };
    expect(resolveFailureKey(failure)).toBe("common.server_error");
    expect(resolveFailureDetail(failure)).toBe("HTTP 502 Bad Gateway");
  });

  it("siradan anahtar hatasi bozulmadan geciyor (gerileme korumasi)", () => {
    const failure = { status: 400, envelope: { message: "wpcli.not_wordpress" } };
    expect(resolveFailureKey(failure)).toBe("wpcli.not_wordpress");
    expect(resolveFailureDetail(failure)).toBe("");
  });
});
