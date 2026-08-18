using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Database;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Motora ozgu mekanizmalar: MySQL'de hesap host kumesi, PostgreSQL'de pg_hba.conf.
/// </summary>
public sealed class RemoteDbAccessEngineMechanismTests
{
    // =================================================================================
    // MySQL / MariaDB: hesap host kumesi
    // =================================================================================

    /// <summary>
    /// UZAK ERISIMI KAPATMAK SITEYI KAPATMAMALI. Kapatma yalnizca uzak hesaplari dusurur;
    /// uygulamanin kullandigi yerel hesaba dokunulmaz.
    /// </summary>
    [Fact]
    public void ClosingRemoteAccess_DropsOnlyRemoteAccounts()
    {
        var plan = MySqlAccountHosts.Plan(["localhost", "88.1.2.3"], []);

        Assert.Equal(["88.1.2.3"], plan.ToDrop);
        Assert.Empty(plan.ToCreate);
        Assert.Equal(["localhost"], plan.Final);
    }

    /// <summary>
    /// Hesap YALNIZCA '%' olarak varsa (panelin eski varsayilani) kapatmak onu tumuyle silmek
    /// olurdu -- kullanici uzak erisimi kapatirken sitesini de kapatmis olurdu. Bu durumda yerel
    /// kume OLUSTURULUR.
    /// </summary>
    [Fact]
    public void ClosingLegacyWideOpenAccount_CreatesLocalAccountsInstead()
    {
        var plan = MySqlAccountHosts.Plan(["%"], []);

        Assert.Equal(["%"], plan.ToDrop);
        Assert.Equal(MySqlAccountHosts.LocalHosts, plan.ToCreate);
        Assert.DoesNotContain("%", plan.Final);
        Assert.Contains("localhost", plan.Final);
    }

    /// <summary>
    /// Yerel kume UC deger icerir. skip-name-resolve kapaliyken 127.0.0.1 "localhost" olarak
    /// cozulur, ACIKKEN yalniz sayisal karsilik eslesir; ikisini de kapsamayan bir "yerel" tanimi
    /// bazi sunucularda hesabi erisilemez birakirdi.
    /// </summary>
    [Fact]
    public void LocalHostSet_CoversBothResolvedAndNumericLoopback()
    {
        Assert.Equal(["localhost", "127.0.0.1", "::1"], MySqlAccountHosts.LocalHosts);
        Assert.All(MySqlAccountHosts.LocalHosts, host => Assert.True(MySqlAccountHosts.IsLocal(host)));
        Assert.False(MySqlAccountHosts.IsLocal("%"));
        Assert.False(MySqlAccountHosts.IsLocal("88.1.2.3"));
    }

    /// <summary>Var olan yerel hesaplar KORUNUR; panel fazladan yerel hesap uretmez.</summary>
    [Fact]
    public void ExistingLocalAccounts_AreLeftAlone()
    {
        var plan = MySqlAccountHosts.Plan(["localhost"], ["88.1.2.3"]);

        Assert.Equal(["88.1.2.3"], plan.ToCreate);
        Assert.Empty(plan.ToDrop);
    }

    /// <summary>Listeden cikan uzak host dusurulur, eklenen olusturulur (eslemedir, eklemedir degil).</summary>
    [Fact]
    public void ChangingTheAllowlist_SynchronisesTheAccountSet()
    {
        var plan = MySqlAccountHosts.Plan(["localhost", "88.1.2.3"], ["10.%", "88.1.2.3"]);

        Assert.Equal(["10.%"], plan.ToCreate);
        Assert.Empty(plan.ToDrop);

        var daralt = MySqlAccountHosts.Plan(["localhost", "88.1.2.3", "10.%"], ["10.%"]);
        Assert.Equal(["88.1.2.3"], daralt.ToDrop);
    }

