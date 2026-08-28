using System.Diagnostics;

namespace AdaPanel.Infrastructure.Files;

/// <summary>
/// Bütçeli, bağlantı (junction/symlink) TAKİP ETMEYEN klasör ağacı yürüyüşü — panelde bir ağacın
/// FİZİKSEL yol üzerinden gezildiği TEK yer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ortak:</b> bu yürüyüşün SEKİZ müşterisi var — dosya yöneticisinin "bu klasör ne kadar yer
/// kaplıyor" ucu (<see cref="FileService"/>), dosya yöneticisinin KLASÖR KOPYALAMASI
/// (<c>FileService.KlasoruKopyala</c>) ve kota ÖN ÖLÇÜMÜ (<c>FileService.MeasureAsync</c>),
/// disk KOTASI hesabı
/// (<c>CustomerScopeService.GetDiskUsageMbAsync</c>), YEDEK dosya taraması
/// (<c>BackupService.ScanFiles</c>), göçte <c>.user.ini</c> arayan tarama
/// (<c>SiteMigrationService.SanitizeUserIniFilesAsync</c>), arşivden içe aktarmanın ağaç
/// ölçümü/kopyalaması (<c>SiteHostingSettingsService</c>) ve site silmenin "ne kadarı gitti"
/// sayımı (<c>SiteCascadeDeletionService.CountFilesSafely</c>). Hepsi de kiracının YAZABİLDİĞİ bir ağaçta
/// yürür, yani hepsi aynı üç tuzağa açıktır. Kota yolu bu korumaları yıllarca ıskaladı:
/// <c>Directory.EnumerateFiles(kok, "*", SearchOption.AllDirectories)</c> kullanıyordu ve
/// <b>ölçüldü</b> — o çağrı bağlantıları TAKİP EDER, bütçesi YOKTUR. Kiracının kendi sitesinin
/// köküne kendi üst ağacını gösteren bir kavşak (junction) koyması, kotayı hesaplayan isteğin
/// dönmemesi için yeterliydi. Aynı hata sınıfı bu depoda <c>SiteLogCleanup</c>'ta bir kez daha
/// yaşandı (bkz. <c>AutomationService.RunLogCleanupAsync</c> belgesi) ve orada da çözüm ikinci
/// kopyayı silip TEK kapıya indirmek oldu. Koruma iki yerde ayrı ayrı yazıldığında, birini
/// sıkılaştırıp diğerini unutmak kaçınılmazdır.
/// </para>
/// <para>
/// <b>Bu tip yalnız FİZİKSEL yolu bilir.</b> Sanal yol çözümlemesi, sanal alan (sandbox) denetimi ve
/// yetkilendirme burada YOKTUR ve bilerek yoktur: o kapı çağıranın sorumluluğudur
/// (<c>ResolveTargetAsync</c>/<c>EnsureInsideSandbox</c> ya da kotada IIS'ten okunan site kökü).
/// Buraya bir sanal alan kavramı sokmak, iki farklı çağıranın iki farklı köke sahip olması yüzünden
/// ya kapıyı zayıflatır ya da yürüyüşü kullanılamaz hâle getirirdi.
/// </para>
/// </remarks>
internal static class SafeDirectoryWalk
{
    /// <summary>Öge (dosya + klasör) bütçesi doldu.</summary>
    internal const string SebepOgeButcesi = "files.size_truncated_entries";

    /// <summary>Süre bütçesi doldu.</summary>
    internal const string SebepSureButcesi = "files.size_truncated_time";

    /// <summary>En az bir klasör okunamadı (erişim reddedildi).</summary>
    internal const string SebepErisimReddi = "files.size_truncated_denied";

