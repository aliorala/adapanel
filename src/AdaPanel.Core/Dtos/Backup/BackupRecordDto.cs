namespace AdaPanel.Core.Dtos.Backup;

public sealed class BackupRecordDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string Format { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public long Size { get; set; }

    public string Status { get; set; } = string.Empty;

    public string TargetType { get; set; } = "local";

    public string RemoteUri { get; set; } = string.Empty;

    /// <summary>
    /// Yedek dosyası AES-256-GCM ile şifreli mi (dosya adı <c>.enc</c> ile biter).
    /// <para>
    /// Bu alan EKRAN İÇİNDİR; geri yüklerken biçim kararı dosyanın kendi başlığından verilir
    /// (eski/elle kopyalanmış dosyalarda kayıt yanılabilir, dosya başlığı yanılmaz).
    /// </para>
    /// <para>
    /// <b>Şifreliyse geri yükleme parola ister ve PAROLA KAYBOLURSA YEDEK GERİ GETİRİLEMEZ.</b>
    /// </para>
    /// </summary>
    public bool Encrypted { get; set; }

    /// <summary>
    /// Yedek dosyası ZSTD ile sıkıştırılmış mı (dosya adı <c>.zst</c> ile biter; şifreliyse
    /// <c>.sql.zst.enc</c>).
    /// <para>
    /// Bu alan da EKRAN İÇİNDİR — <see cref="Encrypted"/> ile aynı disiplin: geri yüklerken
    /// "açılacak mı" kararı dosyanın KENDİ BAŞLIĞINDAN verilir. Sıkıştırma öncesi alınmış eski düz
    /// <c>.sql</c> yedeklerde bu alan yoktur/false'tur ve onlar aynen çalışmaya devam eder.
    /// </para>
    /// </summary>
    public bool Compressed { get; set; }

    /// <summary>
    /// Son geri yüklemenin YARIM KALAN adımları. Boş değilse <see cref="Status"/> da
    /// <c>restored_with_errors</c> olur — "restored" yazıp sorunu yutmak yasaktır.
    /// </summary>
    public List<BackupRestoreWarningDto> Warnings { get; set; } = [];

    /// <summary>
    /// Geri yükleme üzerine yazmadan önce sitenin O ANKİ içeriğinin taşındığı kardeş klasör.
    /// Kullanıcının "geri yükleme yanlış yedekti" dediğinde döneceği tek yer burasıdır.
    /// </summary>
    public string PreservedPath { get; set; } = string.Empty;

    // =================================================================================
    // UZLAŞTIRMA (yedek GERÇEKTEN duruyor mu?)
    //
    // ÖLÇÜLEN HATA: liste, kaydı JSON'dan okuyup olduğu gibi gösteriyordu. Diskten ya da
    // FTP'den ELLE silinmiş bir yedek panelde "geri yüklenebilir" olarak durmaya devam
    // ediyordu; kullanıcı bunu ancak GERİ YÜKLEMEYE ÇALIŞTIĞINDA, yani felaket anında
    // öğreniyordu. Yedeğin var olduğunu SANMAK, hiç yedeğin olmamasından daha tehlikelidir.
    //
    // İki alan ayrı tutuldu çünkü ÖLÇÜM MALİYETLERİ farklı:
    //  - yerel varlık: bir dosya sistemi sorgusu, her listelemede yapılabilir;
    //  - uzak varlık: ağ isteği — her listelemede yapılsa panel yavaşlar ve hedefin hız
    //    sınırına takılırdı. Bu yüzden yalnızca DOĞRULAMA çalıştığında ölçülür ve sonucu
    //    burada SAKLANIR.
    // =================================================================================

    /// <summary>
    /// Yerel dosya listeleme ANINDA yok. Diskte saklanmaz — her listelemede yeniden ölçülür,
    /// yoksa dosyayı geri koyan bir kullanıcı kalıcı "kayıp" damgasıyla kalırdı.
    /// </summary>
    public bool LocalMissing { get; set; }

