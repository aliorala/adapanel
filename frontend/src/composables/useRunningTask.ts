import { computed, onScopeDispose, ref } from "vue";

/**
 * SÜREN bir işin görünür durumu (etiket + geçen süre) — TEK KAYNAK.
 *
 * NEDEN VAR: kullanıcı "şimdi yedekle" deyip bekledi ve işin sürdüğünü mü bittiğini mi
 * anlayamadı. Düğmenin `disabled` olması "sürüyor" DEMEK DEĞİLDİR: metin değişmezse ekran
 * donmuş görünür ve kullanıcı sayfayı yeniler (yedeği ikinci kez tetikler).
 *
 * NEDEN SUNUCUYU YOKLAMIYOR: yedek uçları (`POST /backup/site`, `POST /automation/backup/run`)
 * işi ARKA PLANA ALMAZ; işlem bitene kadar HTTP isteği açık kalır. Yani "istek uçuşta" ile
 * "yedek sürüyor" AYNI ŞEYDİR ve burada ölçülen süre işin gerçek süresidir.
 *
 * SINIRI: bu durum bu sekmeye aittir. Kullanıcı sayfayı yenilerse ya da yedek ZAMANLANMIŞ
 * olarak (panelin arka plan servisinde) başlarsa buradan görünmez — sunucu tarafında yedek
 * için canlı ilerleme ucu yok (bkz. scratchpad/frontend-yedek-ilerleme.md).
 */
export function useRunningTask() {
  const label = ref("");
  const elapsedSeconds = ref(0);
  let timer: ReturnType<typeof setInterval> | undefined;

  const running = computed(() => label.value !== "");

  /** Geçen süre "1:07" / "12:03" biçiminde; saniye tek başına 90+ saniyede okunmuyordu. */
  const elapsedText = computed(() => {
    const minutes = Math.floor(elapsedSeconds.value / 60);
    const seconds = elapsedSeconds.value % 60;
    return `${minutes}:${String(seconds).padStart(2, "0")}`;
  });

  function stopTimer() {
    if (timer !== undefined) {
      clearInterval(timer);
      timer = undefined;
    }
  }

  function begin(taskLabel: string) {
    stopTimer();
    label.value = taskLabel;
    elapsedSeconds.value = 0;
    const startedAt = Date.now();
    // Sayaç, geçen süreyi TİK SAYARAK değil başlangıç anından ÖLÇEREK bulur: sekme arka plana
    // alındığında tarayıcı zamanlayıcıyı kısar ve tik sayan bir sayaç gerçeğin gerisinde kalırdı.
    timer = setInterval(() => {
      elapsedSeconds.value = Math.floor((Date.now() - startedAt) / 1000);
    }, 1000);
  }

  function end() {
    stopTimer();
    label.value = "";
    elapsedSeconds.value = 0;
  }

  // Ekran değişince zamanlayıcı ARKADA KALMAZ (bileşen yok olduktan sonra ref yazan bir
  // interval sızıntıdır).
  onScopeDispose(stopTimer);

  return { running, label, elapsedSeconds, elapsedText, begin, end };
}
