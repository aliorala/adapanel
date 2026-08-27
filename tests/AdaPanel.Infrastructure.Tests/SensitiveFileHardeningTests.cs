using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using AdaPanel.Infrastructure.Bootstrap;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Hassas klasörlerdeki DOSYALARIN kendi izinleri de kapatılmalı.
///
/// NEDEN VAR — canlıda ölçülen hata: 0.7.3 kurulu bir makinede yetkisiz kullanıcı olarak
/// ölçüldü. `certs` klasörünün ACL'i okunamıyordu (klasör sertleşmişti) ama içindeki
/// `panel.password` (32 bayt) ve `panel.pfx` (2725 bayt) HÂLÂ OKUNABİLİYORDU.
///
/// Sebep: klasörün DACL'ini sıfırdan kurmak, o klasördeki dosyaların ÜZERİNDEKİ açık ACE'leri
/// kaldırmaz. Sertifika betiği dosyaları kurulumda yazıyor, sertleştirme sonra çalışıyor ve
/// yalnız klasöre bakıyordu.
///
/// Pratik sonucu: barındırılan herhangi bir sitenin kodu panelin HTTPS özel anahtar parolasını
/// okuyabiliyordu. K1 (posta parolası) ve K2 (veritabanı dizini) aynı turda KAPANMIŞTI — bu
/// üçüncüsü açık kalmıştı ve yalnız canlı ölçümle görüldü.
/// </summary>
[SupportedOSPlatform("windows")]
public class SensitiveFileHardeningTests
{
    /// <summary>
    /// Sertleştirme, klasörün DOĞRUDAN altındaki dosyaların açık iznini kaldırmalı.
    /// Test yükseltilmiş yetki gerektirmez: kendi geçici klasöründe kendi sahipliğiyle çalışır.
    /// </summary>
    [Fact]
    public void Hassas_klasordeki_DOSYANIN_acik_izni_de_kaldirilir()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var kok = Directory.CreateTempSubdirectory("adapanel-file-acl");
        try
        {
            var dosya = Path.Combine(kok.FullName, "panel.password");
            File.WriteAllText(dosya, "gizli");

            // Dosyaya BİLEREK geniş bir izin ver — sertifika betiğinin ürettiği duruma benzesin.
            var herkes = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var oncesi = new FileInfo(dosya).GetAccessControl();
            oncesi.AddAccessRule(new FileSystemAccessRule(herkes, FileSystemRights.Read, AccessControlType.Allow));
            new FileInfo(dosya).SetAccessControl(oncesi);

            var acikOnce = GenisIzinVarMi(dosya);
            Assert.True(acikOnce, "test kurulumu hatali: dosyaya genis izin konulamadi");

            DataRootHardeningService.ProtectFile(dosya, NullLogger.Instance);

            Assert.False(GenisIzinVarMi(dosya), "dosyanin genis izni KALDIRILMADI");
        }
        finally
        {
            kok.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Sertleştirme akışı dosyaları da gezmeli. Kaynağa bakan nöbetçi: çağrı silinirse
    /// yukarıdaki davranış testi hâlâ geçer (fonksiyon doğru çalışıyor), ama ÇAĞRILMADIĞI için
    /// canlıda hiçbir şey değişmezdi — canlıda olan tam olarak buydu.
    /// </summary>
    [Fact]
    public void Sertlestirme_akisi_klasordeki_DOSYALARI_da_kilitler()
    {
        var kaynak = File.ReadAllText(Path.Combine(KaynakKoku(),
            "src", "AdaPanel.Infrastructure", "Bootstrap", "DataRootHardeningService.cs"));

        Assert.Contains("EnumerateFiles(dir, \"*\", SearchOption.TopDirectoryOnly)", kaynak);
        Assert.Contains("ProtectFile(file, logger)", kaynak);
    }

    /// <summary>
    /// Özyineleme OLMAMALI: `backups` altında yüz binlerce dosya olabilir; her servis açılışında
    /// hepsini gezmek açılışı kilitlerdi. Hassas dosyalar klasörün doğrudan altında.
    /// </summary>
    [Fact]
    public void Dosya_kilidi_OZYINELEMELI_degil()
    {
        var kaynak = File.ReadAllText(Path.Combine(KaynakKoku(),
            "src", "AdaPanel.Infrastructure", "Bootstrap", "DataRootHardeningService.cs"));

        Assert.DoesNotContain("EnumerateFiles(dir, \"*\", SearchOption.AllDirectories)", kaynak);
    }

    private static bool GenisIzinVarMi(string dosya)
    {
        var kurallar = new FileInfo(dosya).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));

        foreach (FileSystemAccessRule kural in kurallar)
        {
            var sid = (SecurityIdentifier)kural.IdentityReference;
            if (sid.IsWellKnown(WellKnownSidType.BuiltinUsersSid) ||
                sid.IsWellKnown(WellKnownSidType.WorldSid) ||
                sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid))
            {
                return true;
            }
        }

        return false;
    }

    private static string KaynakKoku()
    {
        var kok = AppContext.BaseDirectory;
        while (kok is not null && !File.Exists(Path.Combine(kok, "AdaPanel.sln")))
        {
            kok = Path.GetDirectoryName(kok);
        }

        Assert.NotNull(kok);
        return kok!;
    }
}