    /// <summary>
    /// Uzak kopya SON DOĞRULAMADA yoktu. Varsayılan <c>false</c> "yok" değil "ölçülmedi"
    /// anlamına gelir; ikisini karıştırmak, hedefe erişilemeyen bir günde bütün yedekleri
    /// kayıp saymak olurdu.
    /// </summary>
    public bool RemoteMissing { get; set; }

    /// <summary>Uzak kopyanın en son ne zaman ölçüldüğü (hiç ölçülmediyse <c>null</c>).</summary>
    public DateTime? RemoteVerifiedUtc { get; set; }

    /// <summary>
    /// Uzak kopyanın SON KESİN ölçümü. <see cref="BackupPresence"/> değerlerinden biri:
    /// <c>none</c> (bu yedeğin uzak kopyası hiç yok), <c>unknown</c> (ölçülmedi ya da ölçülemedi),
    /// <c>present</c>, <c>missing</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden <see cref="RemoteMissing"/> yetmiyor:</b> o alan İKİ değerlidir ve <c>false</c>
    /// hem "ölçtük, duruyor" hem "hiç ölçmedik" anlamına gelir. Arayüz ikisini ayırmak için
    /// <see cref="RemoteVerifiedUtc"/>'nin null olup olmadığına bakmak zorundaydı — yani üç
    /// değerli bir gerçek, iki alana bölünmüş hâlde taşınıyordu ve okuyan taraf birleştirmeyi
    /// unuttuğunda "ölçmedim" sessizce "duruyor" oluyordu.
    /// </para>
    /// <para>
    /// <b>ULAŞILAMADI, "YOK" DEĞİLDİR:</b> hedefe erişilemeyen bir ölçümde bu alan
    /// DEĞİŞTİRİLMEZ — son kesin ölçüm neyse o kalır. Ağ arızasını "yedek silinmiş" diye
    /// yazmak, kullanıcıyı yedeği olmadığına inandırırdı.
    /// </para>
    /// </remarks>
    public string RemoteState { get; set; } = BackupPresence.Unknown;

    /// <summary>
    /// <b>OFF-SITE KOPYA KAYBOLMUŞ:</b> uzak kopya ÖLÇÜLDÜ ve YOK, yerel dosya ise duruyor.
    /// </summary>
    /// <remarks>
    /// Felaket senaryosunun tam ortası: kullanıcı "off-site yedeğim var" sanır, oysa yalnız aynı
    /// makinedeki kopya kalmıştır. Uzak taraf kendi saklama politikasıyla ya da biri FTP
    /// istemcisiyle sildiğinde panel bugüne kadar hiçbir şey söylemiyordu. Yalnız KESİN ölçümde
    /// <c>true</c> olur; "ölçemedim" durumunda ASLA.
    /// </remarks>
    public bool OffsiteCopyLost { get; set; }

    /// <summary>
    /// <b>ARTIK HİÇBİR KOPYA YOK:</b> yerel dosya yok ve (uzak kopya ölçülüp yok bulundu ya da
    /// bu yedeğin hiç uzak kopyası olmadı).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Kayıt yine de SİLİNMEZ.</b> Kullanıcı "silinmişse görünmesin" dedi ama kaydı sessizce
    /// silmek geçmişi yok etmektir: "yedeğim vardı, ne oldu?" sorusunun cevabı kalmaz ve panelin
    /// yedeği kendiliğinden buharlaştırdığı hissi doğar. Doğrusu DAMGALAMAKTIR — kayıt
    /// "artık yok" olarak görünür, silme kararı kullanıcınındır. Aynı disiplin
    /// <c>VerifyBackupsAsync</c>'te de yazılı.
    /// </para>
    /// <para>
    /// "Ölçemedim" hâli buraya GİRMEZ: uzak kopyası olan ama ölçülememiş bir kayıt, yereli
    /// yoksa bile "artık yok" DEĞİLDİR.
    /// </para>
    /// </remarks>
    public bool AllCopiesMissing { get; set; }

