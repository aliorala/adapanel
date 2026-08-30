using AdaPanel.Core.Dtos.Settings;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// OTURUM ÇEREZİNİN BAYRAKLARI ve onları düşürebilen TEK ayar.
/// </summary>
/// <remarks>
/// <para>
/// Ölçülen durum: çerez <c>HttpOnly</c>, <c>SameSite=Strict</c> ve <c>SecurePolicy=Always</c> ile
/// veriliyor (<c>Program.cs</c>). <c>Always</c> yalnız TEK bir koşulda düşüyor:
/// <c>Panel:AllowHttpFallback</c> açıkken <c>SameAsRequest</c> oluyor — yani çerez düz HTTP
/// üzerinden de gidiyor ve ağı dinleyen biri okuyabiliyor.
/// </para>
/// <para>
/// <b>Bulunan asimetri:</b> <c>appsettings.json</c> bayrağı <c>false</c> gönderiyordu ama iki DTO
/// (<see cref="PanelPreferencesDto"/>, <see cref="UpdatePanelPreferencesRequest"/>) başlangıç
/// değeri olarak <c>true</c> taşıyordu. Sonuç: ayarları kaydeden ama bu alanı GÖNDERMEYEN bir
/// istemci (elle yazılmış betik, entegrasyon) HTTP dinleyicisini ve korumasız çerezi sessizce
/// açardı. Panelin kendi arayüzü alanı her zaman gönderdiği için panelden yapılan kayıtlar
/// değişmiyor.
/// </para>
/// <para>
/// <b>Fazla kapatma riski YOK ve ölçüldü:</b> bayrak kapalıyken HTTP dinleyicisi 127.0.0.1'e
/// bağlanır (Program.cs), dışarıya yalnız HTTPS açılır. Kendinden imzalı sertifika da
/// <c>https</c> şemasıdır, yani <c>Secure</c> çerez tarayıcıda çalışır — sertifika uyarısını
/// geçmek yeterlidir.
/// </para>
/// </remarks>
public sealed class OturumCereziBayraklariTests
{
    [Fact]
    public void HTTP_geri_donusu_VARSAYILAN_OLARAK_kapali()
    {
        Assert.False(new PanelPreferencesDto().AllowHttpFallback);
        Assert.False(new UpdatePanelPreferencesRequest().AllowHttpFallback);
    }

    /// <summary>
    /// Çerez bayrakları <c>Program.cs</c>'te; burada METİN olarak ölçülüyor çünkü üst düzey
    /// deyimlerdeki bu kayıtları çalıştırmadan başka türlü sorgulamanın yolu yok (aynı yöntem
    /// <c>PanelRequestSurfaceWiringTests</c>'te de kullanılıyor). Ölçüm, bayraklardan birinin
    /// sessizce düşürülmesini yakalar.
    /// </summary>
    [Fact]
    public void Cerez_HttpOnly_SameSite_ve_Secure_bagli()
    {
        var kaynak = Oku("src", "AdaPanel.Api", "Program.cs");

        Assert.Contains("options.Cookie.HttpOnly = true;", kaynak, StringComparison.Ordinal);
        Assert.Contains("options.Cookie.SameSite = SameSiteMode.Strict;", kaynak, StringComparison.Ordinal);
        Assert.Contains(
            "options.Cookie.SecurePolicy = panelOptions.AllowHttpFallback ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;",
            kaynak,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// CSRF çerezi de aynı bayrakları taşımalı: ikisi ayrı kayıtlar ve biri güncellenip diğeri
    /// unutulabilir. (Ölçüldü: bugün ikisi de aynı satırı kullanıyor, yani iki kez geçiyor.)
    /// </summary>
    [Fact]
    public void CSRF_cerezi_de_ayni_bayraklari_tasir()
    {
        var kaynak = Oku("src", "AdaPanel.Api", "Program.cs");

        var secureSayisi = Sayi(kaynak, "CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always");
        var sameSiteSayisi = Sayi(kaynak, "options.Cookie.SameSite = SameSiteMode.Strict;");
        var httpOnlySayisi = Sayi(kaynak, "options.Cookie.HttpOnly = true;");

        Assert.True(secureSayisi >= 2, $"Secure ilkesi yalniz {secureSayisi} cerezde bagli; oturum ve CSRF cerezinin ikisi de olmali.");
        Assert.True(sameSiteSayisi >= 2, $"SameSite yalniz {sameSiteSayisi} cerezde bagli.");
        Assert.True(httpOnlySayisi >= 2, $"HttpOnly yalniz {httpOnlySayisi} cerezde bagli.");
    }

    private static int Sayi(string kaynak, string aranan)
    {
        var sayi = 0;
        var indeks = 0;
        while ((indeks = kaynak.IndexOf(aranan, indeks, StringComparison.Ordinal)) >= 0)
        {
            sayi++;
            indeks += aranan.Length;
        }

        return sayi;
    }

    private static string Oku(params string[] parcalar)
    {
        var yol = Path.Combine(Kok(), Path.Combine(parcalar));
        Assert.True(File.Exists(yol), $"kaynak bulunamadi: {yol}");
        return File.ReadAllText(yol);
    }

    private static string Kok()
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
