using AdaPanel.Infrastructure.Backup;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Zamanlanmış yedeklerin hedef alanı.
///
/// NEDEN VAR: AutomationService ve SubscriptionBackupScheduleService, desteklenen hedef listesini
/// ELLE kopyalamıştı (`"ftp" or "s3" or "webdav"`). Yeni bulut hedefleri eklendiğinde o listeler
/// güncellenmedi; sonuç, kullanıcının "Dropbox" seçip kaydettiğinde ayarın SESSİZCE "local"e
/// düşmesi ve gece yedeğinin uzağa hiç gitmemesiydi. Panel bunu hata olarak göstermiyordu.
///
/// Bu testler kuralın TEK kaynaktan geldiğini sabitler: yeni bir hedef türü eklendiğinde
/// zamanlama yolları kendiliğinden onu tanır, ayrıca bir liste güncellenmesi gerekmez.
/// </summary>
public class ScheduleTargetNormalizationTests
{
    [Theory]
    [InlineData("ftp")]
    [InlineData("s3")]
    [InlineData("webdav")]
    [InlineData("http")]
    [InlineData("dropbox")]
    [InlineData("onedrive")]
    [InlineData("azure")]
    public void Desteklenen_hedef_local_e_DUSMEZ(string hedef)
    {
        Assert.Equal(hedef, BackupService.NormalizeScheduleTargetType(hedef));
    }

    [Theory]
    [InlineData("DROPBOX", "dropbox")]
    [InlineData("  Azure  ", "azure")]
    [InlineData("OneDrive", "onedrive")]
    public void Buyuk_harf_ve_bosluk_normallestirilir(string girdi, string beklenen)
    {
        Assert.Equal(beklenen, BackupService.NormalizeScheduleTargetType(girdi));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("local")]
    [InlineData("googledrive")]  // BackupService bunu açıkça reddediyor; zamanlamada da yerel kalmalı.
    [InlineData("uydurma-hedef")]
    public void Taninmayan_hedef_local_olur(string? girdi)
    {
        Assert.Equal("local", BackupService.NormalizeScheduleTargetType(girdi));
    }

    /// <summary>
    /// Asıl hatanın nöbetçisi: zamanlamanın tanıdığı küme, yedek servisinin uzak kabul ettiği
    /// kümeyle AYNI olmalı. Biri diğerinden ayrılırsa panel yine "kaydettim" deyip yereli yedekler.
    /// </summary>
    [Fact]
    public void Zamanlama_ve_uzak_silme_ayni_hedef_kumesini_taniyor()
    {
        foreach (var hedef in BackupService.RemoteTargetTypes)
        {
            Assert.Equal(hedef, BackupService.NormalizeScheduleTargetType(hedef));
            Assert.True(
                BackupService.SupportsRemoteDeletion(hedef),
                $"'{hedef}' zamanlamada tanınıyor ama uzak silme kapsamında değil -- yetim dosya üretir.");
        }
    }
}
