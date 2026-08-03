using AdaPanel.Core.Dtos.Health;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Onarma raporunun "zaten güncel" kararı.
/// <para>
/// Bu kararın doğru olması, panelin en çok tekrarlanan hatasını önlüyor: bir şey bozukken
/// "her şey yolunda" demek. "Eksikleri tamamla" düğmesi de bu karara göre görünüyor, yani
/// yanlış hesaplanırsa kullanıcı düzeltmeye ULAŞAMIYOR.
/// </para>
/// </summary>
public sealed class RepairFindingTests
{
    [Fact]
    public void AlreadyCurrent_IsTrueOnlyWhenNothingWasFoundOrChanged()
    {
        Assert.True(new DomainRefreshResultDto().AlreadyCurrent);
    }

    // Asil regresyon: bulgu listesi hesaba katilmazsa, sitesi DURMUS bir alan adi "guncel"
    // gorunur ve onarma dugmesi hic cikmaz.
    [Fact]
    public void AlreadyCurrent_IsFalseWhenAFindingExists()
    {
        var result = new DomainRefreshResultDto
        {
            Findings = [new RepairFindingDto { Key = "repair.site_stopped", Severity = "fail" }]
        };

        Assert.False(result.AlreadyCurrent);
    }

    [Fact]
    public void AlreadyCurrent_IsFalseWhenTheMailCertificateWasRepublished()
    {
        Assert.False(new DomainRefreshResultDto { MailCertificateRefreshed = true }.AlreadyCurrent);
    }

    [Theory]
    [InlineData("repair.site_stopped")]
    [InlineData("repair.mail_domain_missing")]
    [InlineData("repair.mail_certificate_selfsigned")]
    public void Findings_ThatNeedConfirmation_AreNotReportedAsRepaired(string key)
    {
        // Onay bekleyen bir bulgu ASLA "onarildi" isaretlenmemeli: kullanici bakip "tamam,
        // panel halletmis" der ve sorun oldugu yerde kalir.
        var finding = new RepairFindingDto { Key = key, NeedsConfirmation = true };

        Assert.False(finding.Repaired);
    }
}