    /// <summary>
    /// SISTEM HESAPLARI kapali: root'un host kumesi degistirilirse panel kendi veritabani
    /// erisimini kaybeder ve geri donus yolu kalmaz.
    /// </summary>
    [Theory]
    [InlineData("root")]
    [InlineData("ROOT")]
    [InlineData("mysql.sys")]
    [InlineData("mariadb.sys")]
    [InlineData("mysql.session")]
    [InlineData("")]
    public void ProtectedAccounts_CannotBeManaged(string userName)
    {
        var hata = Assert.Throws<AppException>(() => MySqlAccountHosts.EnsureManageable(userName));
        Assert.Equal("database.remote_user_protected", hata.MessageKey);
    }

    [Fact]
    public void OrdinaryAccounts_CanBeManaged()
    {
        MySqlAccountHosts.EnsureManageable("site_wp");
    }

    // ---------------------------------------------------------------------------------
    // Yetki kopyalama (yeni host hesabi kaynaktan daha genis olamaz)
    // ---------------------------------------------------------------------------------

    /// <summary>Butun yetkiler varsa tumce kisalir.</summary>
    [Fact]
    public void AllPrivilegesGranted_ProducesAllPrivilegesClause()
    {
        var columns = MySqlAccountHosts.PrivilegeColumns.ToDictionary(item => item.Column, _ => true);

        Assert.Equal("ALL PRIVILEGES", MySqlAccountHosts.BuildGrantClause(columns));
    }

    /// <summary>Kismi yetki AYNEN kopyalanir; "hepsi"ne yuvarlanmaz.</summary>
    [Fact]
    public void PartialPrivileges_AreCopiedExactly_NotWidenedToAll()
    {
        var columns = MySqlAccountHosts.PrivilegeColumns.ToDictionary(item => item.Column, _ => false);
        columns["Select_priv"] = true;
        columns["Insert_priv"] = true;

        Assert.Equal("SELECT, INSERT", MySqlAccountHosts.BuildGrantClause(columns));
    }

    /// <summary>Yetkisiz satir kopyalanmaz (bos GRANT uretilmez).</summary>
    [Fact]
    public void NoPrivileges_ProducesNoClause()
    {
        var columns = MySqlAccountHosts.PrivilegeColumns.ToDictionary(item => item.Column, _ => false);

        Assert.Null(MySqlAccountHosts.BuildGrantClause(columns));
    }

