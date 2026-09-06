using AdaPanel.Core.Dtos.Notifications;
using AdaPanel.Core.Dtos.Security;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// FAZLA KAPATMA NÖBETÇİSİ: sıfırlama postasını arka plana almak, onu SESSİZCE ÖLDÜRMEMELİ.
/// </summary>
/// <remarks>
/// <para>
/// Zamanlama sızıntısı, işi yanıt yolundan çıkararak kapatıldı. Bu düzeltmenin bariz riski şudur:
/// posta artık kimsenin baktığı bir yerde gönderilmiyorsa, gönderilmediği de fark edilmez ve
/// "sıfırlama postası hiç gelmiyor" arızası aylarca yaşar. Bu testler tam olarak bunu kovalar:
/// meşru kullanıcı postayı GERÇEKTEN alıyor mu, bağlantı GERÇEKTEN çalışıyor mu, gönderim
/// patladığında panel bunu KAYDEDİYOR mu.
/// </para>
/// <para>
/// Kaynak taranmıyor: her testte işin kendisi çalıştırılıp SONUÇLARI ölçülüyor.
/// </para>
/// </remarks>
public sealed class ParolaSifirlamaPostasiTests
{
    [Fact]
    public async Task Kayitli_adrese_posta_GERCEKTEN_gonderilir()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync();
        var bildirim = new KayitEdenBildirimServisi();

        await CalistirAsync(duzenek, bildirim, duzenek.Kullanici.Email);

        var posta = Assert.Single(bildirim.Gonderilenler);
        Assert.Equal(duzenek.Kullanici.Email, posta.Alici);
        Assert.Contains("resetToken=", posta.Govde, StringComparison.Ordinal);
        Assert.Contains("auth.password_reset.requested", duzenek.Denetim.Actions);
        Assert.DoesNotContain("auth.password_reset.mail_failed", duzenek.Denetim.Actions);
    }

    /// <summary>
    /// E-postadaki bağlantı GERÇEKTEN çalışmalı. "Posta gönderildi" demek yetmez: taşıdığı belirteç
    /// geçerli değilse kullanıcı için hiçbir şey değişmemiş olur.
    /// </summary>
    [Fact]
    public async Task Postadaki_baglanti_parolayi_GERCEKTEN_sifirlar()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync();
        var bildirim = new KayitEdenBildirimServisi();

        await CalistirAsync(duzenek, bildirim, duzenek.Kullanici.Email);

        var belirtec = BelirtecCikar(Assert.Single(bildirim.Gonderilenler).Govde);
        await duzenek.Servis.ResetPasswordAsync(belirtec, "Yeni!Parola12345");

        Assert.NotEqual(duzenek.IlkKarma, (await duzenek.TazeleAsync()).PasswordHash);
    }

    [Fact]
    public async Task Kayitsiz_adrese_posta_gitmez_ve_kayit_dusmez()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync();
        var bildirim = new KayitEdenBildirimServisi();

        await CalistirAsync(duzenek, bildirim, "yok@ornek.test");

        Assert.Empty(bildirim.Gonderilenler);
        // Denetim kaydı da yazılmaz: yazılsaydı kayıt, "hangi adresler denendi" listesine dönerdi.
        Assert.Empty(duzenek.Denetim.Actions);
    }

    [Fact]
    public async Task Pasif_hesaba_posta_gitmez()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync(aktif: false);
        var bildirim = new KayitEdenBildirimServisi();

        await CalistirAsync(duzenek, bildirim, duzenek.Kullanici.Email);

        Assert.Empty(bildirim.Gonderilenler);
        Assert.Empty(duzenek.Denetim.Actions);
    }

    /// <summary>
    /// SMTP hiç yapılandırılmamışsa posta ÇIKMAZ — ve panel bunu KAYDEDER. Eskiden burada yalnız
    /// bir günlük satırı vardı; yönetici panelde hiçbir şey görmüyordu.
    /// </summary>
    [Fact]
    public async Task SMTP_ayarsizken_arizanin_izi_KAYDA_dusulur()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync();
        var bildirim = new KayitEdenBildirimServisi { Yapilandirildi = false };

        await CalistirAsync(duzenek, bildirim, duzenek.Kullanici.Email);

        Assert.Empty(bildirim.Gonderilenler);
        Assert.Contains("auth.password_reset.mail_failed", duzenek.Denetim.Actions);
        Assert.DoesNotContain("auth.password_reset.requested", duzenek.Denetim.Actions);
        // Kullanıcıya ulaşmayacak bir belirteç üretilmemeli: üretilseydi kişinin ELİNDEKİ geçerli
        // (eski) bağlantıyı da geçersiz kılardı.
        Assert.False(File.Exists(duzenek.DosyaDeposu.ResolvePath(ParolaSifirlamaPostasi.BelirtecDepoYolu)));
    }

    /// <summary>
    /// Gönderim patlarsa (SMTP reddetti, ağ yok, kimlik doğrulama hatası) panel BAŞARI BİLDİRMEZ:
    /// kayıt "mail_failed" olur ve sebep kaydın içindedir.
    /// </summary>
    [Fact]
    public async Task Gonderim_patlarsa_arizanin_izi_KAYDA_dusulur()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync();
        var bildirim = new KayitEdenBildirimServisi
        {
            GonderimHatasi = new InvalidOperationException("smtp reddetti")
        };

        await CalistirAsync(duzenek, bildirim, duzenek.Kullanici.Email);

        Assert.DoesNotContain("auth.password_reset.requested", duzenek.Denetim.Actions);
        var kayit = Assert.Single(duzenek.Denetim.Kayitlar, giris => giris.Action == "auth.password_reset.mail_failed");
        Assert.Contains("smtp reddetti", kayit.Details, StringComparison.Ordinal);
    }

    // --- yardımcılar ---

    private static Task CalistirAsync(GirisDuzenegi duzenek, KayitEdenBildirimServisi bildirim, string adres) =>
        ParolaSifirlamaPostasi.GonderAsync(
            duzenek.DbContext,
            bildirim,
            new SabitTercihServisi(),
            new SabitGuvenlikServisi(),
            duzenek.Denetim,
            duzenek.DosyaDeposu,
            NullLogger.Instance,
            adres,
            CancellationToken.None);

    private static string BelirtecCikar(string govde)
    {
        const string isaret = "resetToken=";
        var basla = govde.IndexOf(isaret, StringComparison.Ordinal);
        Assert.True(basla >= 0, "Postada sifirlama baglantisi YOK.");
        basla += isaret.Length;
        var son = govde.IndexOfAny([' ', '\r', '\n'], basla);
        var ham = son < 0 ? govde[basla..] : govde[basla..son];
        return Uri.UnescapeDataString(ham);
    }
}

