using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// "ILK GUN CALISIR, N GUN SONRA SESSIZCE OLUR" — sayaci kurulum gunu baslayan varsayilanlar.
///
/// <para>
/// Bu sinifin kapattigi iki nokta da kurulum gunu YESIL yanar; hicbir kurulum-sonrasi denetim
/// onlari yakalayamaz, cunku kirilma ZAMANLA gelir. Ayni aile: posta sunucusunun tam 72 saat
/// sonra Gorev Zamanlayici tarafindan oldurulmesi (bkz. <c>LongRunningTaskSettingsTests</c>).
/// </para>
/// </summary>
public sealed class TimeBombDefaultsTests
{
    /// <summary>
    /// FTP hesabinin parola suresi hesap acilirken VE parola degisirken kapatilmali.
    /// </summary>
    /// <remarks>
    /// <b>ÖLÇÜLDÜ — BOZUK (canli, bu makinede).</b> FTP kullanicisi <c>net user … /add</c> ile
    /// acilan gercek bir Windows yerel hesabidir. Hicbir yerde parola suresi kapatilmiyordu, yani
    /// hesap makinenin <c>MaxPasswordAge</c> ilkesini (varsayilan 42 GUN) miras aliyordu.
    /// <c>Get-LocalUser</c> ciktisi kaniti: panelin actigi <c>alioral_xyz_ftp</c> (02.08 -&gt; 13.09)
    /// ve <c>test_local_ftp</c> (29.08 -&gt; 10.10) hesaplarinin ikisinde de TAM 42 gunluk bir
    /// <c>PasswordExpires</c> vardi; makinedeki diger hesaplarda (<c>aLi</c>) yoktu. Yani sure sonu
    /// YALNIZCA panelin actigi hesaplarda olusuyordu.
    ///
    /// Musteri 42 gun sonra <c>530 Login incorrect</c> alir; panel hesabi hala "etkin" gosterir.
    /// </remarks>
    [Fact]
    public void Ftp_hesabinin_parola_suresi_kapatiliyor_ve_geri_okunuyor()
    {
        var kaynak = Oku("src", "AdaPanel.Infrastructure", "Ftp", "FtpService.cs");

        // (a) Bayrak gercekten veriliyor.
        Assert.Contains("-PasswordNeverExpires $true", kaynak, StringComparison.Ordinal);

        // (b) GERI OKUMA: "hata vermedi" ile "oldu" ayni sey degil. Set-LocalUser sessizce etkisiz
        //     kalirsa bunu olcen tek sey geri okumadir; onsuz koruma bos bir guvence olurdu.
        Assert.Contains("Get-LocalUser -Name", kaynak, StringComparison.Ordinal);
        Assert.Contains("ADAPANEL-SURESIZ", kaynak, StringComparison.Ordinal);

        // (c) IKI cagri yeri: hesap OLUSTURULURKEN ve parola DEGISIRKEN. Yalnizca olusturmada
        //     cagirmak, bu duzeltmeden once acilmis hesaplari sonsuza dek bozuk birakirdi.
        var cagriSayisi = Say(kaynak, "await EnsurePasswordNeverExpiresAsync(");
        Assert.True(
            cagriSayisi >= 2,
            $"EnsurePasswordNeverExpiresAsync yalnizca {cagriSayisi} yerde cagriliyor; "
            + "hem hesap olusturma hem parola degistirme yolunda cagrilmali.");
    }

    /// <summary>
    /// Otomatik SSL yenileme VARSAYILAN ACIK olmali.
    /// </summary>
    /// <remarks>
    /// Let's Encrypt sertifikasi 90 GUN. Bayrak kapaliyken panel sertifikayi kurulum gunu alip bir
    /// daha hic dokunmuyordu. Tek basina "kilit gider" olurdu; ama sertifika kurulurken
    /// HTTP-&gt;HTTPS 301 yonlendirmesi de KOSULSUZ aciliyor
    /// (<c>WinAcmeSslService.ApplyHttpsRedirectAsync(…, true, …)</c>), yani 90. gunde ziyaretci
    /// HTTPS'e zorlanip suresi DOLMUS sertifikayla karsilasiyor: her sitede tam sayfa tarayici
    /// engeli. Ustelik uyari yolu (AlertsEnabled) da varsayilan kapali.
    ///
    /// KAYNAK TARAMASI, cunku olculen alan <c>AutomationService</c> icinde PRIVATE bir kayit
    /// tipindedir (<c>AutomationStateRecord</c>) ve disaridan ornegi alinamaz. Taranan sey
    /// varsayilanin ta kendisi: <c>= true</c> silinirse test kirmizi yanar.
    /// </remarks>
    [Fact]
    public void Otomatik_ssl_yenileme_varsayilan_acik()
    {
        var kaynak = Oku("src", "AdaPanel.Infrastructure", "Automation", "AutomationService.cs");

        Assert.Matches(
            @"public bool AutoSslRenewEnabled \{ get; set; \} = true;",
            kaynak);
    }

    /// <summary>
    /// Yukaridaki denetimlerin GERCEKTEN olctugunun kaniti — sentetik girdiyle.
    /// </summary>
    /// <remarks>
    /// Bu oturumda uc koruma dogustan olu cikti ve hepsi yesil gorunuyordu; bir testin var olmasi
    /// bir sey olctugu anlamina gelmez. Asagisi duzeltilen satirlarin ESKI ve YENI halini yan yana
    /// koyup denetcinin ikisini ayirt ettigini gosterir.
    /// </remarks>
    [Fact]
    public void Denetciler_eski_hali_gercekten_yakaliyor()
    {
        // SSL varsayilani: initializer'i olmayan hal YAKALANMALI.
        const string SslEski = "        public bool AutoSslRenewEnabled { get; set; }";
        const string SslYeni = "        public bool AutoSslRenewEnabled { get; set; } = true;";
        const string Desen = @"public bool AutoSslRenewEnabled \{ get; set; \} = true;";

        Assert.DoesNotMatch(Desen, SslEski);
        Assert.Matches(Desen, SslYeni);

        // "= false" ile susturma girisimi de yakalanmali.
        Assert.DoesNotMatch(Desen, "public bool AutoSslRenewEnabled { get; set; } = false;");

        // FTP: tek cagri yeri YETMEZ (parola degistirme yolu unutulursa eski hesaplar iyilesmez).
        Assert.Equal(1, Say("await EnsurePasswordNeverExpiresAsync(x);", "await EnsurePasswordNeverExpiresAsync("));
        Assert.Equal(0, Say("net user /add yapip hicbir sey yapmayan kod", "await EnsurePasswordNeverExpiresAsync("));
    }

    private static int Say(string metin, string parca)
    {
        var toplam = 0;
        var konum = metin.IndexOf(parca, StringComparison.Ordinal);
        while (konum >= 0)
        {
            toplam++;
            konum = metin.IndexOf(parca, konum + parca.Length, StringComparison.Ordinal);
        }

        return toplam;
    }

    private static string Oku(params string[] parcalar)
    {
        var yol = Path.Combine(KaynakKoku(), Path.Combine(parcalar));
        Assert.True(File.Exists(yol), $"kaynak bulunamadi: {yol}");
        return File.ReadAllText(yol);
    }

    private static string KaynakKoku()
    {
        var yol = AppContext.BaseDirectory;
        while (yol is not null && !File.Exists(Path.Combine(yol, "AdaPanel.sln")))
        {
            yol = Path.GetDirectoryName(yol);
        }

        Assert.NotNull(yol);
        return yol!;
    }
}
