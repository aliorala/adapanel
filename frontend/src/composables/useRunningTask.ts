import { computed, onScopeDispose, ref } from "vue";

/**
 * SÜREN bir işin görünür durumu (etiket + geçen süre) — TEK KAYNAK.
 *
 * NEDEN VAR: kullanıcı "şimdi yedekle" deyip bekledi ve işin sürdüğünü mü bittiğini mi
 * anlayamadı. Düğmenin `disabled` olması "sürüyor" DEMEK DEĞİLDİR: metin değişmezse ekran
 * donmuş görünür ve kullanıcı sayfayı yeniler (yedeği ikinci kez tetikler).
 *
 * SÜRE ARTIK SUNUCUDAN DA ALINABİLİR (`begin(label, { startedAtUtc, serverNowUtc })`).
 * Öncesinde sayaç yalnız kendi `begin()` anından ölçüyordu; yenilenmiş bir sayfada sunucudan
 * okunan 40 dakikalık bir iş için "0:05" yazmak, işi YENİ BAŞLAMIŞ göstermek olurdu.
 *
 * SAAT SAPMASI DÜŞÜRÜLÜR: fark tarayıcının saatiyle değil, sunucunun kendi "şimdi"siyle
 * hesaplanır (`serverNowUtc - startedAtUtc`); tarayıcı yalnız o andan sonraki geçen süreyi
 * ekler. Saati dakikalarca sapmış bir istemcide bile süre doğru kalır, negatife düşmez.
 */
export type RunningTaskStart = {
  /** İşin SUNUCUDA başladığı an (ISO 8601, UTC). */
  startedAtUtc?: string;
  /** Sunucunun o yanıttaki "şimdi"si (ISO 8601, UTC). Yoksa tarayıcının saati kullanılır. */
  serverNowUtc?: string;
};

/**
 * ISO metnini milisaniyeye çevirir; okunamıyorsa `undefined`.
 *
 * TAHMİN YOK: bozuk/eksik bir zaman "şimdi" sayılsaydı panel, saatlerdir süren bir işe
 * "0:00" derdi — yani ölçemediği şeyi ölçmüş gibi gösterirdi.
 */
function toMillis(value: string | undefined): number | undefined {
  if (!value) return undefined;
  const parsed = new Date(value).getTime();
  return Number.isNaN(parsed) ? undefined : parsed;
}

export function useRunningTask() {
  const label = ref("");
  const elapsedSeconds = ref(0);
  let timer: ReturnType<typeof setInterval> | undefined;
  let disposed = false;

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

  /**
   * İşi "sürüyor" olarak işaretler.
   *
   * @param taskLabel Ne sürüyor (çevrilmiş metin).
   * @param serverStart Sunucudan okunan başlangıç. VERİLMEZSE süre bu andan ölçülür — bu
   *   sekmede başlatılan işler için doğru olan budur (bkz. BackupView, SiteBackupView).
   */
  function begin(taskLabel: string, serverStart?: RunningTaskStart) {
    if (disposed) return;
    stopTimer();
    label.value = taskLabel;

    const startedAtMs = toMillis(serverStart?.startedAtUtc);
    // Sunucu "şimdi"si yoksa tarayıcınınki kullanılır; o durumda saat sapması süreye yansır ama
    // hiç süre göstermemekten iyidir (yine de negatif olamaz, aşağıda kırpılıyor).
    const serverNowMs = toMillis(serverStart?.serverNowUtc) ?? Date.now();
    const baseSeconds =
      startedAtMs === undefined ? 0 : Math.max(0, Math.floor((serverNowMs - startedAtMs) / 1000));

    elapsedSeconds.value = baseSeconds;
    const startedAt = Date.now();
    // Sayaç, geçen süreyi TİK SAYARAK değil başlangıç anından ÖLÇEREK bulur: sekme arka plana
    // alındığında tarayıcı zamanlayıcıyı kısar ve tik sayan bir sayaç gerçeğin gerisinde kalırdı.
    timer = setInterval(() => {
      elapsedSeconds.value = baseSeconds + Math.floor((Date.now() - startedAt) / 1000);
    }, 1000);
  }

  function end() {
    stopTimer();
    label.value = "";
    elapsedSeconds.value = 0;
  }

  // Ekran değişince zamanlayıcı ARKADA KALMAZ (bileşen yok olduktan sonra ref yazan bir
  // interval sızıntıdır).
  onScopeDispose(() => { disposed = true; end(); });

  return { running, label, elapsedSeconds, elapsedText, begin, end };
}

