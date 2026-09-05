using AdaPanel.Api.Controllers;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Mail;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// KİMLİKSİZ uçtan gönderilen postanın İÇİNE konan adresi kim belirliyor?
/// </summary>
/// <remarks>
/// <para>
/// <b>Ölçülen açık:</b> <c>MailListPublicController</c> onay bağlantısını <c>Request.Host</c>'tan
/// kuruyordu. <c>appsettings.json</c>'da <c>"AllowedHosts": "*"</c> ve boru hattında
/// <c>UseHostFiltering</c> YOK — yani <c>Host</c> başlığı istemcinin serbest metnidir. Uç
/// kimliksizdir ve GERÇEK bir aboneye PANELİN alan adından posta gönderttiği için, saldırgan
/// kendi adresini taşıyan bir bağlantıyı kurbanın kutusuna panelin itibarıyla koydurabiliyordu.
/// Bağlantıya tıklandığında tek kullanımlık belirteç de saldırgana giderdi.
/// </para>
/// <para>
/// <b>Fazla kapatma nöbetçileri de burada:</b> panel adresi ayarlı değilken bağlantı YİNE
/// üretilmeli (yoksa özellik ölür) ve 443'te porta yazılmamalı (yoksa bağlantı kırılır).
/// </para>
/// </remarks>
public sealed class PostaBaglantiAdresiTests
{
    private const string Liste = "duyuru@ornek.com";
    private const string Abone = "abone@baska.test";

