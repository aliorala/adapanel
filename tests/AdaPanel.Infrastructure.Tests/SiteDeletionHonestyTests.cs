using System.Diagnostics;
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
            Assert.Equal(4, oncesi);

            // Dosyayı BAŞKASI tutuyor (IIS'in yazmakta olduğu günlük gibi).
            using var tutucu = new FileStream(kilitliDosya, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            Assert.ThrowsAny<Exception>(() => Directory.Delete(kok, recursive: true));

            var duruyor = Directory.Exists(kok);
            var kalan = duruyor ? SiteCascadeDeletionService.CountFilesSafely(kok) : 0;

            // ÖLÇÜM: silme yarıda kaldı ama dosyaların bir kısmı GERÇEKTEN gitti.
            Assert.True(duruyor);
            Assert.True(kalan < oncesi, $"Bu makinede kısmi silme üretilemedi ({oncesi} -> {kalan}); testin dayanağı ölçüm.");

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
        var sonuc = SiteCascadeDeletionService.ClassifyDeletion(oncekiDosyaSayisi: 5, kalanDosyaSayisi: 5, klasorDuruyor: true);
        Assert.Equal(SiteCascadeDeletionService.DirectoryDeletionOutcome.NotDeleted, sonuc);

        var mesaj = SiteCascadeDeletionService.DescribeDeletionFailure(
            "Site dosyaları", @"D:\web\musteri.com", sonuc, 5, 5, "erişim reddedildi");

        Assert.Contains("silinemedi, dosyalar duruyor", mesaj, StringComparison.Ordinal);
        Assert.DoesNotContain("KISMEN", mesaj, StringComparison.Ordinal);
    }

    /// <summary>Klasör gitmişse istisna gelse bile sonuç "silindi"dir (silme başka bir turda tamamlanmış olabilir).</summary>
    [Fact]
    public void WhenTheFolderIsGone_TheOutcomeIsDeleted()
    {
        Assert.Equal(
            SiteCascadeDeletionService.DirectoryDeletionOutcome.Deleted,
            SiteCascadeDeletionService.ClassifyDeletion(oncekiDosyaSayisi: 3, kalanDosyaSayisi: 0, klasorDuruyor: false));
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
            Assert.Equal(1, SiteCascadeDeletionService.CountFilesSafely(kok));
        }
        finally
        {
            TemizleyeSil(Path.Combine(kok, "baglanti"));
            TemizleyeSil(kok);
            TemizleyeSil(hedef);
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
        Assert.Contains(
            "if (await StepAsync(result, $\"Veritabanı ({db})\", () => databaseService.DeleteDatabaseAsync(engine, name, cancellationToken)))",
            kaynak,
            StringComparison.Ordinal);
        Assert.Contains("await TryRemoveOwnershipAsync(result, engine, name, cancellationToken);", kaynak, StringComparison.Ordinal);

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
