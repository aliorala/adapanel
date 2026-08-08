using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// IIS'in günlük dizini ayarının okunması.
/// <para>
/// Bu yol panelde üç yerde sabit kodluydu (istatistikler, günlük görüntüleyici, otomatik günlük
/// temizliği). Yönetici klasörü taşımışsa üçü de <b>sessizce yanlış cevap</b> veriyordu: sayaçlar
/// sıfır, günlükler boş, temizlik "0 dosya silindi, tamamlandı". Yanlış okumak sessiz kaldığı için
/// ayrıştırmanın testi şart.
/// </para>
/// </summary>
public sealed class IisLogPathsTests : IDisposable
{
    private readonly string klasor = Path.Combine(Path.GetTempPath(), $"ada-iislog-{Guid.NewGuid():N}");

    public IisLogPathsTests() => Directory.CreateDirectory(klasor);

    public void Dispose()
    {
        try { Directory.Delete(klasor, recursive: true); } catch { /* en iyi çaba */ }
    }

    [Fact]
    public void Sunucu_varsayilani_okunur()
    {
        var yol = Yaz("""
            <configuration>
              <system.applicationHost>
                <sites>
                  <siteDefaults>
                    <logFile logFormat="W3C" directory="D:\Logs" />
                  </siteDefaults>
                  <site name="ornek" id="7" />
                </sites>
              </system.applicationHost>
            </configuration>
            """);

        Assert.Equal(@"D:\Logs", IisLogPaths.ResolveRootFrom(yol, siteId: null));
    }

    [Fact]
    public void Sitenin_KENDI_ayari_varsayilani_ezer()
    {
        // IIS'te de böyle davranır; kontrol koruduğu şeyle aynı kuralı uygulamalı.
        var yol = Yaz("""
            <configuration>
              <system.applicationHost>
                <sites>
                  <siteDefaults>
                    <logFile directory="D:\Logs" />
                  </siteDefaults>
                  <site name="ozel" id="7">
                    <logFile directory="E:\OzelLoglar" />
                  </site>
                </sites>
              </system.applicationHost>
            </configuration>
            """);

        Assert.Equal(@"E:\OzelLoglar", IisLogPaths.ResolveRootFrom(yol, siteId: 7));
        Assert.Equal(@"D:\Logs", IisLogPaths.ResolveRootFrom(yol, siteId: 9));   // ayarı olmayan site
        Assert.Equal(@"D:\Logs", IisLogPaths.ResolveRootFrom(yol, siteId: null)); // sunucu geneli
    }

    [Fact]
    public void Ortam_degiskeni_genisletilir()
    {
        // IIS bu ayarı neredeyse her zaman %SystemDrive% ile yazar; genişletilmezse yol geçersizdir.
        var yol = Yaz("""
            <configuration>
              <system.applicationHost>
                <sites>
                  <siteDefaults>
                    <logFile directory="%SystemDrive%\inetpub\logs\LogFiles" />
                  </siteDefaults>
                </sites>
              </system.applicationHost>
            </configuration>
            """);

        var sonuc = IisLogPaths.ResolveRootFrom(yol, siteId: null);
        Assert.DoesNotContain("%", sonuc);
        Assert.EndsWith(@"inetpub\logs\LogFiles", sonuc);
    }

    [Theory]
    // Dosya yok / bozuk XML / beklenen düğümler yok / dizin boş: hepsinde IIS VARSAYILANINA düşülür.
    // "Ölçemedik" durumunda davranış, düzeltmeden önceki hâlin aynısı olmalı — hiçbir kurulum bozulmaz.
    [InlineData(null)]
    [InlineData("<configuration><bozuk")]
    [InlineData("<configuration><system.applicationHost /></configuration>")]
    [InlineData("<configuration><system.applicationHost><sites><siteDefaults><logFile directory=\"   \" /></siteDefaults></sites></system.applicationHost></configuration>")]
    public void Okunamayan_ayar_varsayilana_duser(string? icerik)
    {
        var yol = icerik is null ? Path.Combine(klasor, "yok.config") : Yaz(icerik);

        var sonuc = IisLogPaths.ResolveRootFrom(yol, siteId: null);

        Assert.False(string.IsNullOrWhiteSpace(sonuc));
        Assert.EndsWith(@"inetpub\logs\LogFiles", sonuc);
    }

    [Fact]
    public void Site_klasoru_W3SVC_onekiyle_birlesir()
    {
        // IIS her sitenin günlüğünü kökün altında "W3SVC<id>" klasörüne yazar.
        var sonuc = IisLogPaths.ResolveSiteDirectory(42);
        Assert.EndsWith(@"\W3SVC42", sonuc);
    }

    private string Yaz(string icerik)
    {
        var yol = Path.Combine(klasor, $"{Guid.NewGuid():N}.config");
        File.WriteAllText(yol, icerik);
        return yol;
    }
}
