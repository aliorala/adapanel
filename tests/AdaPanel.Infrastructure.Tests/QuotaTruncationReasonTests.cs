using AdaPanel.Infrastructure.Files;
using AdaPanel.Infrastructure.Security;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// BİRDEN ÇOK SİTENİN KESİLME SEBEBİNDEN HANGİSİ RAPOR EDİLİR.
///
/// <para>
/// <b>Neden ayrı bir dosya:</b> disk kotası bir aboneliğin BÜTÜN sitelerini gezer ve her site
/// kendi kesilme sebebini üretebilir. Kullanıcıya tek bir sebep gösterilir; hangisi olduğu
/// keyfi olamaz.
/// </para>
/// <para>
/// <b>Kural:</b> hesabı DURDURAN sebep (öge/süre bütçesi), yalnızca bir klasörün atlandığını
/// söyleyen erişim reddinin ÖNÜNE geçer. Bütçe dolduğunda ağacın büyük kısmına hiç bakılmamıştır;
/// orada "bir klasör okunamadı" demek, eksikliğin boyutunu KÜÇÜK göstermek olurdu.
/// </para>
/// <para>
/// <b>Neden bu test önceki turda yazılmadı ve neden şimdi yazılabiliyor:</b> o turda "gerçek ACL
/// manipülasyonu gerekiyor, makineye bağımlı olur" denip atlanmıştı. Doğru olan kısmı şu: iki
/// farklı sitede AYNI ANDA bütçe dolması ve erişim reddi kurmak gerçekten kırılgan bir test
/// üretir. Ama öncelik kararı yürüyüşün içinde değil, <see cref="CustomerScopeService.SebepSec"/>
/// adlı SAF bir fonksiyonda yaşıyor — ve saf fonksiyon doğrudan ölçülür. Ölçülemeyen şey
/// yürüyüşün tamamıydı, kuralın kendisi değil.
/// </para>
/// </summary>
public class QuotaTruncationReasonTests
{
    private const string Butce = SafeDirectoryWalk.SebepOgeButcesi;
    private const string Sure = SafeDirectoryWalk.SebepSureButcesi;
    private const string Reddi = SafeDirectoryWalk.SebepErisimReddi;

    /// <summary>Hiç sebep yokken ilk gelen sebep alınır.</summary>
    [Theory]
    [InlineData("", Butce, Butce)]
    [InlineData("", Sure, Sure)]
    [InlineData("", Reddi, Reddi)]
    public void Ilk_sebep_oldugu_gibi_alinir(string mevcut, string yeni, string beklenen)
        => Assert.Equal(beklenen, CustomerScopeService.SebepSec(mevcut, yeni));

    /// <summary>
    /// ASIL KURAL: bütçe sebebi, erişim reddinin ÜSTÜNE yazar.
    /// </summary>
    /// <remarks>
    /// Sıradan bağımsız olmalı: aynı abonelikte siteler hangi sırayla gezilirse gezilsin
    /// kullanıcı AYNI cümleyi görmeli. Sıra bağımlı olsaydı, listeyi yenilemek sebebi
    /// değiştirebilirdi ve kullanıcı hangisine inanacağını bilemezdi.
    /// </remarks>
    [Theory]
    [InlineData(Reddi, Butce, Butce)]   // once erisim reddi goruldu, sonra butce doldu
    [InlineData(Reddi, Sure, Sure)]
    [InlineData(Butce, Reddi, Butce)]   // ters sira: butce yine kazanir
    [InlineData(Sure, Reddi, Sure)]
    public void Butce_sebebi_ERISIM_REDDININ_onune_gecer_SIRADAN_BAGIMSIZ(
        string mevcut, string yeni, string beklenen)
        => Assert.Equal(beklenen, CustomerScopeService.SebepSec(mevcut, yeni));

    /// <summary>
    /// İki bütçe sebebi çarpışırsa İLK olan korunur — ikisi de "hesap durduruldu" der, aralarında
    /// kullanıcı için bir fark yoktur; sürekli üzerine yazmak yalnız gereksiz belirsizlik üretirdi.
    /// </summary>
    [Theory]
    [InlineData(Butce, Sure, Butce)]
    [InlineData(Sure, Butce, Sure)]
    public void Iki_butce_sebebi_carpisirsa_ILK_olan_korunur(string mevcut, string yeni, string beklenen)
        => Assert.Equal(beklenen, CustomerScopeService.SebepSec(mevcut, yeni));

    /// <summary>
    /// Kesilme YOKKEN sebep BOŞ kalır: boş sebep "kesilmedi" demektir ve arayüz rozetini buna
    /// göre çizer. Buraya bir varsayılan sebep koymak, tam ölçülen aboneliği eksik göstermek olurdu.
    /// </summary>
    [Fact]
    public void Sebep_yoksa_BOS_kalir()
        => Assert.Equal(string.Empty, CustomerScopeService.SebepSec(string.Empty, string.Empty));

    /// <summary>
    /// Öncelik, ortak yürüyüşün KENDİ kuralıyla aynı sabitleri kullanır.
    /// </summary>
    /// <remarks>
    /// Kaynağa bakan nöbetçi: kural bir gün kendi dize sabitlerini yazarsa (kopya anahtar),
    /// yürüyüş anahtarlarını değiştirdiğinde ikisi sessizce ayrışır ve arayüz çevirisi olmayan
    /// bir anahtar gösterir. Tutunulan ad <c>SafeDirectoryWalk.SebepErisimReddi</c>; bu dizenin
    /// dosyanın YORUMLARINDA geçmediği ayrıca doğrulandı — bu turda bir mutasyon tam da yorumda
    /// geçen bir dizeye tutunduğu için kaçmıştı.
    /// </remarks>
    [Fact]
    public void Kural_ortak_yuruyusun_SABITLERINI_kullanir()
    {
        var kaynak = File.ReadAllText(Path.Combine(KaynakKoku(),
            "src", "AdaPanel.Infrastructure", "Security", "CustomerScopeService.cs"));

        Assert.Contains("SafeDirectoryWalk.SebepErisimReddi", kaynak);
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
