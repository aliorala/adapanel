using AdaPanel.Infrastructure.Ftp;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// FTP DENETİM PORTU HÜKMÜ — "kullanıcı var" demek yetmez, kullanıcı giriş yapabiliyor mu.
/// </summary>
/// <remarks>
/// <para>
/// <b>Düzeltilen hata (19.08.2026, bu makinede ölçüldü).</b> Panel
/// <c>isConfigured=true, statusMessage=ftp.server_ready</c> ve her kullanıcı için
/// <c>port: 21, existsOnSystem: true</c> raporluyordu; ama 21 portuna bağlanan istemciyi
/// <b>FileZilla Server 0.9.59</b> karşılıyordu (ham banner ile doğrulandı) ve panelin açtığı iki
/// gerçek kullanıcı doğru parolayla bile <c>530</c> alıyordu. Geçici olarak 2121 portuna alınınca
/// aynı kullanıcı doğru parolayla <c>230 User logged in</c>, yanlış parolayla <c>530</c> aldı —
/// yani MOTOR KUSURSUZ, tek sorun portun başkasında olması. Panel bunu hiçbir yerde söylemiyordu.
/// </para>
/// <para>
/// <b>Ölçümün en önemli ayrıntısı ve bu testlerin varlık sebebi:</b> "site çalışıyor mu" sorusu bu
/// hatayı GÖREMEZ. <c>Get-Website</c> siteyi <c>Started</c> raporladı, FTPSVC <c>Running</c>'di ve
/// 21 portunda GERÇEKTEN dinliyordu (pid 5068) — FileZilla (pid 5036) aynı portu paylaşıyordu.
/// Tek arıza belirtisi portun PAYLAŞILMASIYDI. Bu yüzden hüküm, dinleyicilerin TAMAMINA bakar.
/// </para>
/// </remarks>
public sealed class FtpControlPortDiagnosticsTests
{
    private static FtpService.FtpPortDinleyici Dinleyici(int pid, string surec, params string[] servisler)
        => new(pid, surec, servisler);

    [Fact]
    public void Portu_yalniz_FTPSVC_tutuyorsa_SORUN_YOK()
    {
        // Sağlıklı kurulum. Düzeltme bu yolu KIRMAMALI: yanlış alarm, çakışmayı kaçırmak kadar kötü.
        var (durum, sahip) = FtpService.ClassifyControlPort(
            olculebildi: true,
            [Dinleyici(5068, "svchost", "FTPSVC")],
            ftpsvcPidleri: [5068]);

        Assert.Equal(FtpService.FtpControlPortDurumu.PanelinServisi, durum);
        Assert.Equal(string.Empty, sahip);
    }

    [Fact]
    public void CANLI_SENARYO_port_PAYLASILIYORSA_cakisma_bildirilir()
    {
        // 19.08.2026'da bu makinede ölçülen tablonun birebir kendisi: FTPSVC de dinliyor, FileZilla
        // da. "En az bir dinleyici varsa sorun yok" diyen bir kontrol burada YANLIŞ cevap verirdi.
        var (durum, sahip) = FtpService.ClassifyControlPort(
            olculebildi: true,
            [
                Dinleyici(5068, "svchost", "FTPSVC"),
                Dinleyici(5036, "FileZilla Server", "FileZilla Server")
            ],
            ftpsvcPidleri: [5068]);

        Assert.Equal(FtpService.FtpControlPortDurumu.YabanciSurec, durum);

        // Suçlunun ADI söylenmeli: "bir sorun var" demek operatöre hiçbir şey kazandırmaz.
        Assert.Contains("FileZilla Server.exe", sahip);
        Assert.Contains("5036", sahip);
    }

    [Fact]
    public void Yabanci_surec_TEK_BASINA_portu_tutuyorsa_da_cakismadir()
    {
        // FTPSVC hiç bağlanamamış olabilir; o zaman panelin kullanıcıları hiç giriş yapamaz.
        var (durum, sahip) = FtpService.ClassifyControlPort(
            olculebildi: true,
            [Dinleyici(5036, "FileZilla Server", "FileZilla Server")],
            ftpsvcPidleri: [5068]);

        Assert.Equal(FtpService.FtpControlPortDurumu.YabanciSurec, durum);
        Assert.Contains("FileZilla Server.exe", sahip);
    }

    [Fact]
    public void Servis_adi_FTPSVC_ise_PID_listesi_bayat_olsa_da_yabanci_SAYILMAZ()
    {
        // FTPSVC paylaşımlı bir svchost içinde çalışır ve PID sorgusu yarış hâlinde bayatlayabilir.
        // Tek kaynağa güvenen bir kontrol KENDİ servisimizi yabancı ilan eder ve panel sağlıklı bir
        // sunucuda "çakışma var" diye bağırırdı.
        var (durum, _) = FtpService.ClassifyControlPort(
            olculebildi: true,
            [Dinleyici(9999, "svchost", "FTPSVC")],
            ftpsvcPidleri: [5068]);

        Assert.Equal(FtpService.FtpControlPortDurumu.PanelinServisi, durum);
    }

    [Fact]
    public void Ayni_surecteki_diger_servisler_hukmu_bozmaz()
    {
        // Paylaşımlı svchost birden çok servis barındırır; FTPSVC listede olduğu sürece bizimdir.
        var (durum, _) = FtpService.ClassifyControlPort(
            olculebildi: true,
            [Dinleyici(5068, "svchost", "WinRM", "FTPSVC", "Dnscache")],
            ftpsvcPidleri: []);

        Assert.Equal(FtpService.FtpControlPortDurumu.PanelinServisi, durum);
    }

    [Fact]
    public void Portu_KIMSE_dinlemiyorsa_hizmet_verilmiyor_demektir()
    {
        // Servis "Running" görünse bile bağlama kurulamamış olabilir; kimse bağlanamaz.
        var (durum, sahip) = FtpService.ClassifyControlPort(olculebildi: true, [], ftpsvcPidleri: [5068]);

        Assert.Equal(FtpService.FtpControlPortDurumu.DinleyenYok, durum);
        Assert.Equal(string.Empty, sahip);
    }

    [Fact]
    public void OLCULEMEDI_sessizce_SORUN_YOK_a_cevrilmez()
    {
        // Ölçüm yapılamadıysa (ör. Get-NetTCPConnection olmayan eski Windows) hüküm "temiz" DEĞİL
        // "bilinmiyor"dur. FirewallService.IsPortCovered'daki "ölçemedi = null" deseninin aynısı:
        // ölçüm eksikliğini iyi habere çevirmek, panelin yalan söylemesinin ta kendisi.
        var (durum, sahip) = FtpService.ClassifyControlPort(
            olculebildi: false,
            [Dinleyici(5036, "FileZilla Server", "FileZilla Server")],
            ftpsvcPidleri: []);

        Assert.Equal(FtpService.FtpControlPortDurumu.Olculemedi, durum);
        Assert.Equal(string.Empty, sahip);
    }

    [Fact]
    public void Surec_adi_okunamayan_yabanci_da_bildirilir()
    {
        // Süreç adı okunamayabilir (erişim reddi, 32/64 bit uyuşmazlığı). "Adını göremedim" demek,
        // çakışmayı hiç söylememek için gerekçe değildir.
        var (durum, sahip) = FtpService.ClassifyControlPort(
            olculebildi: true,
            [Dinleyici(4242, string.Empty)],
            ftpsvcPidleri: [5068]);

        Assert.Equal(FtpService.FtpControlPortDurumu.YabanciSurec, durum);
        Assert.Contains("4242", sahip);
    }
}