    /// <summary>
    /// Verilen fiziksel klasörün alt ağacını yürür; bütçe dolarsa DURUR ve sonucu "kısmi" damgalar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Yürüyüş neden ÖZYİNELEMELİ ÇAĞRI değil de YIĞIN:</b> derinliği kullanıcının klasör ağacı
    /// belirler. İç içe geçmiş bir <c>node_modules</c> ağacında derinlik binlere çıkabilir ve
    /// özyinelemeli çağrı orada <c>StackOverflowException</c> atar — bu istisna .NET'te
    /// YAKALANAMAZ, yani bir kiracının klasör ağacı panel sürecini tümüyle öldürürdü. Aynı karar
    /// aramada da verilmiştir (<c>FileService.EnumerateSafeFiles</c>).
    /// </para>
    /// <para>
    /// <b>Neden <c>FileService.EnumerateSafeFiles</c> yeniden kullanılmıyor:</b> o yardımcı tembel
    /// bir DOSYA akışıdır; klasör sayısını görmez, erişim reddini sessizce yutar ve bütçe kavramı
    /// yoktur. Bu yürüyüşün sözleşmesi tam olarak bu üç şeyi (klasör sayısı, atlananın bildirilmesi,
    /// bütçe) gerektirir; ortak olan tek kural (bağlantı takip edilmez) iki yerde de aynıdır ve
    /// gerekçesi <c>FileService.KlasoruKopyala</c>'da yazılıdır.
    /// </para>
    /// <para>
    /// <b>İstisna ATMAZ</b> (iptal hariç): erişim reddi, silinmiş klasör ve dolu bütçe sonucun
    /// içinde bildirilir. Çağıranların biri HTTP cevabı, diğeri kota toplamı üretiyor; ikisi için de
    /// "hiçbir şey" cevabı, kısmi bir alt sınırdan kötüdür.
    /// </para>
    /// </remarks>
    /// <param name="fizikselYol">Yürünecek klasörün tam fiziksel yolu.</param>
    /// <param name="butce">Öge ve süre bütçesi. Aynı nesne birden çok çağrıda PAYLAŞILABİLİR
    /// (kota yolu tek bir aboneliğin bütün sitelerini tek bütçeyle gezer).</param>
    /// <param name="cancellationToken">İptal edilen istekte yürüyüş bırakılır.</param>
    internal static SafeDirectoryWalkResult Yuru(
        string fizikselYol,
        SafeDirectoryWalkBudget butce,
        CancellationToken cancellationToken)
        => Yuru(fizikselYol, butce, null, null, cancellationToken);

