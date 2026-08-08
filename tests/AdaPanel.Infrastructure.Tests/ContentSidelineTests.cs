using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// "Uzerine yazmadan ONCE yana al" kuralinin ortak uygulamasi. Ayni kural panelde uc ayri yerde
/// ayri ayri yazilmis ve her biri farkli derecede eksik kalmisti; testler kurali tek yerde korur.
/// </summary>
public sealed class ContentSidelineTests
{
    [Fact]
    public void MoveAside_MovesDirectoriesAndFiles_LeavingRootEmpty()
    {
        using var alan = new Alan();
        alan.Yaz("index.php", "eski");
        alan.Yaz("uploads", "fatura.pdf", "musteri verisi");

        var yanaAlma = ContentSideline.BuildSiblingPath(alan.Kok, "yedek");
        var tasinan = ContentSideline.MoveAside(alan.Kok, yanaAlma);

        Assert.Equal(2, tasinan.Count);
        Assert.Empty(Directory.EnumerateFileSystemEntries(alan.Kok));
        Assert.Equal("musteri verisi", File.ReadAllText(Path.Combine(yanaAlma, "uploads", "fatura.pdf")));

        // Yana alma klasoru kokun DISINDA olmali; icinde olsaydi "kokU bosalt" kendi yedegini de kapsardi.
        Assert.False(AdaPanel.Core.Common.PathContainment.IsInsideRoot(alan.Kok, yanaAlma));
    }

    [Fact]
    public void MoveAside_WithSelector_TouchesOnlySelectedEntries()
    {
        using var alan = new Alan();
        alan.Yaz("index.php", "eski");
        alan.Yaz("App_Data", "musteri.mdf", "veritabani");

        var yanaAlma = ContentSideline.BuildSiblingPath(alan.Kok, "yedek");
        var tasinan = ContentSideline.MoveAside(alan.Kok, yanaAlma, item => item.Name == "index.php");

        Assert.Single(tasinan);
        Assert.True(File.Exists(Path.Combine(alan.Kok, "App_Data", "musteri.mdf")));
    }

    [Fact]
    public void MoveAside_WhenOneEntryFails_RollsBackTheOnesAlreadyMoved()
    {
        using var alan = new Alan();
        alan.Yaz("aaa-once", "veri.txt", "ilk tasinan");
        alan.Yaz("bbb-kilitli", "kilit.dat", "kilitli");

        var yanaAlma = ContentSideline.BuildSiblingPath(alan.Kok, "yedek");

        using (new FileStream(
                   Path.Combine(alan.Kok, "bbb-kilitli", "kilit.dat"),
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            Assert.Throws<IOException>(() => ContentSideline.MoveAside(alan.Kok, yanaAlma));
        }

        // YARIM YANA ALMA EN KOTU DURUMDUR: kok ne eski ne yeni halde kalir. Geri sarilmali.
        Assert.Equal("ilk tasinan", File.ReadAllText(Path.Combine(alan.Kok, "aaa-once", "veri.txt")));
        Assert.Equal("kilitli", File.ReadAllText(Path.Combine(alan.Kok, "bbb-kilitli", "kilit.dat")));
    }

    [Fact]
    public void MoveBack_ReplacesEmptyShellDirectoryLeftByTheFailedRun()
    {
        using var alan = new Alan();
        alan.Yaz("temp", "roundcube.db", "posta verisi");

        var yanaAlma = ContentSideline.BuildSiblingPath(alan.Kok, "yedek");
        var tasinan = ContentSideline.MoveAside(alan.Kok, yanaAlma);

        // Basarisiz kurulum ayni adla BOS bir kabuk birakmis olabilir; taşimayi engellememeli.
        Directory.CreateDirectory(Path.Combine(alan.Kok, "temp"));

        ContentSideline.MoveBack(alan.Kok, tasinan);

        Assert.Equal("posta verisi", File.ReadAllText(Path.Combine(alan.Kok, "temp", "roundcube.db")));
    }

    [Fact]
    public void MoveBack_NeverThrows_AndReportsFailuresWithoutLosingData()
    {
        using var alan = new Alan();
        alan.Yaz("veri", "a.txt", "musteri");

        var yanaAlma = ContentSideline.BuildSiblingPath(alan.Kok, "yedek");
        var tasinan = ContentSideline.MoveAside(alan.Kok, yanaAlma);

        // Ayni adla DOLU bir klasor engel olur: geri koyma basarisiz olur ama fırlatmamalı,
        // cunku geri koyma zaten bir hata yolunda calisir ve asil hatayi gizlerdi.
        alan.Yaz("veri", "engel.txt", "araya giren");

        var hatalar = new List<SidelinedEntry>();
        ContentSideline.MoveBack(alan.Kok, tasinan, (entry, _) => hatalar.Add(entry));

        Assert.Single(hatalar);

        // VERI KAYBOLMADI: hala yana alma klasorunde duruyor.
        Assert.Equal("musteri", File.ReadAllText(Path.Combine(yanaAlma, "veri", "a.txt")));
    }

    [Fact]
    public void RemoveIfEmpty_KeepsDirectoryThatStillHoldsData()
    {
        using var alan = new Alan();
        var yanaAlma = Path.Combine(alan.Kok, "yedek");
        Directory.CreateDirectory(yanaAlma);
        File.WriteAllText(Path.Combine(yanaAlma, "kalan.txt"), "geri konamadi");

        ContentSideline.RemoveIfEmpty(yanaAlma);
        Assert.True(Directory.Exists(yanaAlma));

        File.Delete(Path.Combine(yanaAlma, "kalan.txt"));
        ContentSideline.RemoveIfEmpty(yanaAlma);
        Assert.False(Directory.Exists(yanaAlma));
    }

    [Fact]
    public void BuildSiblingPath_RefusesDriveRoot()
    {
        // Surucu kokunde kardes klasor yoktur; sessizce "C_yedek_..." uretip sasirtmak yerine reddet.
        Assert.Throws<IOException>(() => ContentSideline.BuildSiblingPath(@"C:\", "yedek"));
    }

    private sealed class Alan : IDisposable
    {
        private readonly string _ust;

        public Alan()
        {
            _ust = Path.Combine(Path.GetTempPath(), $"adapanel-sideline-{Guid.NewGuid():N}");
            Kok = Path.Combine(_ust, "kok");
            Directory.CreateDirectory(Kok);
        }

        public string Kok { get; }

        /// <summary>Son parca ICERIK, oncekiler yol parcalaridir.</summary>
        public void Yaz(params string[] parcalar)
        {
            var yol = Path.Combine([Kok, .. parcalar[..^1]]);
            Directory.CreateDirectory(Path.GetDirectoryName(yol)!);
            File.WriteAllText(yol, parcalar[^1]);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_ust, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
