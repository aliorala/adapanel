/*
 * GÖÇTE DOĞRULANAMAYAN UZAK KİMLİĞİN ÇÖZÜMLENMESİ.
 *
 * Arka uç, kaynak sunucunun kimliğini doğrulayamadığında `migration.tls.untrusted` ya da
 * `migration.hostkey.untrusted` anahtarıyla hata döner ve teşhis alanına (`details`) tek satırlık,
 * ` · ` ile ayrılmış bir kayıt yazar:
 *
 *   SHA256:1A:2B:… · sebep=self_signed,name_mismatch · konu=CN=… · veren=CN=… · gecerli=…..…
 *
 * İLK parça HER ZAMAN parmak izidir; "kabul et" düğmesinin geri göndereceği değer odur. Biçim
 * arka uçta testle sabitlenmiştir (`MigrationRemoteTrustTests`), bu yüzden buradaki çözümleme
 * sessizce kaymaz.
 *
 * NEDEN AYRI DOSYA: aynı çözümleme hem keşif hatasında (istisna) hem göç adımı sonucunda (adımın
 * `detail` alanı) gerekiyor. İki nüsha yazmak, birinin düzelip ötekinin sessizce farklı davranması
 * demekti — bu depoda defalarca ölçülmüş hata sınıfı.
 */

/** Doğrulanamayan uzak kimliğin ekrana çizilebilir hâli. */
export type RemoteTrustPrompt = {
  /** `migration.tls.untrusted` ya da `migration.hostkey.untrusted`. */
  messageKey: string;
  /** `SHA256:…` — yöneticinin teyit edip kabul edeceği değer. */
  fingerprint: string;
  /** Makine okunur ret sebepleri (`self_signed`, `expired`…). */
  reasons: string[];
  subject: string;
  issuer: string;
  validity: string;
};

const TRUST_MESSAGE_KEYS = ["migration.tls.untrusted", "migration.hostkey.untrusted"];

/*
 * Onaltılık (`SHA256:1A:2B:…`) ve base64 (`SHA256:wjZ1j61…`) yazımlarının İKİSİNİ de tanır: ilki
 * TLS sertifikası, ikincisi SSH host key. Ayraç ` · ` boşluk içerdiğinden ve boşluk sınıfın
 * DIŞINDA olduğundan eşleşme parmak izinin sonunda durur.
 */
const FINGERPRINT = /SHA256:[A-Za-z0-9+/:=_-]+/;

function alan(details: string, ad: string): string {
  const eslesme = new RegExp(`${ad}=([^·]*)`).exec(details);
  return eslesme ? eslesme[1].trim() : "";
}

/**
 * Hata bir GÜVEN reddi mi — öyleyse ekrana çizilecek hâlini üretir, değilse `null`.
 *
 * `null` dönmesi bilinçli olarak "bu sıradan bir hata" demektir: çağıran taraf o zaman normal hata
 * şeridini gösterir. Her hatayı güven reddi sanmak, gerçek bağlantı arızalarında yöneticiye
 * anlamsız bir "parmak izini kabul et" düğmesi gösterirdi.
 */
export function parseRemoteTrustPrompt(messageKey: string, details: string): RemoteTrustPrompt | null {
  if (!TRUST_MESSAGE_KEYS.includes(messageKey)) {
    return null;
  }

  const fingerprint = FINGERPRINT.exec(details ?? "")?.[0] ?? "";
  if (!fingerprint) {
    // Anahtar doğru ama parmak izi okunamadı: kabul düğmesi gösterilemez, çünkü NEYİ kabul
    // ettireceğini bilmiyoruz. Sessizce boş bir onay göstermektense sıradan hata yolu doğrudur.
    return null;
  }

  return {
    messageKey,
    fingerprint,
    reasons: alan(details, "sebep")
      .split(",")
      .map((reason) => reason.trim())
      .filter((reason) => reason.length > 0),
    subject: alan(details, "konu"),
    issuer: alan(details, "veren"),
    validity: alan(details, "gecerli")
  };
}