    /// <summary>
    /// Yukarıdakinin AĞACI GEZDİREN hâli: kabul edilen her ögeyi ve okunamayan her klasörü
    /// çağırana bildirir. Sayım sözleşmesi ve bütün korumalar birebir aynıdır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden ayrı bir yürüyüş DEĞİL de aynı gövde:</b> bu yardımcının müşterilerinin çoğu
    /// (yedek taraması, göçte <c>.user.ini</c> arama, arşivden içe aktarma kopyası) "boyut toplamı"
    /// değil "ağaçtaki ögeleri GÖRME" istiyordu ve üçü de kendi elle yazdığı yürüyüşünü taşıyordu.
    /// Üçünde de aynı iki koruma eksikti: <c>ReparsePoint</c> atlaması ve bütçe. Onlara ikinci bir
    /// güvenli yürüyüş yazmak, bu dosyanın var oluş sebebini (TEK uygulama) ilk günden bozmak
    /// olurdu; bu yüzden yürüyüş değil, YALNIZ ÇIKTISI genişletildi — geri çağrı verilmediğinde
    /// üretilen makine kodu ile eski hâli arasında fark yoktur.
    /// </para>
    /// <para>
    /// <b>Geri çağrı ne zaman çalışır:</b> öge bütçeden düşüldükten ve bağlantı elemesinden GEÇTİKTEN
    /// sonra. Yani çağıran, yürüyüşün ATLADIĞI bir ögeyi asla görmez — atlananı ikinci bir kuralla
    /// yeniden değerlendirebilseydi koruma çağıran başına delinebilirdi.
    /// </para>
    /// <para>
    /// <b>Sıra GARANTİLİDİR:</b> bir klasör, İÇİNDEKİLERDEN ÖNCE bildirilir (yığın ögeyi görüp
    /// sonra iter). İçe aktarma kopyası buna dayanır: hedefteki alt klasör, içine dosya yazılmadan
    /// önce oluşturulmuş olur.
    /// </para>
    /// <para>
    /// <b>Geri çağrının attığı istisna YUTULMAZ, yukarı çıkar.</b> Bu, metodun "istisna atmaz"
    /// sözleşmesinin tek istisnasıdır (iptalin yanında) ve bilerek böyledir: içe aktarma
    /// kopyalaması yarıda kaldığında site kökünü GERİ SARMAK zorundadır, yani hatayı görmesi şart.
    /// Yutulsaydı yarım kopyalanmış bir site "tamam" sayılırdı.
    /// </para>
    /// </remarks>
    /// <param name="ogeGorulunce">Kabul edilen her dosya/klasör için çağrılır. <c>null</c> ise yalnız sayılır.</param>
    /// <param name="klasorOkunamadi">Okunamayan her klasör için (yol + sebep) çağrılır.
    /// Sonuçtaki <see cref="SafeDirectoryWalkResult.Truncated"/> "bir yerde eksik var" der; bu geri
    /// çağrı NEREDE olduğunu söyler. Yedek yolunun ihtiyacı tam olarak budur: atlanan klasörün ADI
    /// kullanıcıya gösterilir, yoksa "yedeğinizde bir eksik var, nerede olduğunu bilmiyoruz" gibi
    /// işe yaramaz bir uyarı kalırdı.</param>
    /// <param name="baglantiAtlandiginda">En az bir bağlantı (junction/symlink) elendiğinde çağrılır.
    /// <para>
    /// <b>Neden PARAMETRESİZ ve neden ayrı bir geri çağrı:</b> yukarıdaki kural — "çağıran,
    /// yürüyüşün ATLADIĞI bir ögeyi asla görmez" — bir güvenlik kuralıdır: atlananı ikinci bir
    /// kuralla yeniden değerlendirebilen bir çağıran, korumayı kendi içinden delerdi. O yüzden bu
    /// geri çağrıya ATLANAN ÖGE VERİLMEZ; yalnızca "bir şey atlandı" haberi verilir. Kopyalama
    /// yolunun ihtiyacı tam olarak bu kadardır: kullanıcıya "kopya oluştu ama EKSİK"
    /// (<c>files.copy_link_skipped</c>) diyebilmek. Haberi hiç vermeseydik, dosya yöneticisi
    /// bağlantı içeren bir klasörü sessizce eksik kopyalar ve kullanıcı eksik kopyayı tam sanırdı.
    /// </para>
    /// <para>
    /// Atlanan bağlantı <see cref="SafeDirectoryWalkResult.Truncated"/> ÜRETMEZ (gerekçesi aşağıda,
    /// eleme satırında); bu yüzden haber sonuç kaydında değil ayrı bir geri çağrıda taşınır.
    /// </para></param>
    internal static SafeDirectoryWalkResult Yuru(
        string fizikselYol,
        SafeDirectoryWalkBudget butce,
        Action<FileSystemInfo>? ogeGorulunce,
        Action<string, Exception>? klasorOkunamadi,
        CancellationToken cancellationToken,
        Action? baglantiAtlandiginda = null)
    {
        // BOŞ YOL bir hata değil, "sayılacak bir şey yok" demektir: kota yolunda IIS'ten okunan site
        // kökü boş olabilir (bağlama yapılmamış site). Burada istisna atmak, tek bozuk sitenin bütün
        // aboneliğin kota hesabını düşürmesi olurdu.
        if (string.IsNullOrWhiteSpace(fizikselYol))
        {
            return new SafeDirectoryWalkResult(0, 0, 0, false, string.Empty);
        }

        long toplamBayt = 0;
        var dosyaSayisi = 0;
        var klasorSayisi = 0;
        var erisimReddedildi = false;

        var yigin = new Stack<string>();
        yigin.Push(fizikselYol);

        while (yigin.Count > 0)
        {
            // İptal edilen istek: kullanıcı sekmeyi kapattıysa yürüyüş SÜRMEZ. Bütçeden farkı,
            // ortada cevabı bekleyen kimse olmamasıdır — kısmi sonuç üretmenin bir anlamı yok.
            cancellationToken.ThrowIfCancellationRequested();

            var klasor = yigin.Pop();

            IEnumerator<FileSystemInfo> gezgin;
            try
            {
                gezgin = new DirectoryInfo(klasor).EnumerateFileSystemInfos().GetEnumerator();
            }
            catch (UnauthorizedAccessException exception)
            {
                // ERİŞİM REDDİ HESABI ÇÖKERTMEZ ama SESSİZ DE GEÇİLMEZ. Bayrak aşağıda
                // Truncated'a çevrilir; gerekçesi orada yazılı.
                erisimReddedildi = true;
                klasorOkunamadi?.Invoke(klasor, exception);
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                // Yürüyüş sürerken silinmiş klasör — ya da hiç var olmamış kök. Bu bir eksiklik
                // DEĞİL: artık var olmayan bir klasörün diskte yeri de yok, yani toplam "şu an"
                // için doğru kalır. Geri çağrı da ÇALIŞMAZ: yedek yolunda "şu klasör arşive
                // giremedi" demek, ortada klasör yokken kullanıcıyı olmayan bir kayba bakmaya
                // gönderirdi.
                continue;
            }
            catch (IOException exception)
            {
                // GENEL G/Ç HATASI (aygıt hazır değil, ağ payı düştü, dosya sistemi bozuk).
                // ESKİDEN YAKALANMIYORDU ve bu, metodun kendi "istisna ATMAZ" sözleşmesini
                // çiğniyordu: kopmuş tek bir ağ sürücüsü hem kullanıcı listesindeki kota hesabını
                // hem de o gecenin bütün yedeğini istisnayla düşürebilirdi. Erişim reddiyle aynı
                // muamele görür — kısmi sonuç + "kısmi" damgası + nerede olduğunun bildirilmesi.
                erisimReddedildi = true;
                klasorOkunamadi?.Invoke(klasor, exception);
                continue;
            }

            using (gezgin)
            {
                while (true)
                {
                    // BÜTÇE DENETİMİ ÖGE BAŞINA: klasör başına bakmak yetmez, çünkü TEK bir klasör
                    // milyonlarca dosya içerebilir — bütçe o klasörün ortasında da tutmak zorunda.
                    if (butce.KalanOge <= 0)
                    {
                        return Kirp(toplamBayt, dosyaSayisi, klasorSayisi, SebepOgeButcesi);
                    }

                    if (butce.SureDoldu)
                    {
                        return Kirp(toplamBayt, dosyaSayisi, klasorSayisi, SebepSureButcesi);
                    }

                    FileSystemInfo oge;
                    try
                    {
                        if (!gezgin.MoveNext())
                        {
                            break;
                        }

                        oge = gezgin.Current;
                    }
                    catch (UnauthorizedAccessException exception)
                    {
                        // Sıralama TEMBELDİR: erişim reddi klasörü açarken değil, ilerlerken de
                        // gelebilir. Yalnız açılışı sarmalamak bu yolu korumasız bırakırdı.
                        erisimReddedildi = true;
                        klasorOkunamadi?.Invoke(klasor, exception);
                        break;
                    }
                    catch (DirectoryNotFoundException)
                    {
                        break;
                    }
                    catch (IOException exception)
                    {
                        // Açılıştaki ile AYNI gerekçe (yukarıda yazılı); sıralama tembel olduğu için
                        // aynı hata burada da gelebilir ve yalnız birini sarmalamak, korumanın
                        // yarısını yazmak olurdu.
                        erisimReddedildi = true;
                        klasorOkunamadi?.Invoke(klasor, exception);
                        break;
                    }

                    butce.KalanOge--;

                    // BAĞLANTI (junction/symlink) TAKİP EDİLMEZ — gerekçesi
                    // FileService.KlasoruKopyala'da yazılı ve burada aynen geçerlidir: takip etmek
                    // ya sanal alan DIŞINI toplama katmak (kiracıya komşusunun disk kullanımını
                    // ölçtürmek, kotasında komşusunun baytlarını saydırmak) ya da kendi üst ağacını
                    // gösteren bir bağlantıda SONSUZ döngü demektir.
                    //
                    // Atlanan bağlantı Truncated ÜRETMEZ: bağlantılar panelin dosya görüşünde
                    // hiçbir yerde YOKTUR (listeleme, arama, kopyalama). Kullanıcının hiç görmediği
                    // bir ögenin toplama girmemesi eksiklik değil, listeyle TUTARLILIKTIR — aksi
                    // hâlde her bağlantı içeren klasör kalıcı olarak "eksik" damgası yerdi.
                    if (oge.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        // Haber verilir ama ÖGE VERİLMEZ (gerekçe parametre belgesinde): kopyalama
                        // yolu "kopya EKSİK" diyebilsin, ama atlananı yeniden değerlendiremesin.
                        baglantiAtlandiginda?.Invoke();
                        continue;
                    }

                    if (oge is DirectoryInfo altKlasor)
                    {
                        klasorSayisi++;

                        // KLASÖR, İÇİNDEKİLERDEN ÖNCE bildirilir (itmeden önce): içe aktarma
                        // kopyası hedefteki klasörü, içine ilk dosya yazılmadan oluşturabilsin.
                        ogeGorulunce?.Invoke(altKlasor);
                        yigin.Push(altKlasor.FullName);
                    }
                    else if (oge is FileInfo dosya)
                    {
                        dosyaSayisi++;
                        ogeGorulunce?.Invoke(dosya);

                        // Uzunluk, sıralamanın kendi meta verisinden gelir (ek disk erişimi yok);
                        // yine de dosya bu arada silinmiş olabilir — o zaman 0 sayılır, çünkü
                        // artık diskte yeri yoktur.
                        try
                        {
                            toplamBayt += dosya.Length;
                        }
                        catch (FileNotFoundException)
                        {
                        }
                        catch (IOException)
                        {
                        }
                    }
                }
            }
        }

        if (erisimReddedildi)
        {
            // ERİŞİM REDDİ NEDEN AYRI BİR ALAN DEĞİL DE Truncated: kullanıcı için tek bir soru var —
            // "bu sayıya güvenebilir miyim?" Cevap ikisinde de aynı: HAYIR, bu bir ALT SINIR.
            // Sözleşme dışında yeni bir alan uydurmak, arayüzün o alanı hiç okumaması yüzünden
            // atlananın SESSİZ kalması demekti; hâlbuki bu depoda eksik bir sonucu tam göstermek
            // yasak. Sebep anahtarı ayrıdır, yani "neden" bilgisi kaybolmaz.
            return Kirp(toplamBayt, dosyaSayisi, klasorSayisi, SebepErisimReddi);
        }

        return new SafeDirectoryWalkResult(toplamBayt, dosyaSayisi, klasorSayisi, false, string.Empty);
    }

