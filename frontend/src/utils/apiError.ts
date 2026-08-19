/**
 * API HATA METNİ — SAF ÇEKİRDEK.
 *
 * NEDEN AYRI DOSYA: burada yaşayan üç karar (bileşik anahtarı ayır, taşıma hatasını sınıflandır,
 * ayrıntıyı seç) kullanıcının EKRANDA GÖRDÜĞÜ metni belirliyor ve bugüne kadar hiç test
 * edilmemişti. Fonksiyonlar axios örneğine, Vue'ya ya da i18n'e bağlı DEĞİL: düz veri alır, düz
 * veri döner. Böylece tarayıcı açmadan doğrulanabilirler (bkz. utils/apiError.test.ts).
 *
 * `stores/auth.ts` bu dosyayı sarar; 57 ekranın çağırdığı `extractApiMessage` imzası değişmedi.
 */

/** Sunucunun standart yanıt zarfı (yalnız hata yolunda okunan alanlar). */
export type ApiErrorEnvelope = {
  message?: string;
  details?: string;
};

/** `extractApiMessage`'ın axios'tan ihtiyaç duyduğu HER ŞEY — test bunu elle kurabilir. */
export type TransportFailure = {
  /** Yanıt geldiyse HTTP durumu; ağ hatasında undefined. */
  status?: number;
  statusText?: string;
  /** Axios hata kodu: "ECONNABORTED", "ERR_NETWORK", "ETIMEDOUT" … */
  code?: string;
  /** Axios'un ham mesajı: "Network Error", "timeout of 5000ms exceeded" … */
  message?: string;
  /** Yanıt gövdesindeki zarf (varsa). */
  envelope?: ApiErrorEnvelope | null;
  /**
   * Hata GERÇEKTEN bir HTTP isteğinden mi geldi (axios) yoksa `try` bloğunda patlayan sıradan bir
   * JS hatası mı. Ayrım şart: ikisi de yanıtsızdır, ama kod hatasına "sunucuya ulaşılamadı"
   * demek kullanıcıyı boş yere ağını kurcalamaya gönderir.
   */
  isAxios?: boolean;
};

/**
 * Bir dizgenin "çeviri anahtarı gibi" görünüp görünmediği.
 *
 * Kural bilinçli olarak DAR: küçük harfle başlar, en az bir nokta içerir, yalnız
 * `[a-z0-9_.]` kullanır. Böylece `files.path_outside_root` anahtar sayılır ama kullanıcıya
 * yazılmış gerçek cümleler ("Yedek alındı: 3 dosya", "HTTP 502: Bad Gateway") sayılmaz —
 * yanlışlıkla cümlenin başını kesip atmayız.
 */
export function looksLikeMessageKey(value: string): boolean {
  return /^[a-z][a-z0-9_]*(?:\.[a-z0-9_]+)+$/.test(value);
}

export type SplitMessage = {
  /** Sözlükte aranacak anahtar (ya da anahtar değilse metnin kendisi). */
  key: string;
  /** Anahtarın arkasına iliştirilmiş serbest metin; yoksa boş. */
  detail: string;
};

/*
 * Arka uçtan gelen bileşik dizgeyi anahtar + ayrıntı olarak ayırır.
 *
 * BU BİR TELAFİDİR — kök neden arka uçtadır ve orada düzeltilmelidir.
 * `SitesController.DescribeProvisionError` (ve aynı kalıptaki 6 nokta) çeviri anahtarını serbest
 * metnin İÇİNE gömüyor:
 *     $"{app.MessageKey} — {detail}"      →  "ftp.user_exists — CreateUser failed: ..."
 * Ortaya çıkan bileşik dizge HİÇBİR sözlükte yok; `t()` onu bulamayınca olduğu gibi basıyor ve
 * kullanıcı kırmızı şeritte ham anahtar görüyor. Doğru çözüm arka ucun anahtarı `message`,
 * ayrıntıyı `details` alanında AYRI göndermesidir; o dosyalar bu çalışmanın kapsamı dışında
 * olduğu için ön yüz burada dizgeyi geri ayrıştırıp anahtarı çevrilebilir hâle getiriyor.
 * Arka uç düzeltildiğinde bu fonksiyon zararsız biçimde etkisiz kalır (ayırıcı bulunmaz,
 * dizge olduğu gibi döner) — yani sonradan sökmek zorunlu değil.
 *
 * Desteklenen ayırıcılar arka uçta GERÇEKTEN kullanılanlardır: " — " (SitesController,
 * SiteCascadeDeletionService), ": " (DomainRefreshService, BackupService) ve satır sonu
 * (ComponentBootstrapper, PhpService).
 */
