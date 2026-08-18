using System.Reflection;
using AdaPanel.Api.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// ROL KAPISI: coklu sunucu ekrani YALNIZ YONETICIYE acik.
/// <para>
/// Dugum listesi altyapinin haritasidir: kac sunucu var, hangi adreste, hangi surumde, ne kadar
/// dolu ve gizli giris yollari ne. Bir kiracinin (bayi ya da musteri) hicbir kaynagi bu bilgiyi
/// gerektirmez; ustelik "duguma git" ucu baska bir sunucunun yonetim adresini uretir. Panelin
/// kendi kurali da bu yonde: <c>SystemController</c> sunucu geneli olcumleri kiracidan gizler.
/// </para>
/// <para>
/// OLCUM YONTEMI: kaynak taramasi degil YANSIMA. Kaynakta <c>[Authorize(Roles = "Administrator")]</c>
/// yazisini aramak, ozniteligin gercekten baglandigini degil yalnizca yazildigini olcerdi;
/// yorum satirinda gecen bir metin bile testi gecirirdi.
/// </para>
/// </summary>
public sealed class ClusterAccessGuardTests
{
    [Fact]
    public void Denetleyici_SINIF_duzeyinde_yonetici_ister()
    {
        var yetki = typeof(ClusterController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(yetki);
        Assert.Equal("Administrator", yetki!.Roles);
    }

    /// <summary>
    /// Bayi ve musteri ADIYLA disarida. Rol listesine bir gun "Reseller" eklenirse bu test kirilir —
    /// ki eklenmesi bilincli bir karar olmali, kopyala-yapistir kazasi degil.
    /// </summary>
    [Theory]
    [InlineData("Reseller")]
    [InlineData("Customer")]
    [InlineData("Operator")]
    public void Kiraci_rolleri_DISARIDA(string rol)
    {
        var roller = (typeof(ClusterController).GetCustomAttribute<AuthorizeAttribute>()?.Roles ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.DoesNotContain(rol, roller, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// HICBIR eylem sinif kapisini delmez. Tek bir <c>[AllowAnonymous]</c> ya da rolu genisleten
    /// bir eylem ozniteligi, sinif duzeyindeki kapiyi o uc icin anlamsizlastirirdi.
    /// </summary>
    [Fact]
    public void Hicbir_eylem_sinif_kapisini_GEVSETMEZ()
    {
        var gevsetenler = new List<string>();

        foreach (var eylem in Eylemler())
        {
            if (eylem.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
            {
                gevsetenler.Add($"{eylem.Name}: [AllowAnonymous]");
                continue;
            }

            var yetki = eylem.GetCustomAttribute<AuthorizeAttribute>();
            if (yetki is not null && !string.Equals(yetki.Roles, "Administrator", StringComparison.Ordinal))
            {
                gevsetenler.Add($"{eylem.Name}: Roles = '{yetki.Roles}'");
            }
        }

        Assert.True(
            gevsetenler.Count == 0,
            "Sinif duzeyindeki yonetici kapisini gevseten eylem(ler) var:\n  " + string.Join("\n  ", gevsetenler));
    }

    /// <summary>
    /// OLCEMEDIK ile SORUN YOK ayrimi: yansima bozulup hic eylem bulunamazsa yukaridaki test
    /// sessizce gecer ve bir daha hicbir seyi korumazdi.
    /// </summary>
    [Fact]
    public void Eylem_taramasi_gercekten_calisiyor()
    {
        Assert.True(Eylemler().Count >= 8, $"Eylem taramasi bozulmus olabilir: yalnizca {Eylemler().Count} eylem bulundu.");
    }

    /// <summary>
    /// Dugum tarafi ucu (<c>node-info</c>) OKUMADIR.
    /// </summary>
    /// <remarks>
    /// Fiil onemli: <c>PanelTokenCapabilities</c> yalnizca durum DEGISTIREN fiillerde
    /// <c>AllowDestructive</c> ister. Uc POST olsaydi merkez, bir dugumu yalnizca IZLEMEK icin bile
    /// o dugumde tam yetkili (site silebilen) bir anahtar tasimak zorunda kalirdi.
    /// </remarks>
    [Fact]
    public void Node_info_ucu_GET_tir()
    {
        var eylem = typeof(ClusterController).GetMethod("GetNodeInfoAsync");

        Assert.NotNull(eylem);
        Assert.NotNull(eylem!.GetCustomAttribute<HttpGetAttribute>());
        Assert.Null(eylem.GetCustomAttribute<HttpPostAttribute>());
        Assert.Equal("node-info", eylem.GetCustomAttribute<HttpGetAttribute>()!.Template);
    }

    /// <summary>
    /// Rota, uzak istemcinin cagirdigi yolla AYNI olmali. Yol iki yerde yaziliyor (denetleyicinin
    /// rotasi ve istemcinin sabiti); biri degisirse ozellik sessizce 404'e duserdi.
    /// </summary>
    [Fact]
    public void Rota_istemcinin_cagirdigi_yolla_AYNI()
    {
        var rota = typeof(ClusterController).GetCustomAttribute<RouteAttribute>();
        Assert.Equal("api/v1/cluster", rota?.Template);

        var kaynak = KaynakOku("src", "AdaPanel.Infrastructure", "Cluster", "ClusterNodeClient.cs");
        Assert.Contains("\"api/v1/cluster/node-info\"", kaynak, StringComparison.Ordinal);
    }

    /// <summary>
    /// Denetim kaydi ZORUNLU: durum degistiren her uc iz birakmali. Kimin hangi sunucuyu kumeye
    /// ekledigi, hangi dugumde site actigi ve TLS dogrulamasini ne zaman kapattigi sorulari,
    /// ancak kayit varsa cevaplanabilir.
    /// </summary>
    [Fact]
    public void Durum_degistiren_her_uc_denetim_kaydi_yazar()
    {
        var kaynak = KaynakOku("src", "AdaPanel.Api", "Controllers", "ClusterController.cs");
        var eksik = new List<string>();
        var incelenen = 0;

        foreach (var (imza, govde) in UcGovdeleri(kaynak))
        {
            incelenen++;
            if (!govde.Contains("WriteAuditAsync(", StringComparison.Ordinal))
            {
                eksik.Add(imza);
            }
        }

        Assert.True(incelenen >= 5, $"Uc taramasi bozulmus olabilir: {incelenen} durum degistiren uc bulundu.");
        Assert.True(eksik.Count == 0, "Denetim kaydi YAZMAYAN uclar:\n  " + string.Join("\n  ", eksik));
    }

    private static IReadOnlyList<MethodInfo> Eylemler() =>
        typeof(ClusterController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(metot => metot.GetCustomAttributes<HttpMethodAttribute>().Any())
            .ToList();

    private static IEnumerable<(string Imza, string Govde)> UcGovdeleri(string metin)
    {
        var http = new System.Text.RegularExpressions.Regex(@"\[Http(Get|Post|Put|Delete|Patch)[^\]]*\]");
        var eslesmeler = http.Matches(metin);

        for (var i = 0; i < eslesmeler.Count; i++)
        {
            var mevcut = eslesmeler[i];
            var son = i + 1 < eslesmeler.Count ? eslesmeler[i + 1].Index : metin.Length;
            var imza = mevcut.Value;

            if (!imza.StartsWith("[HttpPost", StringComparison.Ordinal)
                && !imza.StartsWith("[HttpPut", StringComparison.Ordinal)
                && !imza.StartsWith("[HttpDelete", StringComparison.Ordinal)
                && !imza.StartsWith("[HttpPatch", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (imza, metin[mevcut.Index..son]);
        }
    }

    internal static string KaynakOku(params string[] parcalar)
    {
        var kok = AppContext.BaseDirectory;
        while (kok is not null && !File.Exists(Path.Combine(kok, "AdaPanel.sln")))
        {
            kok = Path.GetDirectoryName(kok);
        }

        Assert.NotNull(kok);
        return File.ReadAllText(Path.Combine([kok!, .. parcalar]));
    }
}