    /// <summary>Kısmi sonucu "kısmi" olarak DAMGALAR; istisna atmaz.</summary>
    /// <remarks>
    /// <para>
    /// Bütçe dolduğunda istisna atmak, çağırana hiçbir şey vermemek olurdu — oysa bütçe kadarlık bir
    /// ALT SINIR bile "bu klasör en az şu kadar yer kaplıyor" sorusunu cevaplar. Asıl kural şudur:
    /// kısmi sonuç kısmi olduğunu SÖYLER.
    /// </para>
    /// <para>
    /// Bütçe sebebi, erişim reddi bayrağının ÜSTÜNE yazar (bu metot yürüyüşü sonlandıran yolda
    /// çağrılır): ikisi de doğruyken söylenmesi gereken, hesabı DURDURAN sebeptir — "bir klasör
    /// okunamadı" demek, aslında ağacın büyük kısmına hiç bakılmamışken yanıltıcıdır.
    /// </para>
    /// </remarks>
    private static SafeDirectoryWalkResult Kirp(long toplamBayt, int dosyaSayisi, int klasorSayisi, string sebepAnahtari)
        => new(toplamBayt, dosyaSayisi, klasorSayisi, true, sebepAnahtari);
}

/// <summary>
/// Bir yürüyüşün (ya da PAYLAŞILAN bir bütçeyle birden çok yürüyüşün) ölçtükleri.
/// </summary>
/// <remarks>
/// <see cref="Truncated"/> <c>true</c> ise sayıların hepsi ALT SINIRDIR, gerçek değer değildir.
/// Bu ayrımın neden sözleşmenin parçası olduğu <c>DirectorySizeDto</c>'da yazılıdır; kota yolunun
/// bu bayrakla ne yaptığı ise <c>CustomerScopeService.GetDiskUsageMbAsync</c>'te.
/// </remarks>
internal readonly record struct SafeDirectoryWalkResult(
    long TotalBytes,
    int FileCount,
    int DirectoryCount,
    bool Truncated,
    string TruncatedReasonKey);