/**
 * SUNUCUDA süren işin sayfa yenilendikten sonra da görünmesi.
 *
 * NEDEN AYRI BİR PARÇA: uzun uçların hiçbiri işi arka plana almıyor — HTTP isteği iş bitene
 * kadar açık kalıyor. Kullanıcı sayfayı yenilediğinde iş sunucuda DEVAM ediyor ama panelde
 * "sürüyor" bilgisi tamamen kayboluyordu: ekran boş görünüyor, kullanıcı işi ikinci kez
 * tetikliyordu (iki saatlik ikinci bir yedek). Sunucu artık çalışan-iş kaydını `.../running`
 * ucundan veriyor; burası o ucu yoklar ve şeridi besler.
 *
 * NEDEN YÜZDE YOK: sunucu yüzde vermiyor çünkü ÖLÇEMİYOR (yedek toplam dosya sayısını
 * sıkıştırmaya başlamadan bilmez, göç uzak taraftaki dosya sayısını çekmeden bilmez).
 * Uydurulmuş bir yüzde, kullanıcının "%80'de, birazdan biter" diye beklemesine yol açardı.
 *
 * YOKLAMA DESENİ: sabit `setInterval` yerine "bir tur bitince bir sonrakini kur". Yavaş bir
 * yanıt istekleri ÜST ÜSTE bindirmesin diye (aynı kalıp SecurityView'daki antivirüs
 * yoklamasında da kullanılıyor).
 */
export type ServerRunningJob = {
  kind: string;
  title: string;
  startedAtUtc: string;
};

export type ServerInterruptedJob = ServerRunningJob & {
  detectedAtUtc: string;
};

export type ServerRunningJobs = {
  running: ServerRunningJob[];
  interrupted: ServerInterruptedJob[];
  serverTimeUtc: string;
};

/**
 * İş TÜRÜ → etiket anahtarı. Sunucudaki <c>RunningJobKinds</c> ile aynı değerler.
 *
 * NEDEN SABİT EŞLEME (`` t(`panel.runningJob.${kind}`) `` değil): çalışma anında kurulan bir
 * anahtar, çevirisi silindiğinde ekranda HAM olarak belirir ve ölü-anahtar nöbetçisi de o
 * anahtarları "kullanılmıyor" sanıp yanlış yönlendirir. Burada dört anahtar da düz metin.
 */
const JOB_LABEL_KEYS: Record<string, string> = {
  backup: "panel.runningJob.backup",
  restore: "panel.runningJob.restore",
  migration: "panel.runningJob.migration",
  software: "panel.runningJob.software"
};

type Translate = (key: string, named?: Record<string, unknown>) => string;

/**
 * Sunucudan gelen işin ekranda görünecek adı.
 *
 * Tanınmayan bir tür gelirse (sunucu yeni bir tür ekledi, arayüz henüz güncellenmedi) işin
 * KENDİ başlığı gösterilir: uydurma bir etiket ya da boş bir şerit yerine, elde olan gerçek bilgi.
 */
export function runningJobLabel(translate: Translate, job: { kind: string; title: string }) {
  if (job.kind === "backup" && job.title === "automation") {
    return translate("panel.automation.backupSection");
  }
  const key = JOB_LABEL_KEYS[job.kind];
  return key ? translate(key, { title: job.title }) : job.title;
}

