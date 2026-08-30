using System.Reflection;
using AdaPanel.Api.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Zamanlayıcı ORTAM ölçümü sunucu iç bilgisidir; kapsamlı role açılmamalı.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bulunan sızıntı (canlı panelde ölçüldü):</b> <c>GET /api/v1/cron/environment</c> sınıf
/// düzeyindeki <c>Administrator,Reseller,Customer</c> rolüyle açıktı ve yanıt sunucudaki
/// yorumlayıcıların TAM YOLLARINI içeriyordu — ölçülen değer
/// <c>C:\Users\&lt;yönetici&gt;\AppData\Local\Python\…</c>, yani sunucu yöneticisinin Windows
/// kullanıcı adı bir hosting müşterisine düz metin olarak dönüyordu.
/// </para>
/// <para>
/// Panelde aynı sınıf karar zaten iki kez verilmişti: <c>SystemController</c> kapsamlı role
/// makine adını/işletim sistemini boş döner, <c>FtpController.GetStatusAsync</c> yabancı süreç
/// adlarını taşıdığı için açıkça yöneticiye özeldir. Bu uç o kuralın dışında kalmıştı.
/// </para>
/// <para>
/// Test ROL ÖZNİTELİĞİNE bakar çünkü kuralın kendisi bir özniteliktir; gövdede bir <c>if</c>
/// aramak, korumanın yaşadığı yerden başka bir yeri ölçmek olurdu.
/// </para>
/// </remarks>
public sealed class CronEnvironmentExposureTests
{
    /// <summary>Ortam bilgisine dokunan uçların tamamı (okuma + yazma).</summary>
    public static TheoryData<string> YoneticiyeOzelEylemler =>
    [
        nameof(CronController.GetEnvironmentAsync),
        nameof(CronController.AddPythonToPathAsync)
    ];

    [Theory]
    [MemberData(nameof(YoneticiyeOzelEylemler))]
    public void Ortam_uclari_YALNIZ_yoneticiye_acik(string eylem)
    {
        var roller = EylemRolleri(eylem);

        Assert.Equal("Administrator", roller);
    }

    [Fact]
    public void Sinif_rolu_hala_kapsamli_rolleri_iceriyor()
    {
        // Nöbet iki yönlü: yukarıdaki kısıt, controller'ın TAMAMINI yöneticiye kapatarak da
        // "geçirilebilir". O, müşterinin zamanlanmış görev yönetimini tümden kaybetmesi demekti —
        // yani sızıntıyı kapatırken satılan özelliği öldürmek.
        var sinif = typeof(CronController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(sinif);
        Assert.Contains("Customer", sinif!.Roles!);
        Assert.Contains("Reseller", sinif.Roles!);
    }

    private static string EylemRolleri(string eylem)
    {
        var metot = typeof(CronController).GetMethod(eylem, BindingFlags.Public | BindingFlags.Instance);
        Assert.True(metot is not null, $"CronController'da '{eylem}' eylemi yok.");

        var oznitelik = metot!.GetCustomAttribute<AuthorizeAttribute>();

        // Eylemde öznitelik YOKSA sınıfınki geçerlidir; onu döndürmek, kısıtın kaldırıldığını
        // görünür kılar (aksi hâlde test "öznitelik yok" diye sessizce geçebilirdi).
        return oznitelik?.Roles
               ?? typeof(CronController).GetCustomAttribute<AuthorizeAttribute>()?.Roles
               ?? string.Empty;
    }
}
