using System.Diagnostics;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Infrastructure.Iis;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// YARIM KALAN SİLME, GERÇEĞİN TERSİNİ SÖYLEMEZ (denetim Y7) + SAHİPLİK KAYDI (Y1).
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLEN:</b> içinde kavşak bulunan gerçek bir site ağacında
/// <c>Directory.Delete(recursive: true)</c> istisna attı, <b>ama <c>index.php</c>,
/// <c>uploads\photo.jpg</c> ve <c>wwwroot\a.php</c> silinmişti</b>; yalnız boş <c>wwwroot</c> kaldı.
/// Panel bu duruma "Site dosyaları silinemedi" diyordu. Kullanıcı dosyalarının durduğunu sanıyor,
/// oysa neredeyse hepsi gitmişti ve IIS sitesi bir adım önce zaten silinmişti.
/// </para>
/// <para>
/// Aşağıdaki ilk test bunu GERÇEK dosyalarla, gerçek bir kilitle üretir: silme istisna atar,
/// dosyaların bir kısmı gider ve sınıflandırma "kısmen silindi" der.
/// </para>
/// </remarks>
public sealed class SiteDeletionHonestyTests
{
    [Fact]
    public void APartialDeletion_IsReportedAsPartial_NotAsFailure()
    {
        var kok = GeciciKlasor();
        try
        {
            Directory.CreateDirectory(Path.Combine(kok, "uploads"));
            Directory.CreateDirectory(Path.Combine(kok, "zkilit"));
            File.WriteAllText(Path.Combine(kok, "index.php"), "<?php");
            File.WriteAllText(Path.Combine(kok, "uploads", "photo.jpg"), "resim");
            File.WriteAllText(Path.Combine(kok, "uploads", "b.jpg"), "resim2");
            var kilitliDosya = Path.Combine(kok, "zkilit", "acik.log");
            File.WriteAllText(kilitliDosya, "canli");

            var oncesi = SiteCascadeDeletionService.CountFilesSafely(kok);
            Assert.Equal(4, oncesi.FileCount);
            Assert.True(oncesi.Measured);

            // Dosyayı BAŞKASI tutuyor (IIS'in yazmakta olduğu günlük gibi).
            using var tutucu = new FileStream(kilitliDosya, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            Assert.ThrowsAny<Exception>(() => Directory.Delete(kok, recursive: true));

            var duruyor = Directory.Exists(kok);
            var kalan = duruyor
                ? SiteCascadeDeletionService.CountFilesSafely(kok)
                : new SiteCascadeDeletionService.FileCountMeasurement(0, true, string.Empty);

            // ÖLÇÜM: silme yarıda kaldı ama dosyaların bir kısmı GERÇEKTEN gitti.
            Assert.True(duruyor);
            // ÖLÇÜLDÜ (bu makinede): 4 dosyadan 3'ü silindi, 1 kilitli dosya kaldı. Kesin sayıya
            // BAĞLANMIYORUZ (silme sırası dosya sistemi sıralamasına bağlıdır); iddia şudur:
            // silme "başarısız" oldu AMA dosyaların bir kısmı gerçekten gitti.
            Assert.True(
                kalan.FileCount < oncesi.FileCount,
                $"Bu makinede kısmi silme üretilemedi ({oncesi.FileCount} -> {kalan.FileCount}); testin dayanağı ölçüm.");

            var sonuc = SiteCascadeDeletionService.ClassifyDeletion(oncesi, kalan, duruyor);
            Assert.Equal(SiteCascadeDeletionService.DirectoryDeletionOutcome.PartiallyDeleted, sonuc);

            var mesaj = SiteCascadeDeletionService.DescribeDeletionFailure(
                "Site dosyaları", kok, sonuc, oncesi, kalan, "kilitli dosya");

            // Kullanıcının okuyacağı cümle GERÇEĞİ söylemeli.
            Assert.Contains("KISMEN SİLİNDİ", mesaj, StringComparison.Ordinal);
            Assert.Contains("GERİ GETİRİLEMEZ", mesaj, StringComparison.Ordinal);
            Assert.DoesNotContain("silinemedi, dosyalar duruyor", mesaj, StringComparison.Ordinal);
        }
        finally
        {
            TemizleyeSil(kok);
        }
    }

    /// <summary>Hiçbir şey silinmediyse "kısmen silindi" demek de yalan olurdu.</summary>
    [Fact]
    public void WhenNothingWasDeleted_TheMessageSaysSo()
    {
        var sonuc = SiteCascadeDeletionService.ClassifyDeletion(Tam(5), Tam(5), klasorDuruyor: true);
        Assert.Equal(SiteCascadeDeletionService.DirectoryDeletionOutcome.NotDeleted, sonuc);

        var mesaj = SiteCascadeDeletionService.DescribeDeletionFailure(
            "Site dosyaları", @"D:\web\musteri.com", sonuc, Tam(5), Tam(5), "erişim reddedildi");

        Assert.Contains("silinemedi, dosyalar duruyor", mesaj, StringComparison.Ordinal);
        Assert.DoesNotContain("KISMEN", mesaj, StringComparison.Ordinal);
    }

    /// <summary>Klasör gitmişse istisna gelse bile sonuç "silindi"dir (silme başka bir turda tamamlanmış olabilir).</summary>
    [Fact]
    public void WhenTheFolderIsGone_TheOutcomeIsDeleted()
    {
        Assert.Equal(
            SiteCascadeDeletionService.DirectoryDeletionOutcome.Deleted,
            SiteCascadeDeletionService.ClassifyDeletion(Tam(3), Tam(0), klasorDuruyor: false));

        // KESİK ÖLÇÜM DE BUNU DEĞİŞTİRMEZ: klasörün yokluğu bir GÖZLEMDİR, sayıma bağlı değil.
        // Aksi hâlde büyük ağaçlarda başarıyla silinmiş bir site "ölçemedim" diye raporlanırdı.
        Assert.Equal(
            SiteCascadeDeletionService.DirectoryDeletionOutcome.Deleted,
            SiteCascadeDeletionService.ClassifyDeletion(Kesik(200_000), Tam(0), klasorDuruyor: false));
    }

    /// <summary>
    /// SAYIM KAVŞAĞI TAKİP ETMEZ. Denetimde (Y6) ölçüldü: özyinelemeli sayım kavşağı takip
    /// ediyordu. Sayım da bir okuma yoludur ve "kısmen silindi" kararı ona dayanır; kavşağın
    /// arkasındaki müşteri dosyalarını saymak, kararı da yanlış kurardı.
    /// </summary>
    [Fact]
    public void TheFileCount_DoesNotFollowAJunction()
    {
        var kok = GeciciKlasor();
        var hedef = GeciciKlasor();
        try
        {
            Directory.CreateDirectory(kok);
            Directory.CreateDirectory(hedef);
            File.WriteAllText(Path.Combine(kok, "kendi.txt"), "site");
            File.WriteAllText(Path.Combine(hedef, "musteri1.txt"), "musteri");
            File.WriteAllText(Path.Combine(hedef, "musteri2.txt"), "musteri");

            JunctionOlustur(Path.Combine(kok, "baglanti"), hedef);
            Assert.True(Directory.Exists(Path.Combine(kok, "baglanti")), "Junction olusturulamadi.");

            // Kavşağın arkasındaki 2 dosya sayıya GİRMEZ.
            Assert.Equal(1, SiteCascadeDeletionService.CountFilesSafely(kok).FileCount);
        }
        finally
        {
            TemizleyeSil(Path.Combine(kok, "baglanti"));
            TemizleyeSil(kok);
            TemizleyeSil(hedef);
        }
    }

    /// <summary>
    /// SAYIM TAVANDA DURUR — ve tavan bolken GERÇEK sayıyı verir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Yürüyüş ortak kapıya (<c>SafeDirectoryWalk</c>) taşındıktan sonra ölçülen şey, tavanın
    /// TAŞINDIĞI: eski gövdenin kendi <c>limit</c> sayacı vardı, yenisininki ortak bütçe nesnesi.
    /// Tavan sessizce düşerse silme yolu, milyonlarca dosyalı bir ağaçta iki kez baştan sona
    /// gezmeye başlar.
    /// </para>
    /// <para>
    /// İkinci iddia MUTASYON KONTROLÜDÜR: yalnız birinciye bakan bir test, "her zaman 3 dön" gibi
    /// bir uygulamayı da yeşil gösterirdi. Sayının bir tavan değil ÖLÇÜM olduğu ancak bolken
    /// gerçeği söylediğiyle kanıtlanır — ve "kısmen silindi" kararı tam olarak bu ölçüme dayanır.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheFileCount_StopsAtItsBudget_ButCountsForRealWhenTheBudgetIsAmple()
    {
        var kok = GeciciKlasor();
        try
        {
            for (var sira = 0; sira < 10; sira++)
            {
                File.WriteAllText(Path.Combine(kok, $"dosya-{sira:00}.txt"), "x");
            }

            var kesik = SiteCascadeDeletionService.CountFilesSafely(kok, ogeButcesi: 3);
            Assert.Equal(3, kesik.FileCount);
            Assert.Equal(10, SiteCascadeDeletionService.CountFilesSafely(kok).FileCount);

            // SAYININ YANINDA "BU SAYI TAM MI" DA TAŞINIYOR. Bilgi ortak yürüyüşte zaten üretiliyordu
            // (Truncated + sebep anahtarı); düz `int` dönüşü onu tam burada düşürüyor ve sınıflandırma
            // iki alt sınırı kesin ölçüm sanıyordu.
            Assert.False(kesik.Measured);
            Assert.Equal("files.size_truncated_entries", kesik.TruncatedReasonKey);
            Assert.True(SiteCascadeDeletionService.CountFilesSafely(kok).Measured);
        }
        finally
        {
            TemizleyeSil(kok);
        }
    }

    // ---- ÖLÇÜM KESİLDİĞİNDE: üçüncü durum ----

    /// <summary>
    /// İKİ SAYIM DA TAVANA DAYANIRSA PANEL KESİN KONUŞMAZ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Kapatılan yalan:</b> sınıflandırma iki ölçümün FARKINA bakar. İkisi de öge bütçesine
    /// takılırsa fark SIFIR çıkar ve eski kural bunu "hiç silinemedi" sayardı — panel, ağacın çoğu
    /// gitmiş olabilecekken "silinemedi, dosyalar duruyor" diyordu. Tavanın 20.000'den 200.000'e
    /// çıkarılması yalnız olasılığı düşürdü; sınıfı kapatan şey üçüncü durumdur.
    /// </para>
    /// <para>
    /// Test bunu GERÇEK dosyalarla ve GERÇEK bir tavanla üretir (bütçe parametresi tam bunun için
    /// var): 10 dosyalık bir ağaç, 3 ögelik bütçeyle iki kez sayılır — iki sayı da 3'tür, farkları
    /// sıfırdır ve İKİSİ DE alt sınırdır.
    /// </para>
    /// </remarks>
    [Fact]
    public void WhenBothCountsHitTheCeiling_ThePanelSaysItCouldNotMeasure_InsteadOfClaimingFailure()
    {
        var kok = GeciciKlasor();
        try
        {
            for (var sira = 0; sira < 10; sira++)
            {
                File.WriteAllText(Path.Combine(kok, $"dosya-{sira:00}.txt"), "x");
            }

            var oncesi = SiteCascadeDeletionService.CountFilesSafely(kok, ogeButcesi: 3);
            var kalan = SiteCascadeDeletionService.CountFilesSafely(kok, ogeButcesi: 3);

            // ESKİ KURALIN GİRDİSİ: fark sıfır. Eskiden buradan doğrudan "silinemedi" çıkıyordu.
            Assert.Equal(oncesi.FileCount, kalan.FileCount);
            Assert.False(oncesi.Measured);
            Assert.False(kalan.Measured);

            var sonuc = SiteCascadeDeletionService.ClassifyDeletion(oncesi, kalan, klasorDuruyor: true);
            Assert.Equal(SiteCascadeDeletionService.DirectoryDeletionOutcome.MeasurementIncomplete, sonuc);

            var mesaj = SiteCascadeDeletionService.DescribeDeletionFailure(
                "Site dosyaları", kok, sonuc, oncesi, kalan, "kilitli dosya");

            // KESİN CÜMLE KURULMUYOR: ne "silinemedi" ne de "kısmen silindi".
            Assert.DoesNotContain("silinemedi, dosyalar duruyor", mesaj, StringComparison.Ordinal);
            Assert.DoesNotContain("KISMEN SİLİNDİ", mesaj, StringComparison.Ordinal);

            // ...ve kullanıcı boşlukta bırakılmıyor: ne bilinmediği + NE YAPMASI gerektiği yazılı.
            Assert.Contains("ÖLÇÜLEMEDİ", mesaj, StringComparison.Ordinal);
            Assert.Contains("elle açıp içeriğini kontrol edin", mesaj, StringComparison.Ordinal);

            // Sayılar ALT SINIR olarak basılır; çıplak sayı olmayan bir kesinlik iddia ederdi.
            Assert.Contains(">=3", mesaj, StringComparison.Ordinal);

            // Teşhis satırı ETİKET-DEĞER: sebep bir ÇEVİRİ ANAHTARIDIR ve bu liste arayüzde
            // olduğu gibi basılır; anahtarı Türkçe anlatının içine gömmek ham anahtar göstermek olurdu.
            Assert.Contains("measurement=truncated", mesaj, StringComparison.Ordinal);
            Assert.Contains("reason_key=files.size_truncated_entries", mesaj, StringComparison.Ordinal);
        }
        finally
        {
            TemizleyeSil(kok);
        }
    }

    /// <summary>
    /// ÜÇÜNCÜ DURUM, GERÇEKTEN BİLİNENİ "BİLMİYORUM"A ÇEVİRMEZ.
    /// </summary>
    /// <remarks>
    /// Önceki sayım kesikse gerçek önceki sayı EN AZ o kadardır; kalan sayım TAMSA kesindir. Kalan,
    /// kesik öncekinden küçükse gerçek fark kesin pozitiftir — kısmi silme KANITLANMIŞTIR. Bu, tavanı
    /// aşan büyük ağaçlarda en sık görülen hâldir (silme neredeyse her şeyi götürür, kilitli birkaç
    /// dosya kalır); orada "ölçemedim" demek gereksiz bir bilgi kaybı olurdu.
    /// </remarks>
    [Fact]
    public void ATruncatedBeforeCount_StillProvesAPartialDeletion_WhenTheRemainderWasCountedExactly()
    {
        var kok = GeciciKlasor();
        try
        {
            Directory.CreateDirectory(Path.Combine(kok, "zkilit"));
            for (var sira = 0; sira < 10; sira++)
            {
                File.WriteAllText(Path.Combine(kok, $"dosya-{sira:00}.txt"), "x");
            }

            var kilitliDosya = Path.Combine(kok, "zkilit", "acik.log");
            File.WriteAllText(kilitliDosya, "canli");

            // Ön sayım TAVANA DAYANIR (gerçek ağaç 11 dosya, bütçe 3).
            var oncesi = SiteCascadeDeletionService.CountFilesSafely(kok, ogeButcesi: 3);
            Assert.False(oncesi.Measured);

            using var tutucu = new FileStream(kilitliDosya, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.ThrowsAny<Exception>(() => Directory.Delete(kok, recursive: true));

            Assert.True(Directory.Exists(kok));
            var kalan = SiteCascadeDeletionService.CountFilesSafely(kok);

            // Kalan sayım TAMDIR ve kesik ön sayıdan küçüktür → fark KESİN pozitif.
            Assert.True(kalan.Measured);
            Assert.True(
                kalan.FileCount < oncesi.FileCount,
                $"Bu makinede kanıtlanabilir kısmi silme üretilemedi (>={oncesi.FileCount} -> {kalan.FileCount}).");

            var sonuc = SiteCascadeDeletionService.ClassifyDeletion(oncesi, kalan, klasorDuruyor: true);
            Assert.Equal(SiteCascadeDeletionService.DirectoryDeletionOutcome.PartiallyDeleted, sonuc);

            // KANITLANAN ŞEY "kısmi silme"dir, SAYILAR DEĞİL: ön sayım kesik olduğu için hem toplam
            // hem de "kaç tanesi silindi" birer ALT SINIRDIR ve öyle basılır. Çıplak basmak,
            // kaldırdığımız kesinlik iddiasını aynı cümlenin ortasında tekrarlamak olurdu.
            var mesaj = SiteCascadeDeletionService.DescribeDeletionFailure(
                "Site dosyaları", kok, sonuc, oncesi, kalan, "kilitli dosya");

            Assert.Contains("KISMEN SİLİNDİ", mesaj, StringComparison.Ordinal);
            Assert.Contains($">={oncesi.FileCount} dosyadan", mesaj, StringComparison.Ordinal);
            Assert.Contains($">={oncesi.FileCount - kalan.FileCount} tanesi", mesaj, StringComparison.Ordinal);
        }
        finally
        {
            TemizleyeSil(kok);
        }
    }

    /// <summary>
    /// TERS YÖN KANIT DEĞİLDİR: önceki tam, kalan kesikse "kısmen silindi" demek yine yalan olabilir
    /// (gerçek kalan, öncekine eşit olabilir). O hâl bilerek üçüncü duruma düşer.
    /// </summary>
    [Fact]
    public void ATruncatedRemainder_DoesNotProveAnything()
    {
        Assert.Equal(
            SiteCascadeDeletionService.DirectoryDeletionOutcome.MeasurementIncomplete,
            SiteCascadeDeletionService.ClassifyDeletion(Tam(500), Kesik(120), klasorDuruyor: true));
    }

    /// <summary>
    /// ÖLÇÜLEMEYEN SONUÇ, SONUÇ NESNESİNDE DE BAŞARI/BAŞARISIZLIĞA YUVARLANMIYOR.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bayrağı kurup sonuç nesnesine yazmamak (ya da yazıp arayüze bağlamamak) bu depoda tekrar eden
    /// bir hata sınıfı; bu yüzden yazma kararı ayrı ve saf bir yerde duruyor ve burada ölçülüyor.
    /// </para>
    /// <para>
    /// Kalıntının KENDİSİ yuvarlama değildir: klasörün durduğu <c>Directory.Exists</c> ile
    /// GÖZLENMİŞTİR; belirsiz olan yalnızca içeriğinin ne kadarının gittiğidir.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnUnmeasuredResult_IsRecordedAsUnmeasured_InTheResultObject()
    {
        var sonuc = new SiteDeletionResultDto { SiteName = "musteri.com" };

        SiteCascadeDeletionService.KlasorSonucunuYaz(
            sonuc,
            "Site dosyaları",
            @"D:\web\musteri.com",
            SiteCascadeDeletionService.DirectoryDeletionOutcome.MeasurementIncomplete,
            Kesik(200_000),
            Kesik(200_000),
            "kilitli dosya");

        // 1) Makine tarafından okunabilir bayrak: arayüz "temizlenemedi" başlığını KURAMAZ.
        Assert.False(sonuc.FilesDeletionMeasured);
        Assert.Equal("files.size_truncated_entries", sonuc.FilesDeletionTruncatedReasonKey);

        // 2) Klasör duruyor → kalıntı yazılır, ama sebebi "silinemedi" DEMEZ ve bilinçli sayılmaz.
        var kalinti = Assert.Single(sonuc.Leftovers);
        Assert.Equal("files", kalinti.Kind);
        Assert.Equal(@"D:\web\musteri.com", kalinti.Name);
        Assert.False(kalinti.Deliberate);
        Assert.Contains("ÖLÇÜLEMEDİ", kalinti.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("silinemedi", kalinti.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("KISMEN", kalinti.Reason, StringComparison.Ordinal);

        // 3) Sonuç "tamamen silindi" diye de yuvarlanmaz.
        Assert.False(sonuc.FullyDeleted);
    }

    /// <summary>
    /// ...ve ÖLÇÜLEBİLEN durumlarda bayrak KİRLENMEZ: "ölçemedim" her arızaya yapıştırılan bir
    /// kaçamak olsaydı, gerçekten ölçülmüş bir başarısızlık da belirsiz görünürdü.
    /// </summary>
    [Fact]
    public void AMeasuredFailure_LeavesTheMeasurementFlagIntact()
    {
        var sonuc = new SiteDeletionResultDto { SiteName = "musteri.com" };

        SiteCascadeDeletionService.KlasorSonucunuYaz(
            sonuc,
            "Site dosyaları",
            @"D:\web\musteri.com",
            SiteCascadeDeletionService.DirectoryDeletionOutcome.NotDeleted,
            Tam(5),
            Tam(5),
            "erişim reddedildi");

        Assert.True(sonuc.FilesDeletionMeasured);
        Assert.Empty(sonuc.FilesDeletionTruncatedReasonKey);
        Assert.Contains("silinemedi, dosyalar duruyor", sonuc.Warnings.Single(), StringComparison.Ordinal);
    }

    /// <summary>
    /// ARKA UÇTA ALAN AÇIP ARAYÜZE BAĞLAMAMAK ("yazılmış ama bağlanmamış") bu depoda tekrar eden bir
    /// hata sınıfı: üçüncü durum, silmenin sonucunu gösteren HER İKİ ekranda da çiziliyor mu?
    /// </summary>
    /// <remarks>
    /// Aranan dizeler KOD parçalarıdır ve bu ekranların yorumlarında GEÇMEZ (doğrulandı) — bir
    /// mutasyon turunda tam olarak bu tuzağa düşüldü: kaynağa bakan bir test, kodun değil YORUMUN
    /// içindeki kelimeye tutunduğu için koruma bozulduğu hâlde yeşil kaldı.
    /// </remarks>
    [Theory]
    [InlineData("SitesView.vue")]
    [InlineData("WebsiteWorkspaceView.vue")]
    public void TheDeletionScreens_DrawTheUnmeasuredCase(string ekran)
    {
        var kaynak = OkuKaynak("frontend", "src", "views", ekran);

        Assert.Contains("result.filesDeletionMeasured === false", kaynak, StringComparison.Ordinal);
        Assert.Contains("panel.sites.deletedFilesUnmeasured", kaynak, StringComparison.Ordinal);
    }

    /// <summary>Yeni anahtar İKİ DİLDE de var (biri unutulursa panel ham anahtar basar).</summary>
    [Fact]
    public void TheUnmeasuredHeadline_ExistsInBothDictionaries()
    {
        foreach (var dosya in new[] { "tr.json", "en.json" })
        {
            var sozluk = OkuKaynak("frontend", "src", "i18n", dosya);
            Assert.Contains("\"panel.sites.deletedFilesUnmeasured\"", sozluk, StringComparison.Ordinal);
        }
    }

    // ---- Y1: veritabanı sahiplik kaydı ----

    /// <summary>
    /// KABLOLAMA: sahiplik kaydını KOŞULSUZ silen satır gitmeli. Aksi hâlde "veritabanımı koru"
    /// seçeneği, veritabanını motorda bırakıp panelde SAHİPSİZ yapan bir tuzak olurdu — ve sahipsiz
    /// bir veritabanına panelden sahip atayan hiçbir uç yok.
    /// </summary>
    [Fact]
    public void TheController_NoLongerDropsDatabaseOwnershipUnconditionally()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.Api", "Controllers", "SitesController.cs");
        var silmeUcu = kaynak[kaynak.IndexOf("public async Task<ActionResult<ApiResponse<SiteDeletionResultDto>>> DeleteSiteAsync", StringComparison.Ordinal)..];
        silmeUcu = silmeUcu[..silmeUcu.IndexOf("[HttpPost(\"{siteName}/start\")]", StringComparison.Ordinal)];

        // ÇAĞRI aranıyor, adı geçen yorum değil: gerekçe yorumu bilerek duruyor ve o metin
        // testi düşürmemeli (kırılgan metin denetiminin klasik tuzağı).
        Assert.DoesNotContain("await dbOwnership.RemoveBySiteAsync", silmeUcu, StringComparison.Ordinal);
    }

    /// <summary>...ve karar, GERÇEĞİ bilen yerde veriliyor: kayıt yalnız DROP başarılıysa silinir.</summary>
    [Fact]
    public void TheCascade_RemovesTheOwnershipRecordOnlyWhenTheDatabaseWasReallyDropped()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.Infrastructure", "Iis", "SiteCascadeDeletionService.cs");

        // Silme adımının SONUCU okunuyor (StepAsync artık bool döner).
        //
        // İddia adımın TAM ARGÜMAN LİSTESİNE bağlanmıyor: `StepAsync` sonradan kalıntı tipi/adı
        // da alıyor ve o eklemeyi "kırılma" saymak, testi gerçekliğin değil metnin bekçisi yapardı.
        // Provider callback keeps the authority check and post-DROP ownership removal in one lease.
        Assert.Contains("if (await StepAsync(", kaynak, StringComparison.Ordinal);
        Assert.Contains("DeleteSiteDatabaseAsync(siteName, engine, name, cancellationToken)", kaynak, StringComparison.Ordinal);
        Assert.Contains("token => databaseOwnership.RemoveAsync(engine, databaseName, token)", kaynak, StringComparison.Ordinal);

        // Hayatta kalan veritabanının sahipliği KORUNUYOR, yalnız ölü site adı koparılıyor.
        Assert.Contains("DetachSurvivingDatabasesAsync", kaynak, StringComparison.Ordinal);
        Assert.Contains("siteName: null", kaynak, StringComparison.Ordinal);
    }

    // ---- Y7 (ikinci yarı): kullanıcı silmede cascade uyarıları ----

    /// <summary>
    /// Kullanıcı silme, cascade'in uyarılarını ARTIK ATMIYOR. Eskiden sonuç nesnesi hiç okunmuyor,
    /// kullanıcı satırı siliniyor ve 200 dönülüyordu: yarım silinmiş site sahipsiz kalıyor ve
    /// sunucuda ne kaldığını kimse öğrenemiyordu.
    /// </summary>
    [Fact]
    public void DeletingAUser_RecordsWhatTheCascadeLeftBehind()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.Infrastructure", "Services", "UserManagementService.cs");

        Assert.Contains("var sonuc = await siteCascadeDeletion.DeleteAsync(", kaynak, StringComparison.Ordinal);
        Assert.Contains("foreach (var uyari in sonuc.Warnings)", kaynak, StringComparison.Ordinal);
        Assert.Contains("RecordCascadeLeftoversAsync", kaynak, StringComparison.Ordinal);
        Assert.Contains("users.delete.site_leftover", kaynak, StringComparison.Ordinal);
    }

    // ---- Yardımcılar ----

    /// <summary>Ağacın tamamı görülmüş bir sayım.</summary>
    private static SiteCascadeDeletionService.FileCountMeasurement Tam(int dosya)
        => new(dosya, true, string.Empty);

    /// <summary>Öge bütçesinde kesilmiş sayım: değer bir ALT SINIRDIR.</summary>
    private static SiteCascadeDeletionService.FileCountMeasurement Kesik(int altSinir)
        => new(altSinir, false, "files.size_truncated_entries");

    private static string GeciciKlasor()
    {
        var yol = Path.Combine(Path.GetTempPath(), $"ada-sil-{Guid.NewGuid():N}");
        Directory.CreateDirectory(yol);
        return yol;
    }

    private static void TemizleyeSil(string yol)
    {
        try
        {
            if (Directory.Exists(yol))
            {
                // Bağlantı ise HEDEFİ değil yalnız bağlantıyı kaldır.
                var bilgi = new DirectoryInfo(yol);
                if (bilgi.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    Directory.Delete(yol);
                    return;
                }

                Directory.Delete(yol, recursive: true);
            }
        }
        catch
        {
            // Temizlik en iyi çaba.
        }
    }

    private static void JunctionOlustur(string link, string hedef)
    {
        var betik = Path.Combine(Path.GetTempPath(), $"ada-junction-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(
            betik,
            $"$ErrorActionPreference='Stop'\nNew-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{hedef.Replace("'", "''")}' | Out-Null");
        try
        {
            using var surec = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{betik}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            surec.WaitForExit(60_000);
        }
        finally
        {
            try { File.Delete(betik); } catch { /* en iyi çaba */ }
        }
    }

    private static string OkuKaynak(params string[] parcalar)
    {
        var klasor = AppContext.BaseDirectory;
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            var aday = Path.Combine(new[] { klasor }.Concat(parcalar).ToArray());
            if (File.Exists(aday))
            {
                return File.ReadAllText(aday);
            }

            klasor = Path.Combine(klasor, "..");
        }

        throw new FileNotFoundException(string.Join('/', parcalar));
    }
}