/// <summary>
/// Yürüyüşün harcayabileceği öge ve süre. <b>Nesne PAYLAŞILABİLİR</b> — bu, tipin varlık sebebidir.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden bütçe bir NESNE, iki <c>int</c> parametresi değil:</b> kota hesabı bir aboneliğin
/// BÜTÜN sitelerini gezer. Bütçe her siteye ayrı verilseydi, tavan site sayısıyla ÇARPILIRDI —
/// yani 20 siteli bir abonelik 20 kat daha uzun bir isteğe hakkı olurdu ve bütçe fiilen kalkardı.
/// Tek nesne, "bu İSTEK en çok şu kadar iş yapar" demenin tek yoludur.
/// </para>
/// <para>
/// <b>Süre neden nesne oluşturulurken başlar:</b> ölçülmesi gereken, çağıranın işe başladığı andan
/// itibaren geçen süredir. Her yürüyüşte sıfırlanan bir saat, aynı çarpma hatasını süre tarafında
/// tekrarlardı.
/// </para>
/// </remarks>
internal sealed class SafeDirectoryWalkBudget(int ogeButcesi, int sureButcesiMs)
{
    private readonly Stopwatch saat = Stopwatch.StartNew();

    /// <summary>Kalan öge hakkı; yürüyüş her ögede birer azaltır.</summary>
    internal int KalanOge { get; set; } = ogeButcesi;

    /// <summary>Süre bütçesi doldu mu?</summary>
    internal bool SureDoldu => saat.ElapsedMilliseconds >= sureButcesiMs;
}
