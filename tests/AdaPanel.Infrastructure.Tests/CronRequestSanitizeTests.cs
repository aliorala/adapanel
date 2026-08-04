using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Infrastructure.Scheduler;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Gorev isteginin temizlenmesi: gorunmez karakterler ve o tipte KULLANILMAYAN alanlar.
/// <para>
/// CANLI OLAY: kullanici "Bir PHP dosyasi" secip yolu yazdi ve panel
/// "Yazdiginiz calisma klasoru sunucuda yok" dedi -- oysa kullanici calisma klasoru diye bir sey
/// YAZMAMISTI. O alan PHP tipinde EKRANDA GORUNMUYOR; ama kullanici daha once bir .bat gorevini
/// duzenledigi icin formda eski deger duruyordu ve gizli haliyle gonderiliyordu.
/// </para>
/// <para>
/// Gorulemeyen bir alan yuzunden verilen hata, cozulemeyen bir hatadir: kullanici tekrar tekrar
/// dogru yolu yaziyor, panel tekrar tekrar ayni seyi soyluyor ve ikisi de kendince hakli.
/// </para>
/// </summary>
public sealed class CronRequestSanitizeTests
{
    private static CreateScheduledTaskRequest Istek(string taskType, string workingDirectory) => new()
    {
        Name = "gorev",
        TaskType = taskType,
        Command = @"C:\inetpub\wwwroot\ornek.test\httpdocs\imdb-cron.php",
        Url = "https://ornek.test/cron",
        WorkingDirectory = workingDirectory,
        ScheduleType = "hourly",
        Interval = 1
    };

    // Formun gizli tuttugu deger, kaydi ENGELLEMEMELI.
    [Theory]
    [InlineData("php")]
    [InlineData("url")]
    public void HiddenWorkingDirectory_IsDroppedForTypesThatDoNotShowIt(string taskType)
    {
        var istek = Istek(taskType, @"C:\eski\gorevden\kalan\klasor");

        SchedulerService.SanitizeForTests(istek);

        Assert.Empty(istek.WorkingDirectory);
    }

    // Alanin GORUNDUGU tiplerde deger korunur: kullanici bilerek yazmistir.
    [Theory]
    [InlineData("command")]
    [InlineData("script")]
    public void VisibleWorkingDirectory_IsKept(string taskType)
    {
        var istek = Istek(taskType, @"C:\gercekten\yazilmis");

        SchedulerService.SanitizeForTests(istek);

        Assert.Equal(@"C:\gercekten\yazilmis", istek.WorkingDirectory);
    }

    /// <summary>
    /// Windows Gezgini'nin "Yol olarak kopyala" komutu yolun basina U+202A ekler. Karakterin
    /// hicbir goruntusu yoktur: kullanici ne yazdigina baksa da farki GOREMEZ, panel ise bulamaz.
    /// Teshis edilmesi en zor hata turu budur -- iki taraf da kendince haklidir.
    /// </summary>
    [Fact]
    public void InvisibleDirectionMarks_AreStrippedFromPaths()
    {
        const string yol = @"C:\inetpub\wwwroot\ornek.test\httpdocs\imdb-cron.php";

        Assert.Equal(yol, SchedulerService.StripInvisibleCharacters('\u202A' + yol));
        Assert.Equal(yol, SchedulerService.StripInvisibleCharacters(yol + '\u200E'));
        Assert.Equal(yol, SchedulerService.StripInvisibleCharacters('\uFEFF' + yol + '\u2069'));
    }

    // Gorunur icerige DOKUNULMAZ: Turkce harfler, bosluklar ve noktalama oldugu gibi kalmali.
    [Fact]
    public void VisibleCharactersAreNeverTouched()
    {
        const string yol = @"C:\siteler\alioral.xyz\çıktı klasörü\görev-1.php";

        Assert.Equal(yol, SchedulerService.StripInvisibleCharacters(yol));
    }

    [Fact]
    public void Sanitize_CleansTheCommandNotJustTheWorkingDirectory()
    {
        var istek = Istek("php", string.Empty);
        istek.Command = '\u202A' + istek.Command;

        SchedulerService.SanitizeForTests(istek);

        Assert.StartsWith("C:", istek.Command, StringComparison.Ordinal);
    }
}