    [Fact]
    public async Task Onay_baglantisi_YAPILANDIRILMIS_panel_adresinden_kurulur_Host_basligi_yok_sayilir()
    {
        using var duzenek = new Duzenek(sunucuAdresi: "panel.ornek.com", httpsPort: 8888, hostBasligi: "kotu.example");

        await duzenek.Denetleyici.RequestAsync(Liste, Abone, CancellationToken.None);

        var govde = duzenek.Postaci.SonGovde;
        Assert.Contains(
            "https://panel.ornek.com:8888/api/v1/public/mail-lists/unsubscribe/confirm?token=",
            govde,
            StringComparison.Ordinal);
        Assert.DoesNotContain("kotu.example", govde, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Host_basligindaki_sema_ve_yol_enjeksiyonu_da_adrese_giremez()
    {
        // Başlıkla yalnız ana ad değiştirilemez; şema/port/yol denemesi de panel adresini ezemez.
        using var duzenek = new Duzenek(
            sunucuAdresi: "panel.ornek.com",
            httpsPort: 8888,
            hostBasligi: "kotu.example:1337");

        await duzenek.Denetleyici.RequestAsync(Liste, Abone, CancellationToken.None);

        Assert.DoesNotContain("1337", duzenek.Postaci.SonGovde, StringComparison.Ordinal);
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: <c>ServerAddress</c> boş bırakılabilen bir alandır. Yedeği
    /// kaldırmak, adresi ayarlamamış her kurulumda onay bağlantısını ÖLÜ göndermek olurdu.
    /// </summary>
    [Fact]
    public async Task Panel_adresi_ayarli_degilse_baglanti_YINE_uretilir_istek_adresinden()
    {
        using var duzenek = new Duzenek(sunucuAdresi: "", httpsPort: 8888, hostBasligi: "panel.ornek.com:8888");

        await duzenek.Denetleyici.RequestAsync(Liste, Abone, CancellationToken.None);

        Assert.Contains(
            "https://panel.ornek.com:8888/api/v1/public/mail-lists/unsubscribe/confirm?token=",
            duzenek.Postaci.SonGovde,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: 443'te porta yazmak <c>https://host:443/…</c> üretir; çalışır ama
    /// panelin başka her bağlantısıyla tutarsız olur ve sertifika/vekil kurulumlarında kırılır.
    /// </summary>
    [Fact]
    public async Task Panel_443_te_yayindaysa_adrese_port_yazilmaz()
    {
        using var duzenek = new Duzenek(sunucuAdresi: "panel.ornek.com", httpsPort: 443, hostBasligi: "panel.ornek.com");

        await duzenek.Denetleyici.RequestAsync(Liste, Abone, CancellationToken.None);

        Assert.Contains(
            "https://panel.ornek.com/api/v1/public/mail-lists/unsubscribe/confirm?token=",
            duzenek.Postaci.SonGovde,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Uç bir POSTA YANSITICISI olmamalı: alıcı istekten değil, sunucudaki ABONE kümesinden gelir.
    /// </summary>
    [Fact]
    public async Task Abone_OLMAYAN_bir_adrese_posta_gonderilmez()
    {
        using var duzenek = new Duzenek(sunucuAdresi: "panel.ornek.com", httpsPort: 8888, hostBasligi: "panel.ornek.com");

        await duzenek.Denetleyici.RequestAsync(Liste, "kurban@baska-kurum.test", CancellationToken.None);

        Assert.Equal(0, duzenek.Postaci.GonderimSayisi);
    }

    /// <summary>Gönderilen postanın ALICISI istekten değil, doğrulanmış abone kaydından gelir.</summary>
    [Fact]
    public async Task Alici_dogrulanmis_abone_adresidir()
    {
        using var duzenek = new Duzenek(sunucuAdresi: "panel.ornek.com", httpsPort: 8888, hostBasligi: "panel.ornek.com");

        await duzenek.Denetleyici.RequestAsync(Liste, Abone, CancellationToken.None);

        Assert.Equal(Abone, duzenek.Postaci.SonAlici);
    }

    // ------------------------------------------------------------------
    // Düzenek
    // ------------------------------------------------------------------

    private sealed class Duzenek : IDisposable
    {
        private readonly string _kok = Path.Combine(
            Path.GetTempPath(),
            "adapanel-posta-adres-" + Guid.NewGuid().ToString("N"));

        public Duzenek(string sunucuAdresi, int httpsPort, string hostBasligi)
        {
            Directory.CreateDirectory(_kok);
            var depo = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(
                new AdaPanel.Infrastructure.Options.PanelRuntimeOptions { DataRoot = _kok }));

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Scheme = "https";
            httpContext.Request.Host = new HostString(hostBasligi);

            Denetleyici = new MailListPublicController(
                new MailListUnsubscribeService(depo, new SahtePostaServisi { Listeler = [new MailListDto { Address = Liste, Recipients = [Abone] }] }, Postaci, NullLogger<MailListUnsubscribeService>.Instance),
                new AuditTenantLinkClosureTests.SahteKapsam(),
                new AuditTenantLinkClosureTests.SayanSiteler([]),
                new SabitTercihler { Adres = sunucuAdresi, Port = httpsPort },
                new AuditTenantLinkClosureTests.KayitDefteri(),
                NullLogger<MailListPublicController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = httpContext }
            };
        }

        public YakalayanPostaci Postaci { get; } = new();

        public MailListPublicController Denetleyici { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_kok))
                {
                    Directory.Delete(_kok, recursive: true);
                }
            }
            catch (IOException)
            {
                // Geçici klasör silinemedi; ölçümü etkilemez.
            }
        }
    }

    /// <summary>Gönderilen postayı yakalar — gövde ve alıcı ölçülebilsin diye.</summary>
    private sealed class YakalayanPostaci : INotificationService
    {
        public int GonderimSayisi { get; private set; }

        public string SonGovde { get; private set; } = string.Empty;

        public string SonAlici { get; private set; } = string.Empty;

        public Task<bool> IsEmailConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task SendEmailToAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default)
        {
            GonderimSayisi++;
            SonAlici = toEmail;
            SonGovde = body;
            return Task.CompletedTask;
        }

        public Task<Core.Dtos.Notifications.NotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Core.Dtos.Notifications.NotificationSettingsDto> UpdateSettingsAsync(Core.Dtos.Notifications.UpdateNotificationSettingsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SendEmailAsync(string subject, string body, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SendWebhookAsync(string eventName, string body, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task TestAsync(Core.Dtos.Notifications.TestNotificationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

}

/// <summary>Panel tercihleri sabittir: adres ve port testin kurduğu değerdir.</summary>
internal sealed class SabitTercihler : IPanelPreferencesService
{
    public string Adres { get; set; } = string.Empty;

    public int Port { get; set; } = 8888;

    public Task<PanelPreferencesDto> GetAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new PanelPreferencesDto { ServerAddress = Adres, PreferredHttpsPort = Port });

    public Task<PanelPreferencesDto> UpdateAsync(UpdatePanelPreferencesRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

/// <summary>
/// Posta sunucusunun yerine geçen sahte: yalnız testin KURDUĞU listeleri ve DNS kayıtlarını döner.
/// Geri kalan her çağrı <c>NotSupportedException</c> atar — sessizce boş dönmek, ölçülmeyen bir
/// yolun "çalışıyor" görünmesine yol açardı.
/// </summary>
internal sealed class SahtePostaServisi : IMailService
{
    /// <summary>Sunucudaki dağıtım listeleri (abonelik doğrulaması bunun üzerinden ölçülür).</summary>
    public IReadOnlyCollection<MailListDto> Listeler { get; init; } = [];

    /// <summary>Alan adı için üretilen posta DNS kayıtları.</summary>
    public MailDnsRecordDto[] DnsKayitlari { get; init; } = [];

    public Task<MailWorkspaceDto> GetWorkspaceAsync(string? domain, CancellationToken cancellationToken = default)
        => Task.FromResult(new MailWorkspaceDto
        {
            Installed = true,
            Configured = true,
            SelectedDomain = domain ?? string.Empty,
            Lists = Listeler
        });

    public Task<MailWorkspaceDto> SetMailListRecipientsAsync(SetMailListRecipientsRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new MailWorkspaceDto());

    public Task<MailWorkspaceDto> ConfigureAsync(ConfigureMailRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MailWorkspaceDto> CreateDomainAsync(CreateMailDomainRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MailWorkspaceDto> DeleteDomainAsync(string domainName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MailWorkspaceDto> CreateAccountAsync(CreateMailAccountRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MailWorkspaceDto> DeleteAccountAsync(string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MailWorkspaceDto> SetAccountPasswordAsync(SetMailAccountPasswordRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MailWorkspaceDto> CreateAliasAsync(CreateMailAliasRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MailWorkspaceDto> DeleteAliasAsync(string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MailWorkspaceDto> CreateMailListAsync(CreateMailListRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MailWorkspaceDto> DeleteMailListAsync(string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MailWorkspaceDto> AddMailListAliasAsync(MailListAliasRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MailWorkspaceDto> RemoveMailListAliasAsync(MailListAliasRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MailWorkspaceDto> SetAccountQuotaAsync(SetMailAccountQuotaRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MailWorkspaceDto> SetDomainQuotaAsync(SetMailDomainQuotaRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MailAutoResponderDto> GetAutoResponderAsync(string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MailAutoResponderDto> SetAutoResponderAsync(SetMailAutoResponderRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MailDnsDto> GetDnsRecordsAsync(string domain, CancellationToken cancellationToken = default)
        => Task.FromResult(new MailDnsDto { Domain = domain, MailHost = "mail." + domain, Records = DnsKayitlari });
    public Task<MailDiagnosticsDto> GetDiagnosticsAsync(string domain, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MailCertificateDto> GetCertificateStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MailCertificateDto> IssueCertificateAsync(IssueMailCertificateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MailCertificateDto?> SyncCertificateAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> EnsureServerHostnameAsync(bool applyChanges = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MailSpamSettingsDto> GetSpamSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MailSpamSettingsDto> UpdateSpamSettingsAsync(UpdateMailSpamSettingsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MailListenerSnapshotDto> GetListenersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MailClientSettingsDto?> GetClientSettingsAsync(string domain, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MailOutboundLimitDto> GetOutboundLimitAsync(string domain, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MailOutboundLimitDto> SetOutboundLimitAsync(string domain, int messagesPerHour, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MailQueueDto> GetQueueAsync(int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MailQueueMessageDto?> GetQueueMessageAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> DeleteQueueMessageAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MailRelayDto> GetRelayAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MailRelayDto> SetRelayAsync(SetMailRelayRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