export function splitCompositeMessage(raw: string): SplitMessage {
  const trimmed = (raw ?? "").trim();
  if (!trimmed) {
    return { key: "", detail: "" };
  }

  // Ayırıcı: em/en tire, düz tire, iki nokta ya da satır sonu. Tire biçimlerinin çevresinde
  // boşluk ARANIR; aksi hâlde `ssl.fallback.acme-failed` gibi anahtarlar ortadan bölünürdü.
  const match = /^([^\s:]+?)(?:\s+[—–-]\s+|:\s+|\s*\r?\n\s*)([\s\S]+)$/.exec(trimmed);
  if (!match) {
    return { key: trimmed, detail: "" };
  }

  const [, candidate, detail] = match;
  if (!looksLikeMessageKey(candidate)) {
    // Baştaki parça anahtar değilse metin kullanıcı için yazılmış demektir: BÖLME.
    return { key: trimmed, detail: "" };
  }

  return { key: candidate, detail: detail.trim() };
}

/**
 * Yanıtı hiç alınamamış ya da zarfsız gelmiş isteği ÇEVRİLEBİLİR bir anahtara indirger.
 *
 * NEDEN: eskiden bu yol ham İngilizce dönüyordu — Türkçe panelde kullanıcı `Network Error` ya da
 * `HTTP 502: Bad Gateway` görüyordu. Sayı ve İngilizce metin, "internetim mi gitti, sunucu mu
 * çöktü, ben mi yanlış yaptım" sorusunu cevaplamıyor.
 *
 * Durum kodunun kendisi KAYBOLMAZ: `describeTransport` onu ayrıntı satırına yazar.
 */
export function classifyTransport(failure: TransportFailure): string {
  const code = (failure.code ?? "").toUpperCase();
  const rawMessage = (failure.message ?? "").toLowerCase();

  // Zaman aşımı ağ kopukluğundan ÖNCE bakılır: axios ikisini de yanıtsız bırakır, ama kullanıcı
  // için "sunucu geç kaldı" ile "sunucuya hiç ulaşılamadı" farklı işlerdir.
  if (code === "ECONNABORTED" || code === "ETIMEDOUT" || rawMessage.includes("timeout")) {
    return "common.timeout";
  }

  if (failure.status === undefined) {
    // Axios'tan gelmeyen hata bir AĞ sorunu değildir: ekranın kendi kodunda patlayan bir şeydir.
    return failure.isAxios === false ? "common.unexpected_error" : "common.network_error";
  }

  if (failure.status === 401) {
    return "auth.unauthorized";
  }

  if (failure.status === 403) {
    return "common.forbidden";
  }

  if (failure.status === 404) {
    return "common.not_found";
  }

  if (failure.status >= 500) {
    return "common.server_error";
  }

  return "common.request_failed";
}

/**
 * Ayrıntı satırı için taşıma bilgisi. Sunucu zarfı bir mesaj verdiyse BOŞ döner: `t()` zaten
 * anlamlı bir cümle basacaktır, altına "HTTP 400 Bad Request" eklemek gürültüdür.
 * Zarf yoksa durum kodu yazılır — yöneticinin teşhis için ihtiyaç duyduğu tek şey odur.
 */
export function describeTransport(failure: TransportFailure): string {
  if (failure.envelope?.message) {
    return "";
  }

  if (failure.status === undefined) {
    return "";
  }

  const statusText = (failure.statusText ?? "").trim();
  return statusText ? `HTTP ${failure.status} ${statusText}` : `HTTP ${failure.status}`;
}

/**
 * Ekranda gösterilecek anahtar. Sunucu zarfı varsa ONA güvenilir (bileşik ise ayrıştırılır);
 * yoksa taşıma sınıflandırmasına düşülür.
 */
export function resolveFailureKey(failure: TransportFailure): string {
  const envelopeMessage = failure.envelope?.message?.trim();
  if (envelopeMessage) {
    return splitCompositeMessage(envelopeMessage).key;
  }

  return classifyTransport(failure);
}

/**
 * Ekranda anahtarın ALTINA yazılacak ayrıntı. Üç kaynak sırayla denenir:
 * 1) zarfın `details` alanı (arka ucun doğru yolu),
 * 2) bileşik mesajın içine gömülmüş ayrıntı (yukarıdaki telafi),
 * 3) taşıma bilgisi (HTTP durumu).
 */
export function resolveFailureDetail(failure: TransportFailure): string {
  const envelopeDetails = failure.envelope?.details?.trim();
  if (envelopeDetails) {
    return envelopeDetails;
  }

  const envelopeMessage = failure.envelope?.message?.trim();
  if (envelopeMessage) {
    return splitCompositeMessage(envelopeMessage).detail;
  }

  return describeTransport(failure);
}