    /// <summary>
    /// GRANT OPTION kopyalanmaz: klonlanan hesap kaynagindan DAHA GENIS olmamali ve bir
    /// barindirma hesabinin yetki dagitabilmesi istenmez.
    /// </summary>
    [Fact]
    public void GrantOption_IsNeverCopied()
    {
        var columns = MySqlAccountHosts.PrivilegeColumns.ToDictionary(item => item.Column, _ => true);
        columns["Grant_priv"] = true;

        var clause = MySqlAccountHosts.BuildGrantClause(columns);

        Assert.DoesNotContain("GRANT OPTION", clause, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Motor surumunde OLMAYAN sutun "hepsi var" kararini bozmaz.</summary>
    [Fact]
    public void MissingColumnsFromOlderEngines_DoNotBreakTheAllDecision()
    {
        var columns = MySqlAccountHosts.PrivilegeColumns
            .Where(item => item.Column != "Trigger_priv" && item.Column != "Event_priv")
            .ToDictionary(item => item.Column, _ => true);

        Assert.Equal("ALL PRIVILEGES", MySqlAccountHosts.BuildGrantClause(columns));
    }

    // =================================================================================
    // PostgreSQL: pg_hba.conf
    // =================================================================================

    private const string OrnekHba = """
        # TYPE  DATABASE   USER   ADDRESS        METHOD
        local   all        all                   scram-sha-256
        host    all        all    127.0.0.1/32   scram-sha-256
        host    all        all    ::1/128        scram-sha-256
        """;

    /// <summary>
    /// Panel blogu ILK host satirinin ONUNE konur. Sona eklemek ise yaramazdi: daha yukarida
    /// genis bir "host all all" satiri varsa ILK ESLESEN KAZANDIGI icin panelin satiri hic
    /// okunmaz, panel "actim" derken erisim acilmamis olurdu.
    /// </summary>
    [Fact]
    public void ManagedBlock_IsPlacedBeforeExistingHostRules()
    {
        var sonuc = PostgreSqlHbaEditor.Apply(OrnekHba, [new HbaEntry("musteri_db", "musteri", "88.1.2.3/32")], "scram-sha-256");
        var satirlar = sonuc.Split('\n').Select(item => item.Trim()).Where(item => item.Length > 0).ToArray();

        var blokIndex = Array.FindIndex(satirlar, item => item.StartsWith(PostgreSqlHbaEditor.BeginMarker, StringComparison.Ordinal));
        var ilkHostIndex = Array.FindIndex(satirlar, item => item.StartsWith("host", StringComparison.Ordinal));

        Assert.True(blokIndex >= 0);
        Assert.True(blokIndex < ilkHostIndex, "Panel blogu mevcut host kurallarindan SONRA konmus; ilk eslesen kural kazandigi icin etkisiz kalirdi.");

        // "local" satiri etkilenmez (TCP degil, yerel soket).
        Assert.Contains(satirlar, item => item.StartsWith("local", StringComparison.Ordinal));
    }

    /// <summary>Kapatmak = blogun GERCEKTEN yok olmasi.</summary>
    [Fact]
    public void ApplyingAnEmptyList_RemovesTheManagedBlockEntirely()
    {
        var acik = PostgreSqlHbaEditor.Apply(OrnekHba, [new HbaEntry("musteri_db", "musteri", "88.1.2.3/32")], "scram-sha-256");
        var kapali = PostgreSqlHbaEditor.Apply(acik, [], "scram-sha-256");

        Assert.DoesNotContain("88.1.2.3/32", kapali, StringComparison.Ordinal);
        Assert.DoesNotContain(PostgreSqlHbaEditor.BeginMarker, kapali, StringComparison.Ordinal);
        Assert.Contains("host    all        all    127.0.0.1/32", kapali, StringComparison.Ordinal);
    }

    /// <summary>Blok her seferinde KOMPLE degistirilir; eski satirlar birikmez.</summary>
    [Fact]
    public void Reapplying_ReplacesTheBlockInsteadOfAppending()
    {
        var birinci = PostgreSqlHbaEditor.Apply(OrnekHba, [new HbaEntry("musteri_db", "musteri", "88.1.2.3/32")], "scram-sha-256");
        var ikinci = PostgreSqlHbaEditor.Apply(birinci, [new HbaEntry("musteri_db", "musteri", "10.0.0.0/8")], "scram-sha-256");

        Assert.DoesNotContain("88.1.2.3/32", ikinci, StringComparison.Ordinal);
        Assert.Contains("10.0.0.0/8", ikinci, StringComparison.Ordinal);
        Assert.Equal(1, ikinci.Split(PostgreSqlHbaEditor.BeginMarker).Length - 1);
    }

    /// <summary>Yoneticinin elle ekledigi satirlar KORUNUR.</summary>
    [Fact]
    public void AdministratorsOwnRules_ArePreserved()
    {
        var elle = OrnekHba + "\nhost    all    all    203.0.113.0/24    md5\n";
        var sonuc = PostgreSqlHbaEditor.Apply(elle, [new HbaEntry("db", "kullanici", "88.1.2.3/32")], "scram-sha-256");

        Assert.Contains("203.0.113.0/24", sonuc, StringComparison.Ordinal);
    }

    /// <summary>
    /// SATIR ENJEKSIYONU: bosluk/satir sonu iceren bir ad, tek bir alan olmaktan cikip KENDI
    /// KURALINI yazardi. Bu dosyaya yazan tek kapi burasi oldugu icin burada da dogrulanir.
    /// </summary>
    [Theory]
    [InlineData("db\nhost all all 0.0.0.0/0 trust")]
    [InlineData("db all all 0.0.0.0/0 trust")]
    [InlineData("db\ttrust")]
    [InlineData("")]
    public void EntryFieldsThatCouldInjectARule_AreRejected(string kotuAd)
    {
        Assert.Throws<AppException>(() =>
            PostgreSqlHbaEditor.Apply(OrnekHba, [new HbaEntry(kotuAd, "kullanici", "88.1.2.3/32")], "scram-sha-256"));

        Assert.Throws<AppException>(() =>
            PostgreSqlHbaEditor.Apply(OrnekHba, [new HbaEntry("db", kotuAd, "88.1.2.3/32")], "scram-sha-256"));
    }

    /// <summary>Adres alani da bicime uymak zorunda (CIDR).</summary>
    [Theory]
    [InlineData("88.1.2.3")]
    [InlineData("88.1.2.3/32 trust")]
    [InlineData("all")]
    public void MalformedAddresses_AreRejected(string adres)
    {
        Assert.Throws<AppException>(() =>
            PostgreSqlHbaEditor.Apply(OrnekHba, [new HbaEntry("db", "kullanici", adres)], "scram-sha-256"));
    }

    /// <summary>
    /// Kimlik dogrulama yontemi DOSYADAN okunur. Sabit yazmak, md5 ile kurulmus bir sunucuda
    /// kullanicinin giris yapamamasina yol acardi -- panel "erisim verdim" derken.
    /// </summary>
    [Fact]
    public void AuthMethod_IsTakenFromTheFileItself()
    {
        var md5Dosya = OrnekHba.Replace("scram-sha-256", "md5");

        Assert.Equal("md5", PostgreSqlHbaEditor.DetectAuthMethod(md5Dosya));
        Assert.Equal("scram-sha-256", PostgreSqlHbaEditor.DetectAuthMethod(OrnekHba));
    }

    /// <summary>
    /// 'trust' ASLA yazilmaz. Dosyada trust'li bir host satiri olsa bile panel onu ornek almaz:
    /// uzak bir aga parolasiz erisim vermek olurdu.
    /// </summary>
    [Fact]
    public void TrustIsNeverAdopted_EvenIfTheFileUsesIt()
    {
        var trustDosya = "host    all    all    127.0.0.1/32    trust\n";

        Assert.Equal("scram-sha-256", PostgreSqlHbaEditor.DetectAuthMethod(trustDosya));

        var sonuc = PostgreSqlHbaEditor.Apply(trustDosya, [new HbaEntry("db", "kullanici", "88.1.2.3/32")], "trust");
        Assert.DoesNotContain("88.1.2.3/32    trust", sonuc, StringComparison.Ordinal);
        Assert.Contains("88.1.2.3/32    scram-sha-256", sonuc, StringComparison.Ordinal);
    }

    /// <summary>Yazilan blok geri okunabilmeli (panel "su an ne yaziyor" diye sorabilsin).</summary>
    [Fact]
    public void WrittenEntries_CanBeReadBack()
    {
        var sonuc = PostgreSqlHbaEditor.Apply(
            OrnekHba,
            [new HbaEntry("musteri_db", "musteri", "88.1.2.3/32"), new HbaEntry("musteri_db", "musteri", "10.0.0.0/8")],
            "scram-sha-256");

        var okunan = PostgreSqlHbaEditor.Read(sonuc);

        Assert.Equal(2, okunan.Count);
        Assert.All(okunan, entry =>
        {
            Assert.Equal("musteri_db", entry.Database);
            Assert.Equal("musteri", entry.User);
        });
        Assert.Equal(["88.1.2.3/32", "10.0.0.0/8"], okunan.Select(entry => entry.Address));
    }

    /// <summary>Blok DISINDAKI satirlar okunmaz: yoneticinin kurallari panelin sanilmasin.</summary>
    [Fact]
    public void ReadingIgnoresRulesOutsideTheManagedBlock()
    {
        Assert.Empty(PostgreSqlHbaEditor.Read(OrnekHba));
    }

    /// <summary>Windows satir sonlari korunur; dosya bicimi bozulmaz.</summary>
    [Fact]
    public void WindowsLineEndings_ArePreserved()
    {
        var crlf = OrnekHba.Replace("\n", "\r\n");
        var sonuc = PostgreSqlHbaEditor.Apply(crlf, [new HbaEntry("db", "kullanici", "88.1.2.3/32")], "scram-sha-256");

        Assert.Contains("\r\n", sonuc, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n", sonuc.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }
}
