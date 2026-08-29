using AdaPanel.Infrastructure.Bootstrap;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// SESSİZ ÖLÜM — panelin zamanlanmış görev olarak çalıştırdığı yardımcı süreçler (Stalwart posta
/// sunucusu, Memcached) kaç saat yaşar?
/// </summary>
/// <remarks>
/// Denetim bulgusu (ÖLÇÜLDÜ-BOZUK): kurulum <c>schtasks /Create … /SC ONSTART</c> ile görevi
/// kuruyordu. Komut "SUCCESS" yazar, kurulum sonrası port doğrulaması da YEŞİL yanar — ama ayarlar
/// Task Scheduler VARSAYILANINDA kalır. Bu makinede kurulu gerçek AdaStalwart görevi okundu:
/// <c>ExecutionTimeLimit=PT72H</c>, <c>RestartCount=0</c>, tek tetikleyici <c>BootTrigger</c>.
/// Microsoft belgesi: "By default, a task will be stopped 72 hours after it starts to run."
/// Aynı görevin son çalışma sonucu <c>267014</c> = <c>0x41306</c> = SCHED_S_TASK_TERMINATED'di ve
/// ölçüm anında hiçbir posta portu dinlenmiyordu.
///
/// Yani: posta 3 gün çalışır, sonra Task Scheduler öldürür, tek tetikleyici açılış olduğu için
/// makine yeniden başlatılana kadar bir daha açılmaz. Aşağıdaki testler o üç varsayılanı
/// (süre limiti / yeniden başlatma / pil) kilitler.
/// </remarks>
public sealed class LongRunningTaskSettingsTests
{
    private const string Komut = @"C:\ProgramData\AdaPanel\runtimes\stalwart\run-stalwart.cmd";

    /// <summary>
    /// ASIL KORUMA: süre limiti süresiz olmalı. <c>PT0S</c> Windows belgesinde "run indefinitely"
    /// demektir; <c>PT72H</c> ise sessiz ölümün ta kendisidir.
    /// </summary>
    [Fact]
    public void Gorev_suresiz_calisir_72_saatlik_varsayilan_yok()
    {
        var xml = ComponentBootstrapper.BuildLongRunningTaskXml(Komut);

        Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", xml);
        Assert.DoesNotContain("PT72H", xml);
    }

    /// <summary>
    /// Süreç çökerse görev açılışı BEKLEMEDEN geri gelmeli: eski hâlde <c>RestartCount=0</c> idi,
    /// yani tek bir çökme postayı sunucu yeniden başlatılana kadar bitiriyordu.
    /// </summary>
    [Fact]
    public void Cokme_sonrasi_yeniden_baslatma_tanimli()
    {
        var xml = ComponentBootstrapper.BuildLongRunningTaskXml(Komut);

        Assert.Contains("<RestartOnFailure>", xml);
        Assert.Contains("<Count>3</Count>", xml);
        Assert.Contains("<Interval>PT1M</Interval>", xml);
    }

    /// <summary>
    /// Pil ayarları: varsayılan ikisi de <c>true</c>'dur. UPS'li ya da dizüstü bir sunucuda
    /// güç kesintisi posta sunucusunu durdurur ve geri getirmez.
    /// </summary>
    [Fact]
    public void Pil_moduna_gecmek_sureci_durdurmaz()
    {
        var xml = ComponentBootstrapper.BuildLongRunningTaskXml(Komut);

        Assert.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", xml);
        Assert.Contains("<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>", xml);
    }

    /// <summary>
    /// Görev SYSTEM olarak ve yükseltilmiş çalışmalı (eski komutun <c>/RU SYSTEM /RL HIGHEST</c>
    /// kısmı kaybolmasın). Ad yerine SID: "SYSTEM" adı yerelleştirilmiş Windows'ta farklı yazılır.
    /// </summary>
    [Fact]
    public void Gorev_SYSTEM_olarak_yukseltilmis_calisir()
    {
        var xml = ComponentBootstrapper.BuildLongRunningTaskXml(Komut);

        Assert.Contains("<UserId>S-1-5-18</UserId>", xml);
        Assert.Contains("<RunLevel>HighestAvailable</RunLevel>", xml);
        Assert.Contains("<BootTrigger />", xml);
    }

    /// <summary>
    /// Çalıştırılacak komut XML'e KAÇIŞLI girmeli. Kurulum dizini <c>&amp;</c> içeren bir yola
    /// taşınırsa kaçışsız XML "malformed" diye reddedilir ve görev HİÇ kurulmaz — 72 saatlik
    /// sessiz ölümden de kötüsü.
    /// </summary>
    [Fact]
    public void Komut_yolu_XML_e_kacisli_yazilir()
    {
        var xml = ComponentBootstrapper.BuildLongRunningTaskXml(@"C:\Ar & Ge\calis.cmd");

        Assert.Contains("<Command>C:\\Ar &amp; Ge\\calis.cmd</Command>", xml);
        Assert.DoesNotContain("<Command>C:\\Ar & Ge", xml);
    }

    /// <summary>
    /// Şablonun kendisi Windows tarafından kabul edilen biçimde kalmalı: UTF-16 bildirimi ve
    /// Task Scheduler ad alanı. (Bu şablonun birebir kendisi gerçek bir Windows makinesinde
    /// <c>schtasks /Create /XML</c> ile kaydedilip geri okundu.)
    /// </summary>
    [Fact]
    public void Sablon_Task_Scheduler_bicimindedir()
    {
        var xml = ComponentBootstrapper.BuildLongRunningTaskXml(Komut);

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", xml);
        Assert.Contains("xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"", xml);
    }
}
