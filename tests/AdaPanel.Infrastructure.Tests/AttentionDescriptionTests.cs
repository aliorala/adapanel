using AdaPanel.Core.Dtos.Advisor;
using AdaPanel.Core.Dtos.Health;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// "Dikkat" öğelerinin AÇIKLAMA taşıması ve onarma raporunun "güncel" kararı.
/// </summary>
public sealed class AttentionDescriptionTests
{
    // Danisman aciklamayi zaten uretiyor; rozete kopyalanirken dusuruluyordu ve kullanici
    // yalnizca sorunun ADINI goruyordu.
    [Fact]
    public void AttentionItem_CarriesADescriptionKeySeparateFromTheTitle()
    {
        var item = new AttentionItemDto { TitleKey = "attention.software.updates", DescriptionKey = "attention.software.updates.desc" };

        Assert.NotEqual(item.TitleKey, item.DescriptionKey);
        Assert.NotEmpty(item.DescriptionKey);
    }

    // ROZET HIC SONMEME HATASI: onay bekleyen bulgular "yapilacak is" sayiliyordu.
    // Kullanici "Eksikleri tamamla" diyor, hicbir sey degismiyor, uyari geri geliyordu.
    [Fact]
    public void AlreadyCurrent_IgnoresFindingsThatOnlyNeedUserConfirmation()
    {
        var result = new DomainRefreshResultDto
        {
            Findings = [new RepairFindingDto { Key = "repair.site_stopped", NeedsConfirmation = true }]
        };

        Assert.True(result.AlreadyCurrent);
    }

    // Buna karsilik onarmanin DUZELTEBILECEGI bir bulgu varsa "guncel" denmemeli.
    [Fact]
    public void AlreadyCurrent_IsFalseWhenRepairStillHasWorkToDo()
    {
        var result = new DomainRefreshResultDto
        {
            Findings = [new RepairFindingDto { Key = "repair.zone_not_published", NeedsConfirmation = false }]
        };

        Assert.False(result.AlreadyCurrent);
    }

    [Fact]
    public void AlreadyCurrent_IsTrueForAnUntouchedDomain()
    {
        Assert.True(new DomainRefreshResultDto().AlreadyCurrent);
    }
}
