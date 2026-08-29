import { ref } from "vue";
import { useI18n } from "vue-i18n";

import { extractApiDetails, extractApiMessage } from "../stores/auth";
import { looksLikeMessageKey, splitCompositeMessage } from "../utils/apiError";

/**
 * Ekran üstü bildirim şeridi + API hata çevirisi — TEK KAYNAK.
 *
 * NEDEN: `resolveApiMessage` 42 ekrana, `showNotice` 41 ekrana ELLE kopyalanmıştı. Kopyaların
 * bir kısmı `t()` çağrısını unutmuştu; sonuç: kullanıcı `files.path_outside_root` gibi HAM
 * anahtar görüyordu. Kopya olduğu sürece bu hata sınıfı geri gelir — bu yüzden tek yerde.
 *
 * Kullanım:
 *   const { notice, noticeError, showNotice, showError, resolveApiMessage } = useNotice();
 *
 * Şablonda DOĞRU kalıp (ekran okuyucu duyar):
 *   <p v-if="notice" :class="['notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">
 */
export function useNotice() {
  const { t } = useI18n();

  const notice = ref("");
  const noticeError = ref(false);

  /**
   * Backend HAM `MessageKey` döndürür (ör. `files.path_outside_root`); metni frontend çevirir.
   * Çeviri yoksa anahtarın kendisi gösterilir — sessizce boş bırakmaktan iyidir.
   *
   * Anahtarın serbest metne gömüldüğü ("anahtar — ayrıntı") ve ağ/zaman aşımı durumları
   * `utils/apiError.ts`'te çözülür; buradan bakınca ikisi de sıradan bir anahtardır.
   */
  function resolveApiMessage(error: unknown): string {
    const key = extractApiMessage(error);
    const translated = t(key);
    const message = translated === key ? key : translated;
    const details = extractApiDetails(error);
    return details ? `${message}\n\n${details}` : message;
  }

  function showNotice(message: string, isError = false) {
    notice.value = message;
    noticeError.value = isError;
  }

  /** Hata nesnesini çevirip kırmızı şeritte gösterir — `showNotice(resolveApiMessage(e), true)` kısayolu. */
  function showError(error: unknown) {
    showNotice(resolveApiMessage(error), true);
  }

  function clearNotice() {
    notice.value = "";
    noticeError.value = false;
  }

  return { notice, noticeError, showNotice, showError, resolveApiMessage, clearNotice };
}

/**
 * Sunucudan gelen SERBEST bir alanın ekran metni — hata yolu DIŞINDA.
 *
 * NEDEN AYRI: `resolveApiMessage` yalnız `catch` bloğundaki hata NESNESİNİ çözer. Oysa panelin
 * bazı alanları hata değildir, sıradan veridir ve yine de sunucudan metin taşır: uygulama
 * kataloğunun açıklaması (`AppStoreView`), DNS nöbetçisinin son onarım durumu
 * (`ServerHealthView`), site onarım bulgusunun ayrıntısı (`SiteRepairNotice`). Bu üç yer aynı
 * kararı üç ayrı yerde veriyordu — biri (`AppStoreView`) hiç vermiyordu ve ham Türkçe basıyordu.
 *
 * ÖLÇÜLEN HATA: `AppStoreService` şablon açıklamalarını sabit Türkçe gönderiyordu ve
 * `AppStoreView.vue` onları `t()`'den geçirmeden basıyordu; dili İngilizce olan müşteri
 * katalog ekranında Türkçe cümle görüyordu. Aynı sınıf `DnsService`'in
 * `lastAutoRepairStatus`'ünde de vardı ("ok: DNS servisi yeniden başlatıldı…").
 *
 * KURAL — `utils/apiError`'ın kuralının AYNISI, yenisi değil:
 * anahtar biçimindeyse çevrilir; değilse OLDUĞU GİBİ basılır. Bu ikinci yarısı bir teselli
 * değil, bilinçli karar: Windows'un döndürdüğü `Auto`/`Manual`, `service=…; listening=…` gibi
 * ölçülmüş değerler ve `SQLite`/`web.config` gibi ürün adları çevrilemez — ham anahtar
 * basmaktansa ham değer basmak dürüsttür. Bileşik gelen ("anahtar — ayrıntı") dizgede yalnız
 * baştaki anahtar çevrilir, teşhis kısmı korunur.
 */
export function useServerText() {
  const { t } = useI18n();

  function serverText(raw: string | null | undefined): string {
    const trimmed = (raw ?? "").trim();
    if (!trimmed) {
      return "";
    }

    const { key, detail } = splitCompositeMessage(trimmed);
    if (!looksLikeMessageKey(key)) {
      return trimmed;
    }

    const translated = t(key);
    const head = translated === key ? key : translated;
    return detail ? `${head} — ${detail}` : head;
  }

  return { serverText };
}

/**
 * Bir bölümün "okunamadı" durumu — BOŞ durumdan ayırt edilebilir olmalı.
 *
 * NEDEN: 40 noktada `catch` state'i `null`/`[]` yapıp kullanıcıya hiçbir şey demiyordu. Kullanıcı
 * "kayıt yok" mu, "okunamadı" mı ayırt edemiyordu; hata sessizce yutulup bölüm boş görünüyordu.
 *
 * Kullanım:
 *   const gitLoad = useSectionError();
 *   try { ...; gitLoad.clear(); } catch (e) { gitLoad.capture(e); }
 * Şablonda:
 *   <p v-if="gitLoad.message.value" class="notice error" role="alert">
 *     {{ gitLoad.message.value }}
 *     <button type="button" class="secondary compact" @click="loadGit">{{ t("panel.common.retry") }}</button>
 *   </p>
 */
export function useSectionError() {
  const { t } = useI18n();
  const message = ref("");

  function capture(error: unknown) {
    const key = extractApiMessage(error);
    const translated = t(key);
    message.value = translated === key ? key : translated;
  }

  function clear() {
    message.value = "";
  }

  return { message, capture, clear };
}