    /// <summary>
    /// Bu yedek SİLİNDİ sayılıyor ama UZAK kopyası hedefte kaldı (<see cref="Status"/> =
    /// <c>deleted_remote_failed</c>). Sebep <see cref="Warnings"/> içinde
    /// <c>backup.remote_delete_failed</c> anahtarıyla durur.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ÖLÇÜLEN BOŞLUK:</b> <c>deleted_remote_failed</c> dizesi ne <c>frontend/</c> ne de
    /// <c>AdaPanel.Api</c> altında geçiyordu. Yani panel kaydı işaretliyor, kayıt listede duruyor
    /// ama HİÇBİR EKRAN bunu ayırt etmiyordu: uzak dosya yetim kalıyor ve panel bir daha ondan
    /// söz etmiyordu. Durum dizesini arayüzde ELLE karşılaştırmak yerine ayrı bir alan
    /// verilmesinin sebebi, o dizenin bir daha iki yerde ayrı ayrı yazılmaması.
    /// </para>
    /// <para>
    /// <see cref="LocalMissing"/> ile aynı disiplin: diskte SAKLANMAZ, her listelemede
    /// <see cref="Status"/>'ten türetilir — iki gerçeklik kaynağı olmasın.
    /// </para>
    /// </remarks>
    public bool RemoteDeleteFailed { get; set; }

    // =================================================================================
    // ARTIMSAL (INCREMENTAL) ZİNCİR — Plesk'in kanıtlanmış modeli.
    //
    // Plesk'te bir TAM yedek + ardından gelen ARTIMSAL yedekler bir ZİNCİR oluşturur:
    // artimsal yedek yalnızca "son yedekten beri DEĞİŞEN"i taşır, geri yükleme ise tam
    // yedekten başlayıp halkaları SIRAYLA uygular. Halkalar birbirine BAĞIMLIDIR:
    // ortadaki bir halka giderse ondan SONRAKİ bütün halkalar çöpe döner.
    //
    // Bu alanlar eski kayıtlarda BOŞ kalır ve boş değerler "zincirsiz tekil tam yedek"
    // anlamına gelir — geriye dönük uyumluluk bu yüzden kendiliğinden sağlanır.
    // =================================================================================

    /// <summary>
    /// Zincir kimliği. Aynı <c>ChainId</c>'yi taşıyan kayıtlar TEK bir geri yükleme zinciridir.
    /// Boş ise kayıt zincire ait değildir (tekil, kendi başına yeterli tam yedek).
    /// </summary>
    public string ChainId { get; set; } = string.Empty;

    /// <summary>
    /// Zincirdeki sıra: <c>0</c> TAM yedek, <c>1..n</c> artımsal halkalar. Geri yükleme bu
    /// sıraya göre uygulanır; sıra bozulursa eski dosya yenisinin üzerine yazılır.
    /// </summary>
    public int ChainIndex { get; set; }

    /// <summary>"full" (tam) veya "incremental" (artımsal). Varsayılan tam.</summary>
    public string BackupMode { get; set; } = "full";

    /// <summary>
    /// Bu artımsal halkanın DAYANDIĞI bir önceki halkanın kimliği (tam yedekte boş).
    /// Zincirin kopup kopmadığı bu bağdan ölçülür.
    /// </summary>
    public string BaseBackupId { get; set; } = string.Empty;

    /// <summary>
    /// Abonelik yedeğinde sahibinin kullanıcı kimliği ("N" biçimi). Kiracı izolasyonunun
    /// kaydı: bir aboneliğin yedeği yalnızca kendi sahibine gösterilir/geri yüklenir.
    /// </summary>
    public string SubscriptionUserId { get; set; } = string.Empty;

    /// <summary>
    /// Yedeğin İÇİNDEKİ mantıksal öğe sayısı (site, veritabanı, posta alanı, FTP hesabı).
    /// Abonelik yedeğinde ekranda "8 öğe" demeyi mümkün kılar; 0 = ölçülmedi.
    /// </summary>
    public int ItemCount { get; set; }
}
