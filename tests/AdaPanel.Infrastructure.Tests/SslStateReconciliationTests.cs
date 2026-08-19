using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Infrastructure.Ssl;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// SSL kaydi ALMA ANINDA yazilip bir daha hic dogrulanmiyordu.
/// <para>
/// Iki yonlu yanlis sonuc: (1) win-acme sertifikayi sorunsuz yeniledigi hâlde kayittaki eski
/// bitis tarihi yerinde kaliyor, panel HER GUN "sertifikanizin suresi doldu" e-postasi atiyordu
/// -- kullanici bir sure sonra butun SSL uyarilarina bakmaz oluyor (uyari korlugu); (2) sertifika
/// depodan silindiginde kayit "gecerli, 60 gun kaldi" demeye devam ediyordu.
/// </para>
/// <para>
/// Ayni disiplin depoda ZATEN vardi: <c>MailService.SyncCertificateAsync</c> PEM'i diskten geri
/// okuyup icerik ozeti degistiyse <c>NotAfterUtc</c>'yi tazeliyor. Site SSL'i bunun disinda
/// kalmisti.
/// </para>
/// </summary>
public sealed class SslStateReconciliationTests
{
    private static readonly DateTime Simdi = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// YENILENMIS sertifika: ayni konu adi, YENI parmak izi, YENI bitis. Kayit gercege gore
    /// tazelenmeli -- bu satirin yoklugu gunluk yanlis alarmin kok nedeniydi.
    /// </summary>
    [Fact]
    public void ARenewedCertificate_RefreshesTheRecordedExpiry()
    {
        var kayit = Kayit("site", "ornek.com", eskiBitis: Simdi.AddDays(-3), thumbprint: "ESKI");

        var sonuc = SslStateReconciler.Reconcile(
            [kayit],
            _ => SslStoreProbe.Found("YENI", Simdi.AddDays(87), "letsencrypt"),
            Simdi);

        Assert.Equal(Simdi.AddDays(87), kayit.NotAfterUtc);
        Assert.Equal("YENI", kayit.Thumbprint);
        Assert.Equal("letsencrypt", kayit.Source);
        Assert.True(kayit.IsPresentInStore);
        Assert.Equal(Simdi, kayit.LastVerifiedUtc);

        Assert.True(sonuc.ShouldPersist);
        Assert.Equal(1, sonuc.Verified);
    }

    /// <summary>
    /// Depodan SILINMIS sertifika: kayit silinmez ama "depoda yok" damgasi yer. Panel bir daha
    /// "gecerli, 60 gun kaldi" diyemez.
    /// </summary>
    [Fact]
    public void ACertificateMissingFromTheStore_IsMarkedMissing_ButTheRecordSurvives()
    {
        var kayit = Kayit("site", "ornek.com", eskiBitis: Simdi.AddDays(60), thumbprint: "ESKI");

        var sonuc = SslStateReconciler.Reconcile([kayit], _ => SslStoreProbe.NotFound(), Simdi);

        Assert.False(kayit.IsPresentInStore);
        Assert.Equal(Simdi, kayit.LastVerifiedUtc);

        // Kayit SILINMEZ: silmek "hic sertifika yoktu" demek olurdu; dogrusu, karsiligi olmayan
        // bir kaydin varligini YUZEYE CIKARMAK.
        Assert.Equal("ornek.com", kayit.HostName);

        // Uydurulmus bir bitis tarihi YAZILMAZ; bilinen tek gercek "depoda yok".
        Assert.Equal(Simdi.AddDays(60), kayit.NotAfterUtc);

        Assert.True(sonuc.ShouldPersist);
        Assert.Equal(1, sonuc.MissingFromStore);
    }

    /// <summary>
    /// ORTAK KURAL: OLCEMEDIYSEN "YOK" DEME. Sertifika deposu acilamadiginda kayda HIC
    /// dokunulmaz -- aksi hâlde tek bir erisim hatasi butun kayitlara kalici "depoda yok" damgasi
    /// vurur ve gecici ariza kalici yanlis duruma donusur.
    /// </summary>
    [Fact]
    public void WhenTheStoreCannotBeRead_TheRecordIsLeftUntouched()
    {
        var kayit = Kayit("site", "ornek.com", eskiBitis: Simdi.AddDays(45), thumbprint: "ESKI");
        kayit.IsPresentInStore = true;
        kayit.LastVerifiedUtc = Simdi.AddDays(-10);

        var sonuc = SslStateReconciler.Reconcile([kayit], _ => SslStoreProbe.Unmeasured(), Simdi);

        Assert.True(kayit.IsPresentInStore);
        Assert.Equal("ESKI", kayit.Thumbprint);
        Assert.Equal(Simdi.AddDays(45), kayit.NotAfterUtc);

        // Dogrulama damgasi da TAZELENMEZ: yapilmamis bir olcumu yapilmis gostermek, kaydin
        // yasina bakan her degerlendirmeyi yaniltirdi.
        Assert.Equal(Simdi.AddDays(-10), kayit.LastVerifiedUtc);

        Assert.False(sonuc.ShouldPersist);
        Assert.Equal(1, sonuc.Unmeasured);
        Assert.Equal(0, sonuc.MissingFromStore);
    }