/// <summary>Gönderilen postaları kaydeden sahte; istenirse "SMTP yok" ya da "gönderim patladı" olur.</summary>
internal sealed class KayitEdenBildirimServisi : INotificationService
{
    public bool Yapilandirildi { get; set; } = true;

    public Exception? GonderimHatasi { get; set; }

    public List<(string Alici, string Konu, string Govde)> Gonderilenler { get; } = [];

    public Task<bool> IsEmailConfiguredAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Yapilandirildi);

    public Task SendEmailToAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default)
    {
        if (GonderimHatasi is not null)
        {
            throw GonderimHatasi;
        }

        Gonderilenler.Add((toEmail, subject, body));
        return Task.CompletedTask;
    }

    public Task<NotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<NotificationSettingsDto> UpdateSettingsAsync(UpdateNotificationSettingsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task SendEmailAsync(string subject, string body, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task SendWebhookAsync(string eventName, string body, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task TestAsync(TestNotificationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class SabitTercihServisi : IPanelPreferencesService
{
    public Task<PanelPreferencesDto> GetAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new PanelPreferencesDto
        {
            PanelName = "AdaPanel",
            ServerAddress = "panel.ornek.test",
            PreferredHttpsPort = 8890
        });

    public Task<PanelPreferencesDto> UpdateAsync(UpdatePanelPreferencesRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

internal sealed class SabitGuvenlikServisi : IPanelSecurityService
{
    public Task<string> GetSecureEntryPathAsync(CancellationToken cancellationToken = default)
        => Task.FromResult("admin");

    public Task<SecuritySettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<SecuritySettingsDto> UpdateSettingsAsync(UpdateSecuritySettingsRequest request, Guid actorUserId, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<bool> IsIpAllowedAsync(string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