export function useServerRunningTask(options: {
  /** Modülün `.../running` ucunu çağıran sarmalayıcı. */
  fetchStatus: () => Promise<ServerRunningJobs>;
  /** Etiket çözücü — genellikle `(job) => runningJobLabel(t, job)`. */
  label: (job: ServerRunningJob) => string;
  /** Yarıda kalmış iş için uyarı metni. */
  interruptedLabel: (job: ServerInterruptedJob) => string;
  /** Yoklama aralığı; 15 sn saatler süren bir işte ekranı canlı tutar, sunucuyu yormaz. */
  pollMs?: number;
}) {
  const pollMs = options.pollMs ?? 15_000;
  const job = ref<ServerRunningJob | null>(null);
  const interrupted = ref<ServerInterruptedJob | null>(null);
  /** Kullanıcı "kesildi" uyarısını kapattı mı (yalnız bu sekme için). */
  const interruptedDismissed = ref(false);
  let timer: ReturnType<typeof setTimeout> | undefined;
  let disposed = false, generation = 0;
  let pending: Promise<void> | undefined;

  // Geçen süre AYNI sayaçtan okunur: sunucudan gelen başlangıç anı `begin()`e verilir, böylece
  // yenilenmiş sayfa "0:05" değil gerçek süreyi ("42:13") yazar.
  const task = useRunningTask();

  const interruptedText = computed(() =>
    !interruptedDismissed.value && interrupted.value ? options.interruptedLabel(interrupted.value) : "");

  function stop() {
    ++generation;
    pending = undefined;
    if (timer !== undefined) {
      clearTimeout(timer);
      timer = undefined;
    }
  }

  /**
   * Sunucudaki durumu bir kez okur.
   *
   * HATAYI YUTAR ama DURUMU BOZMAZ: ağ koptuğunda "iş yok" demek, süren bir işi ekrandan
   * silmek olurdu. Okunamadıysa en son bilinen durum ekranda kalır.
   */
  async function refresh() {
    if (disposed) return;
    if (pending) return pending;
    const requestGeneration = generation;
    pending = readStatus(requestGeneration).finally(() => {
      if (generation === requestGeneration) pending = undefined;
    });
    return pending;
  }

  async function readStatus(requestGeneration: number) {
    try {
      const status = await options.fetchStatus();
      if (disposed || requestGeneration !== generation) return;
      const suren = status.running.length > 0 ? status.running[0] : null;

      // Sayaç YALNIZ iş değiştiğinde yeniden kurulur; her yoklamada `begin()` çağırmak sayacı
      // 15 saniyede bir sıfırlayıp yeniden hesaplatırdı (görünürde zıplayan bir süre).
      if (suren && suren.startedAtUtc !== job.value?.startedAtUtc) {
        task.begin(options.label(suren), {
          startedAtUtc: suren.startedAtUtc,
          serverNowUtc: status.serverTimeUtc
        });
      } else if (!suren && job.value) {
        task.end();
      }

      job.value = suren;

      const yeni = status.interrupted.length > 0 ? status.interrupted[0] : null;
      // Uyarı YENİ bir kesilmeyse kapatma sıfırlanır; aynı kayıt için kapalı kalır.
      if (yeni?.detectedAtUtc !== interrupted.value?.detectedAtUtc) {
        interruptedDismissed.value = false;
      }
      interrupted.value = yeni;
    } catch {
      // Yoklama hatası kullanıcıya kırmızı kutu olarak GÖSTERİLMEZ: bu bir arka plan
      // tazelemesidir, kullanıcının bir eylemi değil. Kırmızı kutu, asıl yaptığı işle ilgisi
      // olmayan bir gürültü olurdu.
    }
  }

  function dismissInterrupted() {
    interruptedDismissed.value = true;
  }

  /** Yoklamayı işin varlığına göre açıp kapatır — iş yokken sunucu boşuna yorulmaz. */
  function syncPolling() {
    if (disposed) return;
    if (job.value !== null) {
      if (timer === undefined) {
        timer = setTimeout(tick, pollMs);
      }
      return;
    }

    stop();
  }

  async function tick() {
    const requestGeneration = generation;
    timer = undefined;
    await refresh();
    if (requestGeneration === generation) syncPolling();
  }

  /** Ekran açılırken bir kez oku; sunucuda iş varsa yoklama kendiliğinden başlar. */
  async function start() {
    if (disposed) return;
    const requestGeneration = generation;
    await refresh();
    if (requestGeneration === generation) syncPolling();
  }

  // Bileşen yok olduktan sonra yoklamaya devam eden bir zamanlayıcı sızıntıdır.
  onScopeDispose(() => { disposed = true; stop(); });

  return {
    /** Sunucuda GERÇEKTEN süren iş (yoksa null) — ekranlar bittiğini buradan anlar. */
    job,
    running: task.running,
    label: task.label,
    elapsedText: task.elapsedText,
    interruptedText,
    refresh,
    start,
    stop,
    dismissInterrupted
  };
}