    /// <summary>
    /// Host adi olmayan kayit OLCULEMEZ (arama olcutu yok) ve "yok" sayilmaz.
    /// </summary>
    [Fact]
    public void ARecordWithoutAHostName_CountsAsUnmeasured_NotMissing()
    {
        var kayit = Kayit("site", string.Empty, eskiBitis: Simdi.AddDays(30), thumbprint: "ESKI");

        // Servisin ProbeCertificate'i bos host icin Unmeasured uretir; sozlesme burada sabitlenir.
        var sonuc = SslStateReconciler.Reconcile(
            [kayit],
            k => string.IsNullOrWhiteSpace(k.HostName) ? SslStoreProbe.Unmeasured() : SslStoreProbe.NotFound(),
            Simdi);

        Assert.Null(kayit.IsPresentInStore);
        Assert.Equal(0, sonuc.MissingFromStore);
        Assert.Equal(1, sonuc.Unmeasured);
    }

    /// <summary>
    /// Gercek DEGISMEDIYSE dosyaya yazilmaz: olcum her okuma yolunda calisir, her seferinde
    /// yazmak gereksiz disk trafigi ve esszamanli yazimlarla yaris demekti.
    /// </summary>
    [Fact]
    public void WhenNothingChanged_TheStateFileIsNotRewritten()
    {
        var kayit = Kayit("site", "ornek.com", eskiBitis: Simdi.AddDays(60), thumbprint: "AYNI");
        kayit.Source = "letsencrypt";
        kayit.IsPresentInStore = true;
        kayit.LastVerifiedUtc = Simdi.AddMinutes(-5);

        var sonuc = SslStateReconciler.Reconcile(
            [kayit],
            _ => SslStoreProbe.Found("AYNI", Simdi.AddDays(60), "letsencrypt"),
            Simdi);

        Assert.False(sonuc.ShouldPersist);
        Assert.Equal(1, sonuc.Verified);
    }

    /// <summary>
    /// Hicbir sey degismese bile dogrulama damgasi ESKIDIYSE bir kez yazilir; boylece "bu kayit
    /// en son ne zaman gercekle karsilastirildi" sorusu diskte de cevaplanabilir kalir.
    /// </summary>
    [Fact]
    public void AStaleVerificationStamp_IsRefreshedOnDisk()
    {
        var kayit = Kayit("site", "ornek.com", eskiBitis: Simdi.AddDays(60), thumbprint: "AYNI");
        kayit.Source = "letsencrypt";
        kayit.IsPresentInStore = true;
        kayit.LastVerifiedUtc = Simdi - SslStateReconciler.VerificationStampInterval - TimeSpan.FromMinutes(1);

        var sonuc = SslStateReconciler.Reconcile(
            [kayit],
            _ => SslStoreProbe.Found("AYNI", Simdi.AddDays(60), "letsencrypt"),
            Simdi);

        Assert.True(sonuc.ShouldPersist);
        Assert.Equal(Simdi, kayit.LastVerifiedUtc);
    }

    /// <summary>
    /// Kayit kimligi site + host + PORT'tur: ayni host farkli portlarda ayri kayitlardir ve
    /// birinin olcumu digerine uygulanmamalidir.
    /// </summary>
    [Fact]
    public void TheRecordKeyDistinguishesSiteHostAndPort()
    {
        var a = Kayit("site", "ornek.com", Simdi, "A");
        var b = Kayit("site", "ornek.com", Simdi, "B");
        b.Port = 8443;
        var c = Kayit("baska", "ornek.com", Simdi, "C");

        Assert.Equal(SslStateReconciler.Key(a), SslStateReconciler.Key(Kayit("SITE", "ORNEK.COM", Simdi, "A")));
        Assert.NotEqual(SslStateReconciler.Key(a), SslStateReconciler.Key(b));
        Assert.NotEqual(SslStateReconciler.Key(a), SslStateReconciler.Key(c));
    }

    /// <summary>
    /// Karisik liste: her kayit KENDI olcumune gore uzlasir; olculemeyen bir komsu, olculen
    /// kayitlarin tazelenmesini engellemez.
    /// </summary>
    [Fact]
    public void EachRecordIsReconciledAgainstItsOwnMeasurement()
    {
        var yenilenen = Kayit("a", "a.com", Simdi.AddDays(-1), "ESKI");
        var silinen = Kayit("b", "b.com", Simdi.AddDays(30), "VAR");
        var olculemeyen = Kayit("c", "c.com", Simdi.AddDays(10), "BILINMEYEN");

        var sonuc = SslStateReconciler.Reconcile(
            [yenilenen, silinen, olculemeyen],
            kayit => kayit.SiteName switch
            {
                "a" => SslStoreProbe.Found("YENI", Simdi.AddDays(89), "letsencrypt"),
                "b" => SslStoreProbe.NotFound(),
                _ => SslStoreProbe.Unmeasured()
            },
            Simdi);

        Assert.Equal(Simdi.AddDays(89), yenilenen.NotAfterUtc);
        Assert.False(silinen.IsPresentInStore);
        Assert.Null(olculemeyen.IsPresentInStore);
        Assert.Equal(Simdi.AddDays(10), olculemeyen.NotAfterUtc);

        Assert.Equal(1, sonuc.Verified);
        Assert.Equal(1, sonuc.MissingFromStore);
        Assert.Equal(1, sonuc.Unmeasured);
    }

    private static SslCertificateDto Kayit(string site, string host, DateTime eskiBitis, string thumbprint) =>
        new()
        {
            SiteName = site,
            HostName = host,
            Thumbprint = thumbprint,
            NotAfterUtc = eskiBitis,
            Source = "letsencrypt",
            Port = 443
        };
}
