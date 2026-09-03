using AdaPanel.Core.Dtos.Databases;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Veritabanı tablo onarımının denetim kaydı, İSTENEN tablo adlarını değil ÖLÇÜLEN sonucu yazar.
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLEN AÇIK.</b> <c>DatabasesController.CheckAndRepairAsync</c> kayda koşulsuz
/// <c>"Tables repaired: " + string.Join(", ", repairTables)</c> yazıyordu — kullanıcının SEÇTİĞİ
/// adları. <c>MySqlMaintenanceRunner</c> ise onarımdan SONRA tabloyu yeniden denetliyor ve gerçek
/// cevabı <see cref="DatabaseCheckResultDto"/> içinde taşıyor. Doğru cevap elde duruyordu; kayıt
/// onu okumuyordu.
/// </para>
/// <para>
/// <b>Bedeli:</b> onarım çalışıp tablo hâlâ bozuk kaldığında bile kayıt "onarıldı" diyordu.
/// Yönetici bu kayda güvenip elindeki yedeği siler — bu depoda geri yükleme yolunda ölçülmüş,
/// aynı sınıftan bir kayıp.
/// </para>
/// <para>
/// Testler <see cref="DatabaseRepairAudit"/>'in gerçek davranışını ölçer; kaynak metni taramaz.
/// </para>
/// </remarks>
public sealed class OnarimKaydiDurustlukTests
{
    private static DatabaseTableCheckDto Tablo(string ad, string motor, string durum) => new()
    {
        Table = ad,
        Engine = motor,
        Status = durum,
        // Motor kararı üretimdeki ile AYNI kaynaktan gelmelidir; burada üretimin gördüğü
        // bayrakları taklit ediyoruz (MyISAM onarılır, InnoDB yalnız yeniden inşa edilir).
        CanRepair = motor is "MyISAM" or "Aria" or "ARCHIVE" or "CSV",
        CanRebuild = motor == "InnoDB"
    };

    private static DatabaseCheckResultDto Sonuc(params DatabaseTableCheckDto[] tablolar) =>
        new() { Engine = "mariadb", Database = "musteri_db", Tables = tablolar, RepairAttempted = true };

    // ------------------------------------------------------------------------------------------
    // FAZLA KAPATMA NÖBETÇİSİ: her şey yolundayken kayıt SADE kalmalı.
    //
    // "Her kayda uyarı yaz" çözümü aşağıdaki bütün testleri geçerdi ve her başarılı onarımı
    // şüpheli gösterirdi. Sürekli uyaran bir kayıt, uyarmayan kadar kullanışsızdır.
    // ------------------------------------------------------------------------------------------
    [Fact]
    public void Hepsi_onarildiysa_kayit_SADE_kalir()
    {
        var metin = DatabaseRepairAudit.Describe(
            Sonuc(Tablo("siparis", "MyISAM", "healthy"), Tablo("musteri", "MyISAM", "healthy")),
            ["siparis", "musteri"]);

        Assert.Equal("2 table(s) repaired: siparis, musteri.", metin);
        Assert.DoesNotContain("STILL FAULTY", metin, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT FOUND", metin, StringComparison.Ordinal);
    }

    /// <summary>
    /// Onarımdan SONRAKİ denetim hâlâ hata diyorsa "onarıldı" YAZILMAZ — ve hangi tablo olduğu
    /// söylenir.
    /// </summary>
    [Fact]
    public void Onarim_sonrasi_HALA_BOZUK_tablo_ADIYLA_yazilir()
    {
        var metin = DatabaseRepairAudit.Describe(
            Sonuc(Tablo("siparis", "MyISAM", "healthy"), Tablo("kayit", "MyISAM", "problem")),
            ["siparis", "kayit"]);

        Assert.Contains("STILL FAULTY: kayit", metin, StringComparison.Ordinal);
        // BASARILI tablo uyari bolumune DUSMEMELI: yoneticiyi saglam bir tabloyu kovalamaya
        // gondermek, sayiyi yanlis yazmak kadar zararlidir.
        var uyari = metin[metin.IndexOf("STILL FAULTY", StringComparison.Ordinal)..];
        Assert.DoesNotContain("siparis", uyari, StringComparison.Ordinal);
        Assert.Contains("repaired: siparis", metin, StringComparison.Ordinal);
    }

    /// <summary>
    /// Veritabanında OLMAYAN bir tablo için hiçbir ifade çalışmaz; kayıt bunu söylemeliydi.
    /// </summary>
    [Fact]
    public void Veritabaninda_OLMAYAN_tablo_onarildi_diye_YAZILMAZ()
    {
        var metin = DatabaseRepairAudit.Describe(
            Sonuc(Tablo("siparis", "MyISAM", "healthy")),
            ["siparis", "hayalet_tablo"]);

        Assert.Contains("NOT FOUND in database: hayalet_tablo", metin, StringComparison.Ordinal);
        Assert.DoesNotContain("repaired: siparis, hayalet_tablo", metin, StringComparison.Ordinal);
    }

    /// <summary>
    /// InnoDB'de <c>OPTIMIZE</c> tabloyu yeniden yazar ama bu BİR ONARIM DEĞİLDİR — DTO'nun kendi
    /// açıklaması da öyle diyor. İki işi aynı kelimeyle kaydetmek, bozuk bir InnoDB tablosunun
    /// onarıldığı izlenimini verirdi.
    /// </summary>
    [Fact]
    public void InnoDB_yeniden_insasi_ONARIM_diye_yazilmaz()
    {
        var metin = DatabaseRepairAudit.Describe(
            Sonuc(Tablo("gunluk", "InnoDB", "healthy")),
            ["gunluk"]);

        Assert.Contains("rebuilt (NOT a repair): gunluk", metin, StringComparison.Ordinal);
        Assert.DoesNotContain("1 table(s) repaired", metin, StringComparison.Ordinal);
    }

    /// <summary>
    /// Motor ne onarımı ne yeniden inşayı destekliyorsa panel ifadeyi HİÇ göndermez; kayıt da
    /// "denenmedi" demelidir.
    /// </summary>
    [Fact]
    public void DESTEKLENMEYEN_motor_denenmedi_diye_yazilir()
    {
        var metin = DatabaseRepairAudit.Describe(
            Sonuc(Tablo("bellek", "MEMORY", "healthy")),
            ["bellek"]);

        Assert.Contains("NOT ATTEMPTED", metin, StringComparison.Ordinal);
        Assert.Contains("bellek", metin, StringComparison.Ordinal);
        Assert.DoesNotContain("1 table(s) repaired", metin, StringComparison.Ordinal);
    }

    /// <summary>Aynı adı iki kez seçmek sayıyı ŞİŞİRMEZ.</summary>
    [Fact]
    public void TEKRAR_EDEN_adlar_sayiyi_sismez()
    {
        var metin = DatabaseRepairAudit.Describe(
            Sonuc(Tablo("siparis", "MyISAM", "healthy")),
            ["siparis", "siparis", " siparis "]);

        Assert.Equal("1 table(s) repaired: siparis.", metin);
    }
}
